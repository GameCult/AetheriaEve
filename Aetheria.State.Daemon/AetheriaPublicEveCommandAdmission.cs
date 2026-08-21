using Aetheria.State;
using Aetheria.State.Documents;
using GameCult.Caching;
using GameCult.Aetheria.State.Verse;
using GameCult.Eve.Surface;

namespace Aetheria.State.Daemon;

/// <summary>
/// Owns public Eve command authorization after CultMesh has authenticated the
/// transport peer and before any command journal record exists.
/// </summary>
internal static class AetheriaPublicEveCommandAdmission
{
    public static void RequireAuthorized(
        AetheriaStateNode node,
        AetheriaDaemonHostOptions options,
        string establishedRuntimeId,
        EveSurfaceCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(establishedRuntimeId) ||
            !string.Equals(request.ClientId, establishedRuntimeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Eve command identity does not match the established CultMesh session.");

        if (string.Equals(request.SurfaceId, AetheriaRuntimeHangarCommands.SurfaceId, StringComparison.Ordinal))
        {
            if (!string.Equals(establishedRuntimeId, options.HangarPrincipalRuntimeId, StringComparison.Ordinal))
                throw new InvalidOperationException("The established runtime does not own this Hangar progression principal.");
            return;
        }

        var activeSession = node.Cache.Get<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey);
        if (activeSession == null)
            throw new InvalidOperationException("No active game session accepts gameplay commands.");
        var mode = AetheriaGameModes.Classify(activeSession.Mode);
        if (mode == AetheriaGameModeKind.Unsupported)
            throw new InvalidOperationException("The active game mode has no installed gameplay command policy.");
        if (mode == AetheriaGameModeKind.Terminus)
        {
            if (string.Equals(request.SurfaceId, AetheriaRuntimeArenaLobbyCommands.SurfaceId, StringComparison.Ordinal))
                throw new InvalidOperationException("Terminus does not expose the Arena lobby command boundary.");
            var frame = node.Cache.Get<AetheriaRuntimeDaemonFrameDocument>(
                AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest);
            if (!AetheriaDaemonFrameProvenance.BelongsToSession(frame, activeSession, options.DaemonId) ||
                !string.Equals(frame!.GameMode, AetheriaGameModes.Terminus, StringComparison.Ordinal))
                throw new InvalidOperationException("Terminus gameplay commands require the active session's authoritative frame.");
            return;
        }
        if (mode == AetheriaGameModeKind.Starbridge)
        {
            var seats = node.Documents<AetheriaRuntimeStarbridgePlayerSeatDocument>()
                .Where(value => value != null &&
                    string.Equals(value.SessionId, activeSession!.SessionId, StringComparison.Ordinal) &&
                    string.Equals(value.RunId, activeSession.RunId, StringComparison.Ordinal))
                .ToArray();
            var expectedSurface = AetheriaStarbridgeRoleNavigation.ResolveSurfaceId(
                activeSession!.SessionId,
                activeSession.RunId,
                establishedRuntimeId,
                seats);
            if (!string.Equals(request.SurfaceId, expectedSurface, StringComparison.Ordinal))
                throw new InvalidOperationException("The Starbridge command surface does not belong to the established runtime's active role seat.");
            return;
        }

        if (!string.Equals(request.SurfaceId, AetheriaRuntimeArenaLobbyCommands.SurfaceId, StringComparison.Ordinal))
            return;
        if (!string.Equals(request.Command, AetheriaRuntimeArenaLobbyCommands.Join, StringComparison.Ordinal))
            throw new InvalidOperationException("The Arena lobby accepts only its advertised join operation.");

        var session = activeSession;
        if (mode != AetheriaGameModeKind.Arena ||
            !string.Equals(
                Payload(request, AetheriaRuntimeArenaLobbyCommands.ExpectedSessionId),
                session.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                Payload(request, AetheriaRuntimeArenaLobbyCommands.ExpectedRunId),
                session.RunId,
                StringComparison.Ordinal))
            throw new InvalidOperationException("No active Arena session is accepting controllers.");
        var roster = node.Cache.Get<AetheriaRuntimeArenaRosterDocument>(
            new CultRecordKey(AetheriaRuntimeArenaRosterDocument.RecordKey(session.SessionId)));
        if (roster?.IsActiveFor(session.SessionId, session.RunId) != true)
            throw new InvalidOperationException("The active Arena has no matching controller roster.");
    }

    private static string Payload(EveSurfaceCommandRequest request, string key) =>
        request.PayloadFields.TryGetValue(key, out var value)
            ? value ?? ""
            : request.PayloadFields.TryGetValue("payload." + key, out value)
                ? value ?? ""
                : "";
}
