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
        AetheriaRuntimeVerseAuthorityPolicyDocument? authorityPolicy,
        AetheriaRuntimeArenaRosterDocument? roster,
        AetheriaRuntimeDaemonFrameDocument? frame)
    {
        Kind = kind;
        Session = session;
        AuthorityPolicy = authorityPolicy;
        Roster = roster;
        Frame = frame;
    }

    public AetheriaArenaExposureKind Kind { get; }
    public AetheriaGameSessionState? Session { get; }
    public AetheriaRuntimeVerseAuthorityPolicyDocument? AuthorityPolicy { get; }
    public AetheriaRuntimeArenaRosterDocument? Roster { get; }
    public AetheriaRuntimeDaemonFrameDocument? Frame { get; }

    public static AetheriaArenaExposureContext Inactive { get; } =
        new(AetheriaArenaExposureKind.Inactive, null, null, null, null);

    public static AetheriaArenaExposureContext Invalid(
        AetheriaGameSessionState session,
        AetheriaRuntimeVerseAuthorityPolicyDocument? authorityPolicy) =>
        new(AetheriaArenaExposureKind.ActiveInvalid, session, authorityPolicy, null, null);

    public static AetheriaArenaExposureContext Active(
        AetheriaGameSessionState session,
        AetheriaRuntimeVerseAuthorityPolicyDocument authorityPolicy,
        AetheriaRuntimeArenaRosterDocument roster,
        AetheriaRuntimeDaemonFrameDocument frame) =>
        new(AetheriaArenaExposureKind.ActiveValid, session, authorityPolicy, roster, frame);
}

internal static class AetheriaDaemonFrameProvenance
{
    public static bool BelongsToSession(
        AetheriaRuntimeDaemonFrameDocument? frame,
        AetheriaGameSessionState? session,
        string hostRuntimeId) =>
        frame != null &&
        session != null &&
        frame.IsAuthoritative &&
        string.Equals(frame.StateSource, "daemon", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(hostRuntimeId) &&
        string.Equals(frame.DaemonId, hostRuntimeId, StringComparison.Ordinal) &&
        string.Equals(frame.SessionId, session.SessionId, StringComparison.Ordinal) &&
        (frame.Run?.Zones ?? Array.Empty<AetheriaRuntimeZoneSnapshotCommit>())
            .Any(zone => (zone.Entities ?? Array.Empty<AetheriaRuntimeEntitySnapshotCommit>()).Count > 0) &&
        !string.IsNullOrWhiteSpace(session.RunId) &&
        !string.IsNullOrWhiteSpace(session.RunRecordKey) &&
        string.Equals(frame.RunRecordKey, session.RunRecordKey, StringComparison.Ordinal) &&
        string.Equals(frame.Run!.RunId, session.RunId, StringComparison.Ordinal) &&
        string.Equals(frame.GameMode, session.Mode, StringComparison.Ordinal);
}

/// <summary>
/// Resolves one Arena exposure generation and owns record/body admission for it.
/// An active but incomplete generation is never equivalent to non-Arena play.
/// </summary>
internal static class AetheriaArenaExposurePolicy
{
    public static string Generation(AetheriaArenaExposureContext context) =>
        context.Kind switch
        {
            AetheriaArenaExposureKind.Inactive => "inactive",
            AetheriaArenaExposureKind.ActiveInvalid => string.Join(
                "\u001f",
                "invalid",
                context.Session?.SessionId ?? "",
                context.Session?.RunId ?? "",
                context.Session?.RunRecordKey ?? "",
                context.Session?.ModePolicyId ?? "",
                context.AuthorityPolicy?.PolicyId ?? "",
                context.AuthorityPolicy?.HostRuntimeId ?? "",
                context.AuthorityPolicy?.DefaultMode ?? ""),
            _ => string.Join(
                "\u001f",
                "active",
                context.Session!.SessionId,
                context.Session.RunId,
                context.Session.RunRecordKey,
                context.Session.ModePolicyId,
                context.AuthorityPolicy!.PolicyId,
                context.AuthorityPolicy.HostRuntimeId,
                context.AuthorityPolicy.DefaultMode,
                context.Roster!.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };

    public static AetheriaArenaExposureContext Resolve(
        AetheriaStateNode node,
        AetheriaRuntimeDaemonFrameDocument? frame,
        string hostRuntimeId)
    {
        var session = node.Cache.Get<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey);
        if (session == null || !string.Equals(session.Mode, AetheriaGameModes.Arena, StringComparison.Ordinal))
            return AetheriaArenaExposureContext.Inactive;
        var authorityPolicy = node.Cache.Get<AetheriaRuntimeVerseAuthorityPolicyDocument>(
            AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy);
        var roster = node.Cache.Get<AetheriaRuntimeArenaRosterDocument>(
            new CultRecordKey(AetheriaRuntimeArenaRosterDocument.RecordKey(session.SessionId)));
        return Resolve(session, authorityPolicy, roster, frame, hostRuntimeId);
    }

