using Aetheria.State;
using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;
using GameCult.Caching;

namespace Aetheria.State.Daemon;

/// <summary>
/// Binds a gameplay receipt to the same role surface that admitted its proposer.
/// </summary>
internal static class AetheriaGameplayReceiptSurface
{
    public static string Resolve(
        AetheriaStateNode node,
        AetheriaRuntimeCommittedCommandFactDocument fact)
    {
        var session = node.Cache.Get<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey);
        if (session == null || !string.Equals(session.SessionId, fact.SessionId, StringComparison.Ordinal))
            return AetheriaRuntimeDaemonGameSurfaceBuilder.SurfaceId;
        if (string.Equals(session.Mode, AetheriaGameModes.Starbridge, StringComparison.Ordinal))
        {
            var seats = node.Documents<AetheriaRuntimeStarbridgePlayerSeatDocument>()
                .Where(seat => seat != null &&
                    string.Equals(seat.SessionId, session.SessionId, StringComparison.Ordinal) &&
                    string.Equals(seat.RunId, session.RunId, StringComparison.Ordinal))
                .ToArray();
            try
            {
                return AetheriaStarbridgeRoleNavigation.ResolveSurfaceId(
                    session.SessionId,
                    session.RunId,
                    fact.ProposedByRuntimeId,
                    seats);
            }
            catch (InvalidOperationException)
            {
                return "";
            }
        }
        if (string.Equals(session.Mode, AetheriaGameModes.Arena, StringComparison.Ordinal))
        {
            var roster = node.Cache.Get<AetheriaRuntimeArenaRosterDocument>(
                new CultRecordKey(AetheriaRuntimeArenaRosterDocument.RecordKey(session.SessionId)));
            var seat = roster?.Seats?.SingleOrDefault(value => value != null &&
                string.Equals(value.Status, AetheriaRuntimeArenaSeatStatuses.Active, StringComparison.Ordinal) &&
                string.Equals(value.ControllerRuntimeId, fact.ProposedByRuntimeId, StringComparison.Ordinal));
            return seat == null ? "" : AetheriaRuntimeVerseRecordKeys.ArenaPilotSurfaceId(seat.ControllerRuntimeId);
        }
        return AetheriaRuntimeDaemonGameSurfaceBuilder.SurfaceId;
    }
}
