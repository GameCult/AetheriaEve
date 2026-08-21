using GameCult.Aetheria.State.Verse;

namespace Aetheria.State.Daemon;

/// <summary>
/// Resolves Starbridge navigation from the durable role seat. The selected mode
/// is not sufficient to choose a player-facing surface.
/// </summary>
public static class AetheriaStarbridgeRoleNavigation
{
    public static string ResolveSurfaceId(
        string sessionId,
        string runId,
        string runtimeId,
        IReadOnlyList<AetheriaRuntimeStarbridgePlayerSeatDocument> seats)
    {
        var matches = (seats ?? Array.Empty<AetheriaRuntimeStarbridgePlayerSeatDocument>())
            .Where(value => value != null &&
                string.Equals(value.SessionId, sessionId, StringComparison.Ordinal) &&
                string.Equals(value.RunId, runId, StringComparison.Ordinal) &&
                string.Equals(value.RuntimeId, runtimeId, StringComparison.Ordinal) &&
                string.Equals(value.ConnectionState, AetheriaRuntimeStarbridgePlayerSeatConnectionStates.Connected, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Starbridge navigation requires exactly one connected role seat for its caller.");
        return matches[0].Role switch
        {
            AetheriaRuntimeStarbridgePlayerSeatRoles.Commander => AetheriaRuntimeDaemonGameSurfaceBuilder.CommanderSurfaceId,
            AetheriaRuntimeStarbridgePlayerSeatRoles.Pilot => AetheriaRuntimeVerseRecordKeys.StarbridgePilotSurfaceId(runtimeId),
            _ => throw new InvalidOperationException("Starbridge navigation cannot resolve an unknown role.")
        };
    }
}