    internal static AetheriaArenaExposureContext Resolve(
        AetheriaGameSessionState? session,
        AetheriaRuntimeVerseAuthorityPolicyDocument? authorityPolicy,
        AetheriaRuntimeArenaRosterDocument? roster,
        AetheriaRuntimeDaemonFrameDocument? frame,
        string hostRuntimeId)
    {
        if (session == null || !string.Equals(session.Mode, AetheriaGameModes.Arena, StringComparison.Ordinal))
            return AetheriaArenaExposureContext.Inactive;
        return AetheriaRuntimeArenaOperationAdmission.IsServerAuthorityActive(
                session.Mode,
                session.ModePolicyId,
                authorityPolicy,
                hostRuntimeId) &&
            roster?.IsActiveFor(session.SessionId, session.RunId) == true &&
            AetheriaDaemonFrameProvenance.BelongsToSession(frame, session, hostRuntimeId)
                ? AetheriaArenaExposureContext.Active(session, authorityPolicy!, roster, frame!)
                : AetheriaArenaExposureContext.Invalid(session, authorityPolicy);
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

}

internal enum AetheriaGameplayExposureKind
{
    LocalOpen,
    ArenaValid,
    StarbridgeValid,
    ActiveInvalid
}

internal sealed class AetheriaStarbridgeExposureContext
{
    public AetheriaStarbridgeExposureContext(
        AetheriaGameSessionState session,
        AetheriaRuntimeVerseAuthorityPolicyDocument? authorityPolicy,
        IReadOnlyList<AetheriaRuntimeStarbridgePlayerSeatDocument> seats,
        AetheriaRuntimeDaemonFrameDocument? frame,
        bool isValid = false)
    {
        Session = session;
        AuthorityPolicy = authorityPolicy;
        Seats = seats ?? Array.Empty<AetheriaRuntimeStarbridgePlayerSeatDocument>();
        Frame = frame;
        IsValid = isValid;
    }

    public AetheriaGameSessionState Session { get; }
    public AetheriaRuntimeVerseAuthorityPolicyDocument? AuthorityPolicy { get; }
    public IReadOnlyList<AetheriaRuntimeStarbridgePlayerSeatDocument> Seats { get; }
    public AetheriaRuntimeDaemonFrameDocument? Frame { get; }
    public bool IsValid { get; }
}

internal sealed class AetheriaGameplayExposureContext
{
    private AetheriaGameplayExposureContext(
        AetheriaGameplayExposureKind kind,
        AetheriaArenaExposureContext? arena,
        AetheriaStarbridgeExposureContext? starbridge,
        string mode)
    {
        Kind = kind;
        Arena = arena;
        Starbridge = starbridge;
        Mode = mode ?? "";
    }

    public AetheriaGameplayExposureKind Kind { get; }
    public AetheriaArenaExposureContext? Arena { get; }
    public AetheriaStarbridgeExposureContext? Starbridge { get; }
    public string Mode { get; }

    public static AetheriaGameplayExposureContext LocalOpen { get; } =
        new(AetheriaGameplayExposureKind.LocalOpen, null, null, AetheriaGameModes.Terminus);

    public static AetheriaGameplayExposureContext Unsupported(string? mode) =>
        new(AetheriaGameplayExposureKind.ActiveInvalid, null, null, mode ?? "");

    public static AetheriaGameplayExposureContext FromArena(AetheriaArenaExposureContext context) =>
        new(
            context.Kind == AetheriaArenaExposureKind.ActiveValid
                ? AetheriaGameplayExposureKind.ArenaValid
                : AetheriaGameplayExposureKind.ActiveInvalid,
            context,
            null,
            context.Session?.Mode ?? AetheriaGameModes.Arena);

    public static AetheriaGameplayExposureContext FromStarbridge(
        AetheriaStarbridgeExposureContext context,
        bool valid) =>
        new(
            valid ? AetheriaGameplayExposureKind.StarbridgeValid : AetheriaGameplayExposureKind.ActiveInvalid,
            null,
            context,
            context.Session.Mode);
}

/// <summary>
/// Owns the authenticated gameplay observation boundary for every active mode.
/// Local Terminus remains open to established local peers; Arena and Starbridge are role-scoped.
/// </summary>
internal static class AetheriaGameplayExposurePolicy
{
    public static AetheriaGameplayExposureContext Resolve(
        AetheriaStateNode node,
        AetheriaRuntimeDaemonFrameDocument? frame,
        string hostRuntimeId)
    {
        var session = node.Cache.Get<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey);
        if (session == null)
            return AetheriaGameplayExposureContext.LocalOpen;
        var mode = AetheriaGameModes.Classify(session.Mode);
        if (mode == AetheriaGameModeKind.Terminus)
            return AetheriaGameplayExposureContext.LocalOpen;
        if (mode == AetheriaGameModeKind.Arena)
            return AetheriaGameplayExposureContext.FromArena(
                AetheriaArenaExposurePolicy.Resolve(node, frame, hostRuntimeId));
        if (mode != AetheriaGameModeKind.Starbridge)
            return AetheriaGameplayExposureContext.Unsupported(session.Mode);

