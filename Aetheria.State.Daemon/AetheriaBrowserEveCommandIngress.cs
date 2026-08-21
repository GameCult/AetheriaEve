using Aetheria.State;
using GameCult.Aetheria.State.Verse;
using GameCult.Caching;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;
using MessagePack;
using MessagePack.Resolvers;
using System.Collections;
using System.Globalization;

namespace Aetheria.State.Daemon;

/// <summary>
/// Owns browser-safe Eve command ingress. Browsers submit typed operations; only the
/// daemon materializes canonical command documents for its existing acceptance loop.
/// </summary>
internal static class AetheriaBrowserEveCommandIngress
{
    public const string ServiceId = "aetheria.daemon.commands";

    public static void Register(
        ICultNetSchemaServer server,
        AetheriaStateNode node,
        AetheriaDaemonHostOptions options,
        Func<ICultNetSchemaServerPeer, string?> resolveEstablishedRuntimeId,
        Func<AetheriaRuntimeDaemonFrameDocument?> liveFrame)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolveEstablishedRuntimeId);
        ArgumentNullException.ThrowIfNull(liveFrame);
        server.OnCultNet<CultNetOperationRequestMessage>((request, peer) =>
            HandleAsync(request, peer, node, options, resolveEstablishedRuntimeId, liveFrame));
    }

    private static async Task HandleAsync(
        CultNetOperationRequestMessage request,
        ICultNetSchemaServerPeer peer,
        AetheriaStateNode node,
        AetheriaDaemonHostOptions options,
        Func<ICultNetSchemaServerPeer, string?> resolveEstablishedRuntimeId,
        Func<AetheriaRuntimeDaemonFrameDocument?> liveFrame)
    {
        try
        {
            if (!string.Equals(request.ServiceId, ServiceId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unknown Aetheria service '{request.ServiceId}'.");
            if (!string.IsNullOrWhiteSpace(request.TargetRuntimeId) &&
                !string.Equals(request.TargetRuntimeId, options.DaemonId, StringComparison.Ordinal))
                throw new InvalidOperationException("Aetheria command targets a different runtime.");
            if (!string.Equals(request.PayloadEncoding, "messagepack-base64", StringComparison.Ordinal))
                throw new InvalidOperationException("Aetheria Eve commands require messagepack-base64 payloads.");
            if (!string.Equals(request.PayloadSchema, EveSurfaceCommandRequest.SchemaId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsupported Aetheria command payload '{request.PayloadSchema}'.");

            var establishedRuntimeId = resolveEstablishedRuntimeId(peer);
            if (string.IsNullOrWhiteSpace(establishedRuntimeId))
                throw new InvalidOperationException("Aetheria Eve commands require an established CultMesh session identity.");

            var intent = MessagePackSerializer.Deserialize<BrowserEveCommandIntent>(
                Convert.FromBase64String(request.Payload),
                MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance));
            Validate(request, intent, establishedRuntimeId);

            var commandId = string.IsNullOrWhiteSpace(request.MessageId)
                ? Guid.NewGuid().ToString("N")
                : request.MessageId;
            var commandRecordKey = new CultRecordKey(
                $"eve:command-invocations:{AetheriaRuntimeVerseRecordKeys.StableToken(commandId)}");
            var commandRequest = ToCommandRequest(request, intent, commandId, establishedRuntimeId);
            var admissionFrame = liveFrame();
            AetheriaPublicEveCommandAdmission.RequireAuthorized(
                node,
                options,
                establishedRuntimeId,
                commandRequest,
                admissionFrame);
            var alreadyReceipted = node.Cache.Get<EveCommandReceiptDocument>(
                AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(commandId)) != null;
            if (!alreadyReceipted)
            {
                await AetheriaHangarCommandJournal.AdmitAsync(
                    node,
                    commandRecordKey,
                    commandRequest,
                    DateTimeOffset.UtcNow.ToString("O"),
                    options.VerseId,
                    options.DaemonId,
                    admissionFrame).ConfigureAwait(false);
            }

            peer.SendCultNet(Response(
                request,
                alreadyReceipted ? "accepted" : "queued",
                options.DaemonId,
                new Dictionary<string, string> { ["commandId"] = commandId }));
        }
        catch (Exception error)
        {
            peer.SendCultNet(Response(
                request,
                "denied",
                options.DaemonId,
                new Dictionary<string, string>(),
                error.Message));
        }
    }

    private static void Validate(
        CultNetOperationRequestMessage request,
        BrowserEveCommandIntent intent,
        string establishedRuntimeId)
    {
        if (!string.Equals(intent.Schema, EveSurfaceCommandRequest.SchemaId, StringComparison.Ordinal) ||
            !string.Equals(intent.Operation?.OperationId, request.Operation, StringComparison.Ordinal))
            throw new InvalidOperationException("Aetheria Eve command envelope disagrees with its operation request.");
        if (string.IsNullOrWhiteSpace(intent.SurfaceId) || string.IsNullOrWhiteSpace(intent.ProviderId))
            throw new InvalidOperationException("Aetheria Eve command requires provider and surface identity.");
        if (!string.Equals(request.SourceRuntimeId, establishedRuntimeId, StringComparison.Ordinal) ||
            !string.Equals(intent.ClientId, establishedRuntimeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Aetheria Eve command client identity is not bound to its CultNet caller.");
        if (!string.Equals(intent.CommandBoundary, request.ServiceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(intent.ReceiptSchema))
            throw new InvalidOperationException("Aetheria Eve command is not bound to its advertised command and receipt boundary.");
        if (string.IsNullOrWhiteSpace(intent.Operation?.SchemaId) ||
            string.IsNullOrWhiteSpace(intent.Operation?.IdempotencyKey))
            throw new InvalidOperationException("Aetheria Eve operation requires a payload schema and idempotency key.");
    }

    private static EveSurfaceCommandRequest ToCommandRequest(
        CultNetOperationRequestMessage request,
        BrowserEveCommandIntent intent,
        string commandId,
        string establishedRuntimeId)
    {
        var issuedAt = DateTimeOffset.TryParse(
            intent.IssuedAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsedIssuedAt)
            ? parsedIssuedAt
            : DateTimeOffset.UtcNow;
        var payload = new CultMeshOperationPayload((intent.Payload ?? new Dictionary<string, object?>())
            .ToDictionary(
                entry => entry.Key,
                entry => FormatPayloadValue(entry.Value),
                StringComparer.Ordinal));
        return new EveSurfaceCommandRequest(
            intent.ProviderId,
            intent.SurfaceId,
            new CultMeshOperationInvocationDescriptor(
                intent.Operation.OperationId,
                intent.Operation.SchemaId,
                new CultMeshRouteHint(
                    CultMeshLocalityKind.Network,
                    $"browser source-version {intent.Operation.RouteHint?.SourceVersion ?? 0}"),
                intent.Operation.IdempotencyKey),
            payload,
            issuedAt,
            establishedRuntimeId,
            intent.CommandBoundary,
            intent.ReceiptSchema);
    }

    private static string FormatPayloadValue(object? value)
    {
        return value switch
        {
            null => "",
            string text => text,
            bool boolean => boolean ? "true" : "false",
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            IEnumerable sequence => string.Join(",", sequence.Cast<object?>().Select(FormatPayloadValue)),
            _ => throw new InvalidOperationException(
                $"Eve command payload field type '{value.GetType().FullName}' is not a portable scalar or sequence.")
        };
    }

    private static CultNetOperationResponseMessage Response(
        CultNetOperationRequestMessage request,
        string status,
        string daemonId,
        IReadOnlyDictionary<string, string> payload,
        string? diagnostic = null)
    {
        return new CultNetOperationResponseMessage
        {
            MessageId = string.IsNullOrWhiteSpace(request.MessageId) ? Guid.NewGuid().ToString("N") : request.MessageId,
            ServiceId = request.ServiceId,
            Operation = request.Operation,
            Status = status,
            PayloadSchema = "gamecult.eve.command_ingress_receipt.v1",
            PayloadEncoding = "messagepack-base64",
            Payload = Convert.ToBase64String(MessagePackSerializer.Serialize(payload)),
            Diagnostics = string.IsNullOrWhiteSpace(diagnostic) ? Array.Empty<string>() : [diagnostic],
            SourceRuntimeId = daemonId
        };
    }

    [MessagePackObject(AllowPrivate = true)]
    internal sealed class BrowserEveCommandIntent
    {
        [Key("schema")] public string Schema { get; set; } = "";
        [Key("providerId")] public string ProviderId { get; set; } = "";
        [Key("surfaceId")] public string SurfaceId { get; set; } = "";
        [Key("operation")] public BrowserEveOperationIntent Operation { get; set; } = new();
        [Key("commandBoundary")] public string CommandBoundary { get; set; } = "";
        [Key("receiptSchema")] public string ReceiptSchema { get; set; } = "";
        [Key("payload")] public Dictionary<string, object?> Payload { get; set; } = new(StringComparer.Ordinal);
        [Key("issuedAt")] public string IssuedAt { get; set; } = "";
        [Key("clientId")] public string ClientId { get; set; } = "";
    }

    [MessagePackObject(AllowPrivate = true)]
    internal sealed class BrowserEveOperationIntent
    {
        [Key("operationId")] public string OperationId { get; set; } = "";
        [Key("schemaId")] public string SchemaId { get; set; } = "";
        [Key("idempotencyKey")] public string IdempotencyKey { get; set; } = "";
        [Key("routeHint")] public BrowserEveRouteHint RouteHint { get; set; } = new();
    }

    [MessagePackObject(AllowPrivate = true)]
    internal sealed class BrowserEveRouteHint
    {
        [Key("sourceVersion")] public long SourceVersion { get; set; }
    }
}
