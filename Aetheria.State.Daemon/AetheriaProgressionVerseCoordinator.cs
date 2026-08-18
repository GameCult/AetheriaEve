using Aetheria.State;
using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal sealed class AetheriaProgressionVerseView
{
    public required AetheriaProgressionSourceDocument Source { get; init; }
    public required AetheriaHangarState Hangar { get; init; }
    public AetheriaLoadoutTemplate? Loadout { get; init; }
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
        IEnumerable<string>? odinEndpoints)
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
                SourceId = "aetheria-configured-odin"
            };
            _discovery = CultMesh.CreateVerseDiscoveryClient(discoveryOptions);
            _remote = new CultMeshClient(new CultMeshClientOptions
            {
                RendezvousEndpoints = _odinEndpoints,
                Discovery = discoveryOptions,
                SubscriptionResponseTimeout = TimeSpan.FromSeconds(2)
            });
        }
    }

    public async Task<AetheriaProgressionSourceDocument> EnsureAndRefreshAsync(string now)
    {
        ThrowIfDisposed();
        var pointer = _node.MutableDocument<AetheriaProgressionSourceDocument>(AetheriaStateNode.ProgressionSourceKey);
        var existing = await pointer.ReadAsync().ConfigureAwait(false) ?? new AetheriaProgressionSourceDocument();
        var next = Clone(existing);
        next.OdinDiscoveryEndpoints = _odinEndpoints;
        next.DiscoveredAtUtc = now ?? "";

        var verses = new List<AetheriaProgressionVerseOption> { LocalOption() };
        if (_discovery == null)
        {
            next.Status = next.UsesLocalProgression
                ? AetheriaProgressionSourceStatuses.Local
                : AetheriaProgressionSourceStatuses.Unavailable;
            next.Diagnostic = next.UsesLocalProgression
                ? ""
                : "The selected Verse is unavailable because no Odin discovery endpoint is configured.";
        }
        else
        {
            try
            {
                using var catalog = CultMesh.CreateVerseCatalog();
                await _discovery.DiscoverAsync(catalog, _odinEndpoints, "cultmesh.v0").ConfigureAwait(false);
                verses.AddRange(catalog.Verses
                    .Where(verse => !string.Equals(verse.VerseId, _localVerseId, StringComparison.Ordinal))
                    .Select(ToOption)
                    .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(option => option.VerseId, StringComparer.Ordinal));
                var selectedIsAvailable = next.UsesLocalProgression ||
                    verses.Any(option => string.Equals(option.VerseId, next.SelectedVerseId, StringComparison.Ordinal));
                next.Status = selectedIsAvailable
                    ? (next.UsesLocalProgression ? AetheriaProgressionSourceStatuses.Local : AetheriaProgressionSourceStatuses.Ready)
                    : AetheriaProgressionSourceStatuses.Unavailable;
                next.Diagnostic = selectedIsAvailable ? "" : "The selected Verse was not advertised by the configured Odin.";
            }
            catch (Exception error) when (error is TimeoutException || error is InvalidOperationException)
            {
                foreach (var option in existing.AvailableVerses ?? Array.Empty<AetheriaProgressionVerseOption>())
                {
                    if (!string.Equals(option.VerseId, AetheriaProgressionSources.Local, StringComparison.Ordinal))
                        verses.Add(option);
                }
                next.Status = next.UsesLocalProgression
                    ? AetheriaProgressionSourceStatuses.Degraded
                    : AetheriaProgressionSourceStatuses.Unavailable;
                next.Diagnostic = $"Odin discovery failed: {error.Message}";
            }
        }

        if (!next.UsesLocalProgression &&
            !verses.Any(option => string.Equals(option.VerseId, next.SelectedVerseId, StringComparison.Ordinal)))
        {
            var previous = (existing.AvailableVerses ?? Array.Empty<AetheriaProgressionVerseOption>())
                .FirstOrDefault(option => string.Equals(option.VerseId, next.SelectedVerseId, StringComparison.Ordinal));
            verses.Add(previous ?? new AetheriaProgressionVerseOption
            {
                VerseId = next.SelectedVerseId,
                DisplayName = next.SelectedVerseId
            });
        }

        next.AvailableVerses = verses
            .Where(option => !string.IsNullOrWhiteSpace(option.VerseId))
            .GroupBy(option => option.VerseId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (!Equivalent(existing, next))
        {
            next.Revision = Math.Max(0, existing.Revision) + 1;
            next.UpdatedAtUtc = now ?? "";
            await pointer.ReplaceAsync(next).ConfigureAwait(false);
            await _node.FlushAsync().ConfigureAwait(false);
        }
        return next;
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
        await pointer.ReplaceAsync(next).ConfigureAwait(false);
        await _node.FlushAsync().ConfigureAwait(false);
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
            var hangar = remote.Hangar;
            AetheriaLoadoutTemplate? loadout = null;
            var selected = hangar.Ships?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(selected?.LoadoutTemplateKey))
                loadout = await _remote.ReadAsync<AetheriaLoadoutTemplate>(
                    target,
                    selected.LoadoutTemplateKey,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            AetheriaRuntimeCatalogSnapshot? catalog = null;
            try
            {
                catalog = await _remote.ReadAsync<AetheriaRuntimeCatalogSnapshot>(
                    target,
                    AetheriaStateNode.RuntimeCatalogKey.ToString(),
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Catalog labels are derived presentation; the Hangar remains usable without them.
            }
            return new AetheriaProgressionVerseView
            {
                Source = source,
                Hangar = hangar,
                Loadout = loadout,
                Catalog = catalog
            };
        }
        catch (Exception error) when (error is TimeoutException || error is InvalidOperationException)
        {
            source = await PersistAvailabilityAsync(
                source,
                AetheriaProgressionSourceStatuses.Unavailable,
                $"Selected Verse progression is unavailable: {error.Message}",
                now).ConfigureAwait(false);
            return UnavailableView(source);
        }
    }

    public async Task<EveCommandReceiptDocument> ForwardHangarInvocationAsync(
        EveSurfaceCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (request == null) throw new ArgumentNullException(nameof(request));
        var source = await _node.MutableDocument<AetheriaProgressionSourceDocument>(AetheriaStateNode.ProgressionSourceKey)
            .ReadAsync().ConfigureAwait(false);
        if (source == null || source.UsesLocalProgression)
            throw new InvalidOperationException("Remote Hangar forwarding requires a selected remote Verse.");
        if (_remote == null)
            throw new InvalidOperationException("No Odin discovery endpoint is configured for the selected Verse.");
        var remote = await ResolveRemoteProgressionAsync(source, cancellationToken).ConfigureAwait(false);
        var target = remote.Target;

        await _remote.SubmitDocumentAsync(
            target,
            AetheriaRuntimeVerseRecordKeys.EveCommandRecordPrefix + ":" + request.CommandId,
            request,
            _runtimeId,
            "aetheria-progression-router",
            cancellationToken).ConfigureAwait(false);

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
                return AddNavigationRoute(receipt, source);
            }
            catch (Exception error) when (error is TimeoutException || error is InvalidOperationException)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new TimeoutException($"Verse '{source.SelectedVerseId}' did not publish a Hangar receipt for '{request.CommandId}'.");
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
        AetheriaLoadoutTemplate? loadout = null;
        var selected = hangar.Ships?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(selected?.LoadoutTemplateKey))
            loadout = await _node.MutableDocument<AetheriaLoadoutTemplate>(new(selected.LoadoutTemplateKey))
                .ReadAsync().ConfigureAwait(false);
        return new AetheriaProgressionVerseView
        {
            Source = source,
            Hangar = hangar,
            Loadout = loadout,
            Catalog = _node.RuntimeCatalog().Latest()
        };
    }

    private async Task<AetheriaProgressionSourceDocument> PersistAvailabilityAsync(
        AetheriaProgressionSourceDocument current,
        string status,
        string diagnostic,
        string now)
    {
        if (string.Equals(current.Status, status, StringComparison.Ordinal) &&
            string.Equals(current.Diagnostic, diagnostic, StringComparison.Ordinal))
            return current;
        var next = Clone(current);
        next.Status = status;
        next.Diagnostic = diagnostic;
        next.Revision = Math.Max(0, current.Revision) + 1;
        next.UpdatedAtUtc = now ?? "";
        await _node.MutableDocument<AetheriaProgressionSourceDocument>(AetheriaStateNode.ProgressionSourceKey)
            .ReplaceAsync(next).ConfigureAwait(false);
        await _node.FlushAsync().ConfigureAwait(false);
        return next;
    }

    private static AetheriaProgressionVerseView UnavailableView(AetheriaProgressionSourceDocument source) => new()
    {
        Source = source,
        Hangar = new AetheriaHangarState
        {
            HangarId = source.SelectedVerseId,
            Ships = Array.Empty<AetheriaHangarShip>(),
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

    private static AetheriaProgressionVerseOption ToOption(CultMeshVerseDescriptor verse) => new()
    {
        VerseId = verse.VerseId,
        DisplayName = verse.DisplayName,
        AuthorityModel = verse.AuthorityModel.ToString(),
        TransportVersion = verse.Compatibility.TransportVersion,
        RulesHash = verse.Compatibility.RulesHash,
        Description = verse.Description ?? "",
        AuthorityRuntimeIds = verse.AuthorityRuntimeIds.ToArray(),
        DiscoveryEndpoints = verse.DiscoveryEndpoints.ToArray()
    };

    private async Task<RemoteProgression> ResolveRemoteProgressionAsync(
        AetheriaProgressionSourceDocument source,
        CancellationToken cancellationToken)
    {
        if (_remote == null)
            throw new InvalidOperationException("No Odin discovery endpoint is configured for remote progression.");
        var selected = (source.AvailableVerses ?? Array.Empty<AetheriaProgressionVerseOption>())
            .FirstOrDefault(option => string.Equals(option.VerseId, source.SelectedVerseId, StringComparison.Ordinal));
        var authorityRuntimeIds = (selected?.AuthorityRuntimeIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
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
                var hangar = await _remote.ReadAsync<AetheriaHangarState>(
                    target,
                    AetheriaStateNode.HangarKey.ToString(),
                    TimeSpan.FromSeconds(2),
                    cancellationToken).ConfigureAwait(false);
                return new RemoteProgression(target, hangar);
            }
            catch (Exception error) when (error is TimeoutException || error is InvalidOperationException)
            {
                failures.Add($"{authorityRuntimeId}: {error.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Verse '{source.SelectedVerseId}' advertises no reachable authority runtime serving " +
            $"the typed Hangar progression record. Probes: {string.Join(" | ", failures)}");
    }

    private sealed record RemoteProgression(CultMeshSessionTarget Target, AetheriaHangarState Hangar);

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
        AetheriaProgressionSourceDocument source)
    {
        if (receipt.Navigation == null || receipt.Navigation.RendezvousEndpoints.Length > 0)
            return receipt;
        var selected = (source.AvailableVerses ?? Array.Empty<AetheriaProgressionVerseOption>())
            .FirstOrDefault(option => string.Equals(option.VerseId, source.SelectedVerseId, StringComparison.Ordinal));
        var endpoints = selected?.DiscoveryEndpoints ?? Array.Empty<string>();
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
                endpoints));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AetheriaProgressionVerseCoordinator));
    }
}