        var policy = node.Cache.Get<AetheriaRuntimeVerseAuthorityPolicyDocument>(
            AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy);
        var seats = node.Documents<AetheriaRuntimeStarbridgePlayerSeatDocument>()
            .Where(seat => seat != null &&
                string.Equals(seat.SessionId, session.SessionId, StringComparison.Ordinal) &&
                string.Equals(seat.RunId, session.RunId, StringComparison.Ordinal))
            .OrderBy(seat => seat.SeatId, StringComparer.Ordinal)
            .ToArray();
        var connected = seats.Where(seat => string.Equals(
                seat.ConnectionState,
                AetheriaRuntimeStarbridgePlayerSeatConnectionStates.Connected,
                StringComparison.Ordinal))
            .ToArray();
        var valid = AetheriaRuntimeStarbridgeOperationAdmission.IsPilotInputActive(
                session.Mode, session.ModePolicyId, policy, hostRuntimeId) &&
            AetheriaDaemonFrameProvenance.BelongsToSession(frame, session, hostRuntimeId) &&
            connected.Count(seat => string.Equals(
                seat.Role, AetheriaRuntimeStarbridgePlayerSeatRoles.Commander, StringComparison.Ordinal)) == 1 &&
            connected.Count(seat =>
                string.Equals(seat.Role, AetheriaRuntimeStarbridgePlayerSeatRoles.Commander, StringComparison.Ordinal) &&
                string.Equals(seat.RuntimeId, hostRuntimeId, StringComparison.Ordinal)) == 1 &&
            connected.GroupBy(seat => seat.RuntimeId ?? "", StringComparer.Ordinal)
                .All(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() == 1) &&
            connected.Where(seat => string.Equals(seat.Role, AetheriaRuntimeStarbridgePlayerSeatRoles.Pilot, StringComparison.Ordinal))
                .All(seat => !string.IsNullOrWhiteSpace(seat.ControlledEntityId) &&
                    frame!.Run.TryResolveEntityId(seat.ControlledEntityId, out _));
        var context = new AetheriaStarbridgeExposureContext(session, policy, seats, frame, valid);
        return AetheriaGameplayExposureContext.FromStarbridge(context, valid);
    }

    public static string Generation(AetheriaGameplayExposureContext context) =>
        context.Kind switch
        {
            AetheriaGameplayExposureKind.LocalOpen => "local-open",
            AetheriaGameplayExposureKind.ArenaValid => "arena\u001f" + AetheriaArenaExposurePolicy.Generation(context.Arena!),
            AetheriaGameplayExposureKind.StarbridgeValid => StarbridgeGeneration("starbridge", context.Starbridge!),
            _ when context.Arena != null => "invalid-arena\u001f" + AetheriaArenaExposurePolicy.Generation(context.Arena),
            _ when context.Starbridge != null => StarbridgeGeneration("invalid-starbridge", context.Starbridge),
            _ => "active-invalid\u001f" + context.Mode
        };

