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
        AetheriaDaemonHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(options);
        server.OnCultNet<CultNetOperationRequestMessage>((request, peer) =>
            HandleAsync(request, peer, node, options));
    }

    private static async Task HandleAsync(
        CultNetOperationRequestMessage request,
        ICultNetSchemaServerPeer peer,
        AetheriaStateNode node,
        AetheriaDaemonHostOptions options)
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

            var intent = MessagePackSerializer.Deserialize<BrowserEveCommandIntent>(
                Convert.FromBase64String(request.Payload),
                MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance));
            Validate(request, intent);

            var commandId = string.IsNullOrWhiteSpace(request.MessageId)
                ? Guid.NewGuid().ToString("N")
                : request.MessageId;
            var commandRecordKey = new CultRecordKey(
                $"eve:command-invocations:{AetheriaRuntimeVerseRecordKeys.StableToken(commandId)}");
            var alreadyReceipted = await node.CommitAsync(async () =>
            {
                var receipted = node.Cache.Get<EveCommandReceiptDocument>(
                    AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(commandId)) != null;
                if (!receipted && node.Cache.Get<EveSurfaceCommandRequest>(commandRecordKey) == null)
                {
                    await node.Database.PutAsync(
                        commandRecordKey,
                        ToCommandRequest(request, intent, commandId)).ConfigureAwait(false);
                }
                return receipted;
            }).ConfigureAwait(false);

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

    private static void Validate(CultNetOperationRequestMessage request, BrowserEveCommandIntent intent)
    {
        if (!string.Equals(intent.Schema, EveSurfaceCommandRequest.SchemaId, StringComparison.Ordinal) ||
            !string.Equals(intent.Command, request.Operation, StringComparison.Ordinal))
            throw new InvalidOperationException("Aetheria Eve command envelope disagrees with its operation request.");
        if (string.IsNullOrWhiteSpace(intent.SurfaceId) || string.IsNullOrWhiteSpace(intent.ProviderId))
            throw new InvalidOperationException("Aetheria Eve command requires provider and surface identity.");
        if (string.IsNullOrWhiteSpace(request.SourceRuntimeId) ||
            !string.Equals(intent.ClientId, request.SourceRuntimeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Aetheria Eve command client identity is not bound to its CultNet caller.");
    }

    private static EveSurfaceCommandRequest ToCommandRequest(
        CultNetOperationRequestMessage request,
        BrowserEveCommandIntent intent,
        string commandId)
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
                intent.Command,
                request.PayloadSchema,
                CultMeshRouteHint.Automatic,
                commandId),
            payload,
            issuedAt,
            request.SourceRuntimeId!,
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
        [Key("type")] public string Type { get; set; } = "";
        [Key("schema")] public string Schema { get; set; } = "";
        [Key("providerId")] public string ProviderId { get; set; } = "";
        [Key("surfaceId")] public string SurfaceId { get; set; } = "";
        [Key("command")] public string Command { get; set; } = "";
        [Key("commandBoundary")] public string CommandBoundary { get; set; } = "";
        [Key("receiptSchema")] public string ReceiptSchema { get; set; } = "";
        [Key("payload")] public Dictionary<string, object?> Payload { get; set; } = new(StringComparer.Ordinal);
        [Key("issuedAt")] public string IssuedAt { get; set; } = "";
        [Key("clientId")] public string ClientId { get; set; } = "";
    }
}
