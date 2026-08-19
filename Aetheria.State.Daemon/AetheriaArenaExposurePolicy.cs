using Aetheria.State;
using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;
using GameCult.Caching;
using GameCult.Networking;

namespace Aetheria.State.Daemon;

internal enum AetheriaArenaExposureKind
{
    Inactive,
    ActiveValid,
    ActiveInvalid
}

internal sealed class AetheriaArenaExposureContext
{
    private AetheriaArenaExposureContext(
        AetheriaArenaExposureKind kind,
        AetheriaGameSessionState? session,
        AetheriaRuntimeArenaRosterDocument? roster,
        AetheriaRuntimeDaemonFrameDocument? frame)
    {
        Kind = kind;
        Session = session;
        Roster = roster;
        Frame = frame;
    }

    public AetheriaArenaExposureKind Kind { get; }
    public AetheriaGameSessionState? Session { get; }
    public AetheriaRuntimeArenaRosterDocument? Roster { get; }
    public AetheriaRuntimeDaemonFrameDocument? Frame { get; }

    public static AetheriaArenaExposureContext Inactive { get; } =
        new(AetheriaArenaExposureKind.Inactive, null, null, null);

    public static AetheriaArenaExposureContext Invalid(AetheriaGameSessionState session) =>
        new(AetheriaArenaExposureKind.ActiveInvalid, session, null, null);

    public static AetheriaArenaExposureContext Active(
        AetheriaGameSessionState session,
        AetheriaRuntimeArenaRosterDocument roster,
        AetheriaRuntimeDaemonFrameDocument frame) =>
        new(AetheriaArenaExposureKind.ActiveValid, session, roster, frame);
}

/// <summary>
/// Resolves one Arena exposure generation and owns record/body admission for it.
/// An active but incomplete generation is never equivalent to non-Arena play.
/// </summary>
internal static class AetheriaArenaExposurePolicy
{
    public static AetheriaArenaExposureContext Resolve(
        AetheriaStateNode node,
        AetheriaRuntimeDaemonFrameDocument? frame)
    {
        var session = node.Cache.Get<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey);
        if (session == null || !string.Equals(session.Mode, AetheriaGameModes.Arena, StringComparison.Ordinal))
            return AetheriaArenaExposureContext.Inactive;
        var roster = node.Cache.Get<AetheriaRuntimeArenaRosterDocument>(
            new CultRecordKey(AetheriaRuntimeArenaRosterDocument.RecordKey(session.SessionId)));
        return Resolve(session, roster, frame);
    }

    internal static AetheriaArenaExposureContext Resolve(
        AetheriaGameSessionState? session,
        AetheriaRuntimeArenaRosterDocument? roster,
        AetheriaRuntimeDaemonFrameDocument? frame)
    {
        if (session == null || !string.Equals(session.Mode, AetheriaGameModes.Arena, StringComparison.Ordinal))
            return AetheriaArenaExposureContext.Inactive;
        return roster?.IsActiveFor(session.SessionId, session.RunId) == true && FrameBelongsToSession(frame, session)
            ? AetheriaArenaExposureContext.Active(session, roster, frame!)
            : AetheriaArenaExposureContext.Invalid(session);
    }

    public static bool CanReadRecord(
        AetheriaStateNode node,
        string establishedRuntimeId,
        string recordKey,
        AetheriaArenaExposureContext context,
        string hangarPrincipalRuntimeId)
    {
        if (context.Kind == AetheriaArenaExposureKind.ActiveInvalid)
            return false;
        if (context.Kind == AetheriaArenaExposureKind.Inactive)
            return true;
        if (string.Equals(recordKey, AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(), StringComparison.Ordinal))
            return string.Equals(establishedRuntimeId, hangarPrincipalRuntimeId, StringComparison.Ordinal);
        if (recordKey.StartsWith(AetheriaRuntimeVerseRecordKeys.EveReceiptRecordPrefix + ":", StringComparison.Ordinal))
        {
            var commandId = recordKey.Substring(AetheriaRuntimeVerseRecordKeys.EveReceiptRecordPrefix.Length + 1);
            var envelope = node.Cache.Get<AetheriaHangarCommandEnvelopeDocument>(
                AetheriaRuntimeVerseRecordKeys.HangarCommandEnvelope(commandId));
            return envelope != null && string.Equals(envelope.ClientId, establishedRuntimeId, StringComparison.Ordinal);
        }
        return AetheriaRuntimeArenaObservationAdmission.CanReadRecord(
            establishedRuntimeId, recordKey, context.Roster!, context.Frame!.Run);
    }

    public static bool CanSubscribe(
        AetheriaStateNode node,
        string establishedRuntimeId,
        CultNetDatabaseSubscribeMessage request,
        AetheriaArenaExposureContext context,
        string hangarPrincipalRuntimeId)
    {
        if (string.IsNullOrWhiteSpace(establishedRuntimeId) ||
            !string.Equals(request.ConsumerRuntimeId, establishedRuntimeId, StringComparison.Ordinal))
            return false;
        return (request.RecordKeys ?? Array.Empty<string>())
                .All(recordKey => CanReadRecord(
                    node, establishedRuntimeId, recordKey, context, hangarPrincipalRuntimeId)) &&
            (request.BodyIds ?? Array.Empty<string>())
                .All(bodyId => context.Kind switch
                {
                    AetheriaArenaExposureKind.Inactive => true,
                    AetheriaArenaExposureKind.ActiveInvalid => false,
                    _ => AetheriaRuntimeArenaObservationAdmission.CanReadBody(
                        establishedRuntimeId, bodyId, context.Roster!, context.Frame!.Run)
                });
    }

    private static bool FrameBelongsToSession(
        AetheriaRuntimeDaemonFrameDocument? frame,
        AetheriaGameSessionState session) =>
        (frame?.Run?.Zones ?? Array.Empty<AetheriaRuntimeZoneSnapshotCommit>())
            .Any(zone => (zone.Entities ?? Array.Empty<AetheriaRuntimeEntitySnapshotCommit>()).Count > 0) &&
        !string.IsNullOrWhiteSpace(session.RunId) &&
        !string.IsNullOrWhiteSpace(session.RunRecordKey) &&
        string.Equals(frame!.RunRecordKey, session.RunRecordKey, StringComparison.Ordinal) &&
        string.Equals(frame.Run.RunId, session.RunId, StringComparison.Ordinal) &&
        string.Equals(frame.GameMode, session.Mode, StringComparison.Ordinal);
}