    public static bool CanReadRecord(
        AetheriaStateNode node,
        string establishedRuntimeId,
        string recordKey,
        AetheriaGameplayExposureContext context,
        string hangarPrincipalRuntimeId)
    {
        if (string.IsNullOrWhiteSpace(establishedRuntimeId))
            return false;
        if (string.Equals(recordKey, AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(), StringComparison.Ordinal))
            return string.Equals(establishedRuntimeId, hangarPrincipalRuntimeId, StringComparison.Ordinal);
        if (recordKey.StartsWith(AetheriaRuntimeVerseRecordKeys.EveReceiptRecordPrefix + ":", StringComparison.Ordinal))
        {
            var commandId = recordKey.Substring(AetheriaRuntimeVerseRecordKeys.EveReceiptRecordPrefix.Length + 1);
            var envelope = node.Cache.Get<AetheriaHangarCommandEnvelopeDocument>(
                AetheriaRuntimeVerseRecordKeys.HangarCommandEnvelope(commandId));
            if (envelope != null)
                return string.Equals(envelope.ClientId, establishedRuntimeId, StringComparison.Ordinal);
            var receipt = node.Cache.Get<GameCult.Eve.Surface.EveCommandReceiptDocument>(new CultRecordKey(recordKey));
            if (receipt == null || context.Kind == AetheriaGameplayExposureKind.ActiveInvalid)
                return false;
            if (context.Kind == AetheriaGameplayExposureKind.LocalOpen)
                return true;
            if (context.Kind == AetheriaGameplayExposureKind.ArenaValid)
            {
                var seat = AetheriaRuntimeArenaObservationAdmission.ResolveSeat(
                    establishedRuntimeId, context.Arena!.Roster!, context.Arena.Frame!.Run);
                return seat != null && string.Equals(
                    receipt.SurfaceId,
                    AetheriaRuntimeVerseRecordKeys.ArenaPilotSurfaceId(seat.ControllerRuntimeId),
                    StringComparison.Ordinal);
            }
            var starbridge = context.Starbridge!;
            var role = AetheriaRuntimeStarbridgeObservationAdmission.ResolveRoleSeat(
                establishedRuntimeId,
                starbridge.Session.SessionId,
                starbridge.Session.RunId,
                starbridge.Seats,
                starbridge.Frame!.Run);
            var expectedSurfaceId = role?.Role switch
            {
                AetheriaRuntimeStarbridgePlayerSeatRoles.Pilot =>
                    AetheriaRuntimeVerseRecordKeys.StarbridgePilotSurfaceId(role.RuntimeId),
                AetheriaRuntimeStarbridgePlayerSeatRoles.Commander =>
                    AetheriaRuntimeDaemonGameSurfaceBuilder.CommanderSurfaceId,
                _ => ""
            };
            return !string.IsNullOrWhiteSpace(expectedSurfaceId) &&
                string.Equals(receipt.SurfaceId, expectedSurfaceId, StringComparison.Ordinal);
        }

        return context.Kind switch
        {
            AetheriaGameplayExposureKind.LocalOpen => true,
            AetheriaGameplayExposureKind.ActiveInvalid => false,
            AetheriaGameplayExposureKind.ArenaValid => AetheriaRuntimeArenaObservationAdmission.CanReadRecord(
                establishedRuntimeId, recordKey, context.Arena!.Roster!, context.Arena.Frame!.Run),
            AetheriaGameplayExposureKind.StarbridgeValid => AetheriaRuntimeStarbridgeObservationAdmission.CanReadRecord(
                establishedRuntimeId,
                recordKey,
                context.Starbridge!.Session.SessionId,
                context.Starbridge.Session.RunId,
                context.Starbridge.Seats,
                context.Starbridge.Frame!.Run),
            _ => false
        };
    }

    public static bool CanSubscribe(
        AetheriaStateNode node,
        string establishedRuntimeId,
        CultNetDatabaseSubscribeMessage request,
        AetheriaGameplayExposureContext context,
        string hangarPrincipalRuntimeId)
    {
        if (string.IsNullOrWhiteSpace(establishedRuntimeId) ||
            !string.Equals(request.ConsumerRuntimeId, establishedRuntimeId, StringComparison.Ordinal))
            return false;
        return (request.RecordKeys ?? Array.Empty<string>()).All(recordKey => CanReadRecord(
                node, establishedRuntimeId, recordKey, context, hangarPrincipalRuntimeId)) &&
            (request.BodyIds ?? Array.Empty<string>()).All(bodyId => context.Kind switch
            {
                AetheriaGameplayExposureKind.LocalOpen => true,
                AetheriaGameplayExposureKind.ActiveInvalid => false,
                AetheriaGameplayExposureKind.ArenaValid => AetheriaRuntimeArenaObservationAdmission.CanReadBody(
                    establishedRuntimeId, bodyId, context.Arena!.Roster!, context.Arena.Frame!.Run),
                AetheriaGameplayExposureKind.StarbridgeValid => AetheriaRuntimeStarbridgeObservationAdmission.CanReadBody(
                    establishedRuntimeId,
                    bodyId,
                    context.Starbridge!.Session.SessionId,
                    context.Starbridge.Session.RunId,
                    context.Starbridge.Seats,
                    context.Starbridge.Frame!.Run),
                _ => false
            });
    }

    private static string StarbridgeGeneration(string state, AetheriaStarbridgeExposureContext context) =>
        string.Join(
            "\u001f",
            state,
            context.Session.SessionId,
            context.Session.RunId,
            context.Session.RunRecordKey,
            context.Session.ModePolicyId,
            context.AuthorityPolicy?.PolicyId ?? "",
            context.AuthorityPolicy?.HostRuntimeId ?? "",
            context.AuthorityPolicy?.DefaultMode ?? "",
            string.Join("\u001e", context.Seats.Select(seat => string.Join(
                "\u001d", seat.SeatId, seat.Role, seat.RuntimeId, seat.ConnectionState, seat.ControlledEntityId))));
}
