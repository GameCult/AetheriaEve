using Aetheria.State;
using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;
using GameCult.Networking.WebSockets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal sealed class AetheriaProgressionVerseView
{
    public required long ProjectionGeneration { get; init; }
    public required AetheriaProgressionSourceDocument Source { get; init; }
    public required string AuthorityRuntimeId { get; init; }
    public required string AssetVerseId { get; init; }
    public required string AssetProviderId { get; init; }
    public required string AssetManifestRecordRef { get; init; }
    public required string[] AssetRendezvousEndpoints { get; init; }
    public required AetheriaHangarState Hangar { get; init; }
    public required AetheriaHangarDraftState Draft { get; init; }
    public AetheriaRuntimeLoadoutTemplateCommit? Loadout { get; init; }
    public AetheriaRuntimeCatalogSnapshot? Catalog { get; init; }
}

internal sealed class AetheriaProgressionVerseCoordinator : IDisposable
{
    private readonly AetheriaStateNode _node;
    private readonly string _runtimeId;
    private readonly string _localVerseId;
    private readonly string[] _odinEndpoints;
    private readonly CultMeshVerseDiscoveryClient? _discovery;
    private readonly CultMeshClient? _remote;
    private bool _disposed;

    public AetheriaProgressionVerseCoordinator(
        AetheriaStateNode node,
        string runtimeId,
        string localVerseId,
        IEnumerable<string>? odinEndpoints,
        CultMeshAuthorityTrustPolicy authorityTrust)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _runtimeId = string.IsNullOrWhiteSpace(runtimeId) ? "aetheria-progression-router" : runtimeId.Trim();
        _localVerseId = string.IsNullOrWhiteSpace(localVerseId) ? "aetheria.local" : localVerseId.Trim();
        _odinEndpoints = (odinEndpoints ?? Array.Empty<string>())
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Select(endpoint => endpoint.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(endpoint => endpoint, StringComparer.Ordinal)
            .ToArray();
        if (_odinEndpoints.Length > 0)
        {
            var discoveryOptions = new CultMeshVerseDiscoveryClientOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                ResponseTimeout = TimeSpan.FromSeconds(2),
                SourceId = "aetheria-configured-odin",
                CreateClientForEndpoint = CreateDiscoveryClient
            };
            _discovery = CultMesh.CreateVerseDiscoveryClient(discoveryOptions);
            _remote = new CultMeshClient(new CultMeshClientOptions
            {
                RendezvousEndpoints = _odinEndpoints,
                Discovery = discoveryOptions,
                Sessions = new CultMeshSessionManagerOptions
                {
                    RuntimeId = _runtimeId,
                    Trust = authorityTrust ?? throw new ArgumentNullException(nameof(authorityTrust))
                },
                Connectors = new ICultMeshTransportConnector[]
                {
                    new CultMeshTcpSchemaTransportConnector(),
                    new CultMeshUriSchemaTransportConnector(
                        "cultnet-websocket",
                        new[] { "ws", "wss" },
                        _ => new CultNetWebSocketSchemaClient())
                },
                SubscriptionResponseTimeout = TimeSpan.FromSeconds(2)
            });
        }
    }

    private static ICultNetSchemaClient CreateDiscoveryClient(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)))
        {
            return new CultNetWebSocketSchemaClient();
        }
        return CultNetSchemaClients.CreateForEndpoint(endpoint);
    }

    public async Task<AetheriaProgressionSourceDocument> EnsureAndRefreshAsync(string now)
    {
        ThrowIfDisposed();
        var pointer = _node.MutableDocument<AetheriaProgressionSourceDocument>(AetheriaStateNode.ProgressionSourceKey);
        var existing = await pointer.ReadAsync().ConfigureAwait(false) ?? new AetheriaProgressionSourceDocument();
        var verses = new List<AetheriaProgressionVerseOption> { LocalOption() };
        var discoveryAttempted = _discovery != null;
        var discoverySucceeded = false;
        var discoveryDiagnostic = "";
        if (_discovery == null)
        {
            discoveryDiagnostic = "The selected Verse is unavailable because no Odin discovery endpoint is configured.";
        }
        else
        {
            var discovered = new List<CultMeshVerseDescriptor>();
            var discoveryFailures = new List<string>();
            foreach (var endpoint in _odinEndpoints)
            {
                try
                {
                    using var catalog = CultMesh.CreateVerseCatalog();
                    await _discovery.DiscoverAsync(catalog, new[] { endpoint }, "cultmesh.v0").ConfigureAwait(false);
                    discovered.AddRange(catalog.Verses);
                }
                catch (Exception error) when (IsRemoteAvailabilityFailure(error))
                {
                    discoveryFailures.Add($"{endpoint}: {error.Message}");
                }
            }
            if (discovered.Count > 0)
            {
                verses.AddRange(ToOptions(discovered)
                    .Where(option => !string.Equals(option.VerseId, _localVerseId, StringComparison.Ordinal))
                    .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(option => option.VerseId, StringComparer.Ordinal));
                discoverySucceeded = true;
            }
            else
            {
                foreach (var option in existing.AvailableVerses ?? Array.Empty<AetheriaProgressionVerseOption>())
                {
                    if (!string.Equals(option.VerseId, AetheriaProgressionSources.Local, StringComparison.Ordinal))
                        verses.Add(option);
                }
                discoveryDiagnostic = discoveryFailures.Count == 0
                    ? "Odin discovery returned no Verse advertisements."
                    : $"Odin discovery failed: {string.Join("; ", discoveryFailures)}";
            }
        }
        return await _node.CommitAsync(async () =>
        {
            // Discovery is slow I/O. Merge its result into the latest committed selection rather
            // than allowing the stale pre-discovery snapshot to become a second selection writer.
            var latest = await pointer.ReadAsync().ConfigureAwait(false) ?? existing;
            var next = MergeDiscovery(
                latest,
                verses,
                discoveryAttempted,
                discoverySucceeded,
                discoveryDiagnostic,
                now);
            if (Equivalent(latest, next))
                return latest;
            next.Revision = Math.Max(0, latest.Revision) + 1;
            next.UpdatedAtUtc = now ?? "";
            await pointer.ReplaceAsync(next).ConfigureAwait(false);
            return next;
        }).ConfigureAwait(false);
    }

    public async Task<AetheriaProgressionSourceDocument> SelectAsync(string verseId, string now)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(verseId))
            throw new ArgumentException("A Verse selection is required.", nameof(verseId));
        var pointer = _node.MutableDocument<AetheriaProgressionSourceDocument>(AetheriaStateNode.ProgressionSourceKey);
        var current = await pointer.ReadAsync().ConfigureAwait(false)
            ?? await EnsureAndRefreshAsync(now).ConfigureAwait(false);
        var selected = verseId.Trim();
        if (!(current.AvailableVerses ?? Array.Empty<AetheriaProgressionVerseOption>())
            .Any(option => string.Equals(option.VerseId, selected, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Verse '{selected}' is not advertised by the configured Odin.");
        }
        if (string.Equals(current.SelectedVerseId, selected, StringComparison.Ordinal))
            return current;

        var next = Clone(current);
        next.SelectedVerseId = selected;
        next.Status = string.Equals(selected, AetheriaProgressionSources.Local, StringComparison.Ordinal)
            ? AetheriaProgressionSourceStatuses.Local
            : AetheriaProgressionSourceStatuses.Ready;
        next.Diagnostic = "";
        next.Revision = Math.Max(0, current.Revision) + 1;
        next.UpdatedAtUtc = now ?? "";
        await _node.CommitAsync(() => pointer.ReplaceAsync(next)).ConfigureAwait(false);
        return next;
    }

    public async Task<AetheriaProgressionVerseView> ReadViewAsync(string now)
    {
        ThrowIfDisposed();
        var source = await EnsureAndRefreshAsync(now).ConfigureAwait(false);
        if (source.UsesLocalProgression)
            return await ReadLocalViewAsync(source).ConfigureAwait(false);
        if (_remote == null || !string.Equals(source.Status, AetheriaProgressionSourceStatuses.Ready, StringComparison.Ordinal))
            return UnavailableView(source);

        try
        {
            var remote = await ResolveRemoteProgressionAsync(source, CancellationToken.None).ConfigureAwait(false);
            var target = remote.Target;
            var projection = remote.Projection;
            return new AetheriaProgressionVerseView
            {
                ProjectionGeneration = projection.Generation,
                Source = source,
                AuthorityRuntimeId = target.AuthorityRuntimeId,
                AssetVerseId = projection.AssetVerseId,
                AssetProviderId = projection.AssetProviderId,
                AssetManifestRecordRef = projection.AssetManifestRecordRef,
                AssetRendezvousEndpoints = source.OdinDiscoveryEndpoints?.ToArray() ?? Array.Empty<string>(),
                Hangar = projection.Hangar,
                Draft = projection.Draft,
                Loadout = projection.Loadout,
                Catalog = projection.Catalog
            };
        }
        catch (Exception error) when (IsRemoteAvailabilityFailure(error))
        {
            source = await PersistAvailabilityAsync(
                source,
                AetheriaProgressionSourceStatuses.Unavailable,
                $"Selected Verse progression is unavailable: {error.Message}",
                now).ConfigureAwait(false);
            return UnavailableView(source);
        }
    }

    public async Task<AetheriaProgressionCommandRouteDocument> ResolveOrPinForwardingRouteAsync(
        EveSurfaceCommandRequest request,
        string payloadHash,
        string expectedVerseId,
        string expectedAuthorityRuntimeId,
        long expectedProgressionSourceRevision,
        string now,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(expectedVerseId) ||
            string.Equals(expectedVerseId, AetheriaProgressionSources.Local, StringComparison.Ordinal))
            throw new InvalidOperationException("Remote Hangar forwarding requires an immutable remote Verse target.");
        if (string.IsNullOrWhiteSpace(expectedAuthorityRuntimeId))
            throw new InvalidOperationException("Remote Hangar forwarding requires the authority runtime that supplied the Hangar view.");
        if (expectedProgressionSourceRevision < 0)
            throw new InvalidOperationException("Remote Hangar forwarding requires the progression-source revision that authored the command.");
        var routeKey = AetheriaRuntimeVerseRecordKeys.ProgressionCommandRoute(request.CommandId);
        var forwardedRequest = CreateForwardedRequest(request, payloadHash, _runtimeId);
        var forwardedInvocationHash = EveCommandInvocationHash.Compute(forwardedRequest);
        var existing = await _node.MutableDocument<AetheriaProgressionCommandRouteDocument>(routeKey)
            .ReadAsync().ConfigureAwait(false);
        if (existing != null)
        {
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal) ||
                !string.Equals(existing.ForwardedInvocationHash, forwardedInvocationHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Hangar command id '{request.CommandId}' was reused with a different payload.");
            return existing;
        }

        var source = await _node.MutableDocument<AetheriaProgressionSourceDocument>(AetheriaStateNode.ProgressionSourceKey)
            .ReadAsync().ConfigureAwait(false);
        if (source == null)
            throw new InvalidOperationException("Remote Hangar forwarding requires the progression source catalog.");
        if (expectedProgressionSourceRevision > source.Revision)
            throw new InvalidOperationException("The Hangar command names a progression-source revision that has not been published.");
        if (_remote == null)
            throw new InvalidOperationException("No Odin discovery endpoint is configured for the targeted Verse.");
        var targetedSource = Clone(source);
        targetedSource.SelectedVerseId = expectedVerseId.Trim();
        var remote = await ResolveRemoteProgressionAsync(
            targetedSource,
            cancellationToken,
            expectedAuthorityRuntimeId.Trim()).ConfigureAwait(false);
        var route = new AetheriaProgressionCommandRouteDocument
        {
            CommandId = request.CommandId,
            PayloadHash = payloadHash,
            ForwardedInvocationHash = forwardedInvocationHash,
            VerseId = remote.Target.VerseId,
            AuthorityRuntimeId = remote.Target.AuthorityRuntimeId,
            ProgressionSourceRevision = expectedProgressionSourceRevision,
            OdinDiscoveryEndpoints = (source.OdinDiscoveryEndpoints ?? Array.Empty<string>()).ToArray(),
            CreatedAtUtc = now ?? ""
        };
        return await _node.CommitAsync(async () =>
        {
            var committed = await _node.MutableDocument<AetheriaProgressionCommandRouteDocument>(routeKey)
                .ReadAsync().ConfigureAwait(false);
            if (committed != null)
            {
                if (!string.Equals(committed.PayloadHash, payloadHash, StringComparison.Ordinal) ||
                    !string.Equals(committed.ForwardedInvocationHash, forwardedInvocationHash, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Hangar command id '{request.CommandId}' was reused with a different payload.");
                return committed;
            }
            await _node.MutableDocument<AetheriaProgressionCommandRouteDocument>(routeKey)
                .ReplaceAsync(route).ConfigureAwait(false);
            return route;
        }).ConfigureAwait(false);
    }

    public async Task<EveCommandReceiptDocument> ForwardHangarInvocationAsync(
        EveSurfaceCommandRequest request,
        AetheriaProgressionCommandRouteDocument route,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (route == null) throw new ArgumentNullException(nameof(route));
        if (!string.Equals(route.CommandId, request.CommandId, StringComparison.Ordinal))
            throw new InvalidOperationException("Pinned progression route does not belong to the Hangar command.");
        if (_remote == null)
            throw new InvalidOperationException("No Odin discovery endpoint is configured for the pinned Verse.");
        var target = new CultMeshSessionTarget(route.VerseId, route.AuthorityRuntimeId);
        var forwardedRequest = CreateForwardedRequest(request, route.PayloadHash, _runtimeId);
        if (!string.Equals(EveCommandInvocationHash.Compute(forwardedRequest), route.ForwardedInvocationHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Pinned progression route does not describe the forwarded command envelope.");

        await _remote.SubmitDocumentAsync(
            target,
            AetheriaRuntimeVerseRecordKeys.EveCommandRecordPrefix + ":" + request.CommandId,
            forwardedRequest,
            _runtimeId,
            "aetheria-progression-router",
            cancellationToken).ConfigureAwait(false);

        if (string.Equals(
                Environment.GetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_RECEIPT_COMMAND_ID"),
                request.CommandId,
                StringComparison.Ordinal) &&
            int.TryParse(
                Environment.GetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_RECEIPT_MS"),
                out var developmentDelayMs) &&
            developmentDelayMs > 0)
        {
            await Task.Delay(developmentDelayMs, cancellationToken).ConfigureAwait(false);
        }

        var receiptKey = AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(request.CommandId).ToString();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var receipt = await _remote.ReadAsync<EveCommandReceiptDocument>(
                    target,
                    receiptKey,
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken).ConfigureAwait(false);
                ValidateRemoteReceipt(request, route, receipt);
                return AddNavigationRoute(receipt, route);
            }
            catch (Exception error) when (IsRemoteAvailabilityFailure(error))
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new TimeoutException($"Verse '{route.VerseId}' did not publish a Hangar receipt for '{request.CommandId}'.");
    }

    public async Task<AetheriaHangarProjectionDocument> ReadProjectionAtLeastAsync(
        AetheriaProgressionCommandRouteDocument route,
        long minimumGeneration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (route == null) throw new ArgumentNullException(nameof(route));
        if (minimumGeneration <= 0)
            throw new InvalidDataException("Remote Hangar finality requires a positive projection generation.");
        if (_remote == null)
            throw new InvalidOperationException("No Odin discovery endpoint is configured for the pinned Verse.");

        var target = new CultMeshSessionTarget(route.VerseId, route.AuthorityRuntimeId);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var projection = await _remote.ReadAsync<AetheriaHangarProjectionDocument>(
                    target,
                    AetheriaRuntimeVerseRecordKeys.HangarProjection.ToString(),
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken).ConfigureAwait(false);
                ValidateProjection(target, projection);
                if (projection.Generation >= minimumGeneration)
                    return projection;
            }
            catch (Exception error) when (IsRemoteAvailabilityFailure(error))
            {
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Verse '{route.VerseId}' did not publish Hangar projection generation {minimumGeneration}.");
    }

    internal AetheriaProgressionVerseView CreateRemoteView(
        AetheriaProgressionSourceDocument source,
        AetheriaProgressionCommandRouteDocument route,
        AetheriaHangarProjectionDocument projection)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (route == null) throw new ArgumentNullException(nameof(route));
        if (projection == null) throw new ArgumentNullException(nameof(projection));
        var target = new CultMeshSessionTarget(route.VerseId, route.AuthorityRuntimeId);
        ValidateProjection(target, projection);
        return new AetheriaProgressionVerseView
        {
            ProjectionGeneration = projection.Generation,
            Source = source,
            AuthorityRuntimeId = route.AuthorityRuntimeId,
            AssetVerseId = projection.AssetVerseId,
            AssetProviderId = projection.AssetProviderId,
            AssetManifestRecordRef = projection.AssetManifestRecordRef,
            AssetRendezvousEndpoints = source.OdinDiscoveryEndpoints?.ToArray() ?? _odinEndpoints,
            Hangar = projection.Hangar,
            Draft = projection.Draft,
            Loadout = projection.Loadout,
            Catalog = projection.Catalog
        };
    }

    internal static void ValidateRemoteReceipt(
        EveSurfaceCommandRequest request,
        AetheriaProgressionCommandRouteDocument route,
        EveCommandReceiptDocument receipt)
    {
        if (receipt == null)
            throw new InvalidDataException("The progression authority returned an empty Hangar receipt.");
        if (!string.Equals(receipt.Schema, EveCommandReceiptDocument.SchemaId, StringComparison.Ordinal) ||
            !string.Equals(receipt.CommandId, request.CommandId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Command, request.Command, StringComparison.Ordinal) ||
            !string.Equals(receipt.ProviderId, request.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(receipt.SurfaceId, request.SurfaceId, StringComparison.Ordinal) ||
            !string.Equals(receipt.Authority, route.AuthorityRuntimeId, StringComparison.Ordinal) ||
            !string.Equals(receipt.InvocationHash, route.ForwardedInvocationHash, StringComparison.Ordinal) ||
            receipt.SourceVersion <= 0 ||
            !(string.Equals(receipt.State, "accepted", StringComparison.Ordinal) ||
              string.Equals(receipt.State, "denied", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Verse '{route.VerseId}' returned a receipt that does not finalize the pinned Hangar command envelope.");
        }
        if (receipt.Navigation != null)
        {
            if (!string.Equals(receipt.Navigation.VerseId, route.VerseId, StringComparison.Ordinal) ||
                !string.Equals(receipt.Navigation.AuthorityRuntimeId, route.AuthorityRuntimeId, StringComparison.Ordinal) ||
                !string.Equals(receipt.Navigation.ProviderId, receipt.ProviderId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(receipt.Navigation.SurfaceId))
            {
                throw new InvalidDataException(
                    $"Verse '{route.VerseId}' returned a Hangar navigation target owned by another Verse or provider.");
            }
        }
    }

    internal static EveCommandReceiptDocument ReEnvelopeForLocalClient(
        EveSurfaceCommandRequest request,
        EveCommandReceiptDocument remoteReceipt,
        AetheriaProgressionCommandRouteDocument route,
        string localAuthorityRuntimeId)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (remoteReceipt == null) throw new ArgumentNullException(nameof(remoteReceipt));
        if (string.IsNullOrWhiteSpace(localAuthorityRuntimeId))
            throw new ArgumentException("The local receipt authority is required.", nameof(localAuthorityRuntimeId));
        var localAuthority = localAuthorityRuntimeId.Trim();
        return new EveCommandReceiptDocument(
            $"receipt:{request.CommandId}:{remoteReceipt.State}:via:{localAuthority}",
            request.CommandId,
            request.Command,
            remoteReceipt.State,
            "Aetheria Progression Router",
            localAuthority,
            request.ProviderId,
            request.SurfaceId,
            remoteReceipt.Message,
            remoteReceipt.IssuedAtUtc,
            remoteReceipt.SourceVersion,
            remoteReceipt.Navigation,
            route.PayloadHash);
    }

    internal static EveSurfaceCommandRequest CreateForwardedRequest(
        EveSurfaceCommandRequest request,
        string originalInvocationHash,
        string delegatingRuntimeId)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(originalInvocationHash))
            throw new ArgumentException("The original invocation hash is required.", nameof(originalInvocationHash));
        if (string.IsNullOrWhiteSpace(delegatingRuntimeId))
            throw new ArgumentException("The delegating runtime is required.", nameof(delegatingRuntimeId));
        var runtimeId = delegatingRuntimeId.Trim();
        return new EveSurfaceCommandRequest(
            request.Schema,
            request.ProviderId,
            request.SurfaceId,
            request.OperationRecord,
            request.PayloadFields,
            request.IssuedAt,
            runtimeId,
            request.CommandBoundary,
            request.ReceiptSchema,
            new EveCommandDelegationRecord(
                originalInvocationHash,
                request.ClientId,
                runtimeId));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _remote?.Dispose();
    }

    private async Task<AetheriaProgressionVerseView> ReadLocalViewAsync(AetheriaProgressionSourceDocument source)
    {
        var hangar = await _node.MutableDocument<AetheriaHangarState>(AetheriaStateNode.HangarKey)
            .ReadAsync().ConfigureAwait(false) ?? new AetheriaHangarState();
        var draft = await AetheriaDaemonHangarCoordinator.EnsureDraftAsync(_node, hangar, DateTimeOffset.UtcNow.ToString("O"))
            .ConfigureAwait(false);
        AetheriaLoadoutTemplate? loadout = null;
        var selected = (hangar.Ships ?? Array.Empty<AetheriaHangarShip>()).FirstOrDefault(ship =>
            string.Equals(ship.ShipId, draft.SelectedShipId, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(selected?.LoadoutTemplateKey))
            loadout = await _node.MutableDocument<AetheriaLoadoutTemplate>(new(selected.LoadoutTemplateKey))
                .ReadAsync().ConfigureAwait(false);
        return new AetheriaProgressionVerseView
        {
            ProjectionGeneration = Math.Max(1, _node.Cache.Get<AetheriaHangarProjectionDocument>(
                AetheriaRuntimeVerseRecordKeys.HangarProjection)?.Generation ?? 1),
            Source = source,
            AuthorityRuntimeId = _runtimeId,
            AssetVerseId = _localVerseId,
            AssetProviderId = AetheriaRuntimeProviderIdentity.ProviderId,
            AssetManifestRecordRef = AetheriaRuntimeVerseRecordKeys.EveAssetCatalog.ToString(),
            AssetRendezvousEndpoints = Array.Empty<string>(),
            Hangar = hangar,
            Draft = draft,
            Loadout = loadout == null ? null : AetheriaRuntimeStateMapper.ToRuntimeLoadoutTemplate(loadout),
            Catalog = _node.RuntimeCatalog().Latest()
        };
    }

    private async Task<AetheriaProgressionSourceDocument> PersistAvailabilityAsync(
        AetheriaProgressionSourceDocument current,
        string status,
        string diagnostic,
        string now)
    {
        return await _node.CommitAsync(async () =>
        {
            var pointer = _node.MutableDocument<AetheriaProgressionSourceDocument>(AetheriaStateNode.ProgressionSourceKey);
            var latest = await pointer.ReadAsync().ConfigureAwait(false) ?? current;
            // A failed read of Verse A cannot mark Verse B unavailable after the player switches.
            if (!string.Equals(latest.SelectedVerseId, current.SelectedVerseId, StringComparison.Ordinal) ||
                (string.Equals(latest.Status, status, StringComparison.Ordinal) &&
                 string.Equals(latest.Diagnostic, diagnostic, StringComparison.Ordinal)))
                return latest;
            var next = Clone(latest);
            next.Status = status;
            next.Diagnostic = diagnostic;
            next.Revision = Math.Max(0, latest.Revision) + 1;
            next.UpdatedAtUtc = now ?? "";
            await pointer.ReplaceAsync(next).ConfigureAwait(false);
            return next;
        }).ConfigureAwait(false);
    }

    private static AetheriaProgressionVerseView UnavailableView(AetheriaProgressionSourceDocument source) => new()
    {
        ProjectionGeneration = 0,
        Source = source,
        AuthorityRuntimeId = "",
        AssetVerseId = "",
        AssetProviderId = "",
        AssetManifestRecordRef = "",
        AssetRendezvousEndpoints = Array.Empty<string>(),
        Hangar = new AetheriaHangarState
        {
            HangarId = source.SelectedVerseId,
            Ships = Array.Empty<AetheriaHangarShip>(),
            UpdatedAtUtc = source.UpdatedAtUtc
        },
        Draft = new AetheriaHangarDraftState
        {
            SelectedMode = AetheriaGameModes.Terminus,
            ActiveView = AetheriaHangarViews.Overview,
            UpdatedAtUtc = source.UpdatedAtUtc
        }
    };

    private AetheriaProgressionVerseOption LocalOption() => new()
    {
        VerseId = AetheriaProgressionSources.Local,
        DisplayName = "Local",
        AuthorityModel = "LocalDaemon",
        TransportVersion = "cultmesh.v0",
        RulesHash = CultMeshVerseDescriptor.ComputeRulesHash("aetheria", "runtime-world.v1"),
        Description = "Local moddable Aetheria progression owned by this daemon.",
        AuthorityRuntimeIds = new[] { _runtimeId }
    };

    private static IEnumerable<AetheriaProgressionVerseOption> ToOptions(
        IEnumerable<CultMeshVerseDescriptor> verses) =>
        verses.GroupBy(verse => verse.VerseId, StringComparer.Ordinal).Select(group =>
        {
            var ordered = group
                .OrderBy(verse => verse.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(verse => verse.Compatibility.RulesHash, StringComparer.Ordinal)
                .ToArray();
            var first = ordered[0];
            return new AetheriaProgressionVerseOption
            {
                VerseId = first.VerseId,
                DisplayName = first.DisplayName,
                AuthorityModel = first.AuthorityModel.ToString(),
                TransportVersion = first.Compatibility.TransportVersion,
                RulesHash = first.Compatibility.RulesHash,
                Description = first.Description ?? "",
                AuthorityRuntimeIds = ordered
                    .SelectMany(verse => verse.AuthorityRuntimeIds)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray(),
                DiscoveryEndpoints = ordered
                    .SelectMany(verse => verse.DiscoveryEndpoints)
                    .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(endpoint => endpoint, StringComparer.Ordinal)
                    .ToArray()
            };
        });

    private async Task<RemoteProgression> ResolveRemoteProgressionAsync(
        AetheriaProgressionSourceDocument source,
        CancellationToken cancellationToken,
        string requiredAuthorityRuntimeId = "")
    {
        if (_remote == null)
            throw new InvalidOperationException("No Odin discovery endpoint is configured for remote progression.");
        var selected = (source.AvailableVerses ?? Array.Empty<AetheriaProgressionVerseOption>())
            .FirstOrDefault(option => string.Equals(option.VerseId, source.SelectedVerseId, StringComparison.Ordinal));
        var advertisedAuthorityRuntimeIds = (selected?.AuthorityRuntimeIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var authorityRuntimeIds = string.IsNullOrWhiteSpace(requiredAuthorityRuntimeId)
            ? advertisedAuthorityRuntimeIds
            : new[] { requiredAuthorityRuntimeId.Trim() };
        if (authorityRuntimeIds.Length == 0)
            throw new InvalidOperationException(
                $"Verse '{source.SelectedVerseId}' does not advertise an authority runtime that can be probed for progression.");

        var failures = new List<string>();
        foreach (var authorityRuntimeId in authorityRuntimeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = new CultMeshSessionTarget(source.SelectedVerseId, authorityRuntimeId);
            try
            {
                var projection = await _remote.ReadAsync<AetheriaHangarProjectionDocument>(
                    target,
                    AetheriaRuntimeVerseRecordKeys.HangarProjection.ToString(),
                    TimeSpan.FromSeconds(2),
                    cancellationToken).ConfigureAwait(false);
                ValidateProjection(target, projection);
                return new RemoteProgression(target, projection);
            }
            catch (Exception error) when (IsRemoteAvailabilityFailure(error))
            {
                failures.Add($"{authorityRuntimeId}: {error.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Verse '{source.SelectedVerseId}' advertises no reachable authority runtime serving " +
            $"the typed Hangar progression record. Probes: {string.Join(" | ", failures)}");
    }

    private static void ValidateProjection(
        CultMeshSessionTarget target,
        AetheriaHangarProjectionDocument projection)
    {
        if (projection.Generation <= 0 ||
            !string.Equals(projection.AuthorityRuntimeId, target.AuthorityRuntimeId, StringComparison.Ordinal) ||
            !string.Equals(projection.AssetVerseId, target.VerseId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(projection.AssetProviderId) ||
            string.IsNullOrWhiteSpace(projection.AssetManifestRecordRef))
        {
            throw new InvalidOperationException(
                $"Verse '{target.VerseId}' authority '{target.AuthorityRuntimeId}' published an invalid Hangar projection.");
        }
    }

    private sealed record RemoteProgression(
        CultMeshSessionTarget Target,
        AetheriaHangarProjectionDocument Projection);

    private static bool IsRemoteAvailabilityFailure(Exception error) =>
        error is TimeoutException or InvalidOperationException or CultMeshSessionException;

    private static AetheriaProgressionSourceDocument Clone(AetheriaProgressionSourceDocument source) => new()
    {
        Name = source.Name,
        SelectedVerseId = string.IsNullOrWhiteSpace(source.SelectedVerseId) ? AetheriaProgressionSources.Local : source.SelectedVerseId,
        OdinDiscoveryEndpoints = source.OdinDiscoveryEndpoints?.ToArray() ?? Array.Empty<string>(),
        AvailableVerses = source.AvailableVerses?.ToArray() ?? Array.Empty<AetheriaProgressionVerseOption>(),
        Status = source.Status,
        Diagnostic = source.Diagnostic,
        Revision = source.Revision,
        DiscoveredAtUtc = source.DiscoveredAtUtc,
        UpdatedAtUtc = source.UpdatedAtUtc
    };

    private AetheriaProgressionSourceDocument MergeDiscovery(
        AetheriaProgressionSourceDocument latest,
        IEnumerable<AetheriaProgressionVerseOption> discoveredVerses,
        bool discoveryAttempted,
        bool discoverySucceeded,
        string discoveryDiagnostic,
        string now)
    {
        var next = Clone(latest);
        next.OdinDiscoveryEndpoints = _odinEndpoints;
        next.DiscoveredAtUtc = now ?? "";
        var verses = discoveredVerses
            .Where(option => !string.IsNullOrWhiteSpace(option.VerseId))
            .GroupBy(option => option.VerseId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var selectedIsAvailable = next.UsesLocalProgression ||
            verses.Any(option => string.Equals(option.VerseId, next.SelectedVerseId, StringComparison.Ordinal));

        if (!next.UsesLocalProgression && !selectedIsAvailable)
        {
            var previous = (latest.AvailableVerses ?? Array.Empty<AetheriaProgressionVerseOption>())
                .FirstOrDefault(option => string.Equals(option.VerseId, next.SelectedVerseId, StringComparison.Ordinal));
            verses.Add(previous ?? new AetheriaProgressionVerseOption
            {
                VerseId = next.SelectedVerseId,
                DisplayName = next.SelectedVerseId
            });
        }

        next.AvailableVerses = verses.ToArray();
        if (!discoveryAttempted)
        {
            next.Status = next.UsesLocalProgression
                ? AetheriaProgressionSourceStatuses.Local
                : AetheriaProgressionSourceStatuses.Unavailable;
            next.Diagnostic = next.UsesLocalProgression ? "" : discoveryDiagnostic;
        }
        else if (!discoverySucceeded)
        {
            next.Status = next.UsesLocalProgression
                ? AetheriaProgressionSourceStatuses.Degraded
                : AetheriaProgressionSourceStatuses.Unavailable;
            next.Diagnostic = discoveryDiagnostic;
        }
        else
        {
            next.Status = selectedIsAvailable
                ? (next.UsesLocalProgression ? AetheriaProgressionSourceStatuses.Local : AetheriaProgressionSourceStatuses.Ready)
                : AetheriaProgressionSourceStatuses.Unavailable;
            next.Diagnostic = selectedIsAvailable ? "" : "The selected Verse was not advertised by the configured Odin.";
        }
        return next;
    }

    private static bool Equivalent(AetheriaProgressionSourceDocument left, AetheriaProgressionSourceDocument right) =>
        string.Equals(left.SelectedVerseId, right.SelectedVerseId, StringComparison.Ordinal) &&
        string.Equals(left.Status, right.Status, StringComparison.Ordinal) &&
        string.Equals(left.Diagnostic, right.Diagnostic, StringComparison.Ordinal) &&
        (left.OdinDiscoveryEndpoints ?? Array.Empty<string>()).SequenceEqual(right.OdinDiscoveryEndpoints ?? Array.Empty<string>(), StringComparer.Ordinal) &&
        OptionsEquivalent(left.AvailableVerses, right.AvailableVerses);

    private static bool OptionsEquivalent(
        IReadOnlyList<AetheriaProgressionVerseOption>? left,
        IReadOnlyList<AetheriaProgressionVerseOption>? right)
    {
        left ??= Array.Empty<AetheriaProgressionVerseOption>();
        right ??= Array.Empty<AetheriaProgressionVerseOption>();
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].VerseId, right[index].VerseId, StringComparison.Ordinal) ||
                !string.Equals(left[index].DisplayName, right[index].DisplayName, StringComparison.Ordinal) ||
                !string.Equals(left[index].AuthorityModel, right[index].AuthorityModel, StringComparison.Ordinal) ||
                !string.Equals(left[index].TransportVersion, right[index].TransportVersion, StringComparison.Ordinal) ||
                !string.Equals(left[index].RulesHash, right[index].RulesHash, StringComparison.Ordinal) ||
                !string.Equals(left[index].Description, right[index].Description, StringComparison.Ordinal) ||
                !(left[index].AuthorityRuntimeIds ?? Array.Empty<string>()).SequenceEqual(
                    right[index].AuthorityRuntimeIds ?? Array.Empty<string>(), StringComparer.Ordinal) ||
                !(left[index].DiscoveryEndpoints ?? Array.Empty<string>()).SequenceEqual(
                    right[index].DiscoveryEndpoints ?? Array.Empty<string>(), StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    private static EveCommandReceiptDocument AddNavigationRoute(
        EveCommandReceiptDocument receipt,
        AetheriaProgressionCommandRouteDocument route)
    {
        if (receipt.Navigation == null || receipt.Navigation.RendezvousEndpoints.Length > 0)
            return receipt;
        var endpoints = route.OdinDiscoveryEndpoints?.Where(endpoint => !string.IsNullOrWhiteSpace(endpoint)).ToArray()
            ?? Array.Empty<string>();
        if (endpoints.Length == 0)
            return receipt;
        return new EveCommandReceiptDocument(
            receipt.ReceiptId,
            receipt.CommandId,
            receipt.Command,
            receipt.State,
            receipt.OwnerRepo,
            receipt.Authority,
            receipt.ProviderId,
            receipt.SurfaceId,
            receipt.Message,
            receipt.IssuedAtUtc,
            receipt.SourceVersion,
            new EveSurfaceNavigationTarget(
                receipt.Navigation.VerseId,
                receipt.Navigation.ProviderId,
                receipt.Navigation.SurfaceId,
                receipt.Navigation.SurfaceKind,
                endpoints,
                route.AuthorityRuntimeId),
            receipt.InvocationHash);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AetheriaProgressionVerseCoordinator));
    }
}
