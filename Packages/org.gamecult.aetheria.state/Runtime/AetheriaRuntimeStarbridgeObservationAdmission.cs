using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Mesh;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    /// <summary>
    /// Owns Starbridge role observation admission. The Commander may inspect canonical state;
    /// a Pilot may inspect only public records and its own seat projection.
    /// </summary>
    public static class AetheriaRuntimeStarbridgeObservationAdmission
    {
        private static readonly string[] PublicRecordKeys =
        {
            AetheriaRuntimeVerseRecordKeys.EveProviderAdvertisement.ToString(),
            AetheriaRuntimeVerseRecordKeys.EveAssetCatalog.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonHealth.ToString()
        };

        private static readonly string[] PublicRecordPrefixes =
        {
            "eve:assets:aetheria.daemon:version:",
            "mesh:cdn:artifact:",
            "mesh:cdn:chunk:",
            "mesh:entity-prefab:"
        };

        private static readonly string[] CommanderRecordKeys =
        {
            AetheriaRuntimeVerseRecordKeys.StarbridgeCommanderSurface.ToString(),
            AetheriaRuntimeVerseRecordKeys.MapMenuSurface.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest.ToString(),
            AetheriaRuntimeVerseRecordKeys.EveEntitySoaViewLatest.ToString(),
            AetheriaRuntimeVerseRecordKeys.ZoneRenderLatest.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonCommandBoundary.ToString(),
            AetheriaRuntimeVerseRecordKeys.StarbridgeScenarioLatest.ToString(),
            AetheriaRuntimeVerseRecordKeys.StarbridgeSessionLatest.ToString(),
            AetheriaRuntimeVerseRecordKeys.StarbridgeSessionSummary.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonGameReactiveSurface.ToString(),
            AetheriaRuntimeVerseRecordKeys.DaemonGameTuiSurface.ToString()
        };

        public static AetheriaRuntimeStarbridgePlayerSeatDocument? ResolveRoleSeat(
            string establishedRuntimeId,
            string sessionId,
            string runId,
            IEnumerable<AetheriaRuntimeStarbridgePlayerSeatDocument>? seats,
            AetheriaRuntimeRunCheckpointCommit run)
        {
            var matches = (seats ?? Enumerable.Empty<AetheriaRuntimeStarbridgePlayerSeatDocument>())
                .Where(seat => seat != null &&
                    string.Equals(seat.SessionId, sessionId ?? "", StringComparison.Ordinal) &&
                    string.Equals(seat.RunId, runId ?? "", StringComparison.Ordinal) &&
                    string.Equals(seat.RuntimeId, establishedRuntimeId ?? "", StringComparison.Ordinal) &&
                    string.Equals(seat.ConnectionState, AetheriaRuntimeStarbridgePlayerSeatConnectionStates.Connected, StringComparison.Ordinal) &&
                    (string.Equals(seat.Role, AetheriaRuntimeStarbridgePlayerSeatRoles.Commander, StringComparison.Ordinal) ||
                     (string.Equals(seat.Role, AetheriaRuntimeStarbridgePlayerSeatRoles.Pilot, StringComparison.Ordinal) &&
                      !string.IsNullOrWhiteSpace(seat.ControlledEntityId) &&
                      run.TryResolveEntityId(seat.ControlledEntityId, out _))))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        public static bool CanReadRecord(
            string establishedRuntimeId,
            string recordKey,
            string sessionId,
            string runId,
            IReadOnlyList<AetheriaRuntimeStarbridgePlayerSeatDocument> seats,
            AetheriaRuntimeRunCheckpointCommit run)
        {
            if (IsPublicRecord(recordKey))
                return true;
            var seat = ResolveRoleSeat(establishedRuntimeId, sessionId, runId, seats, run);
            if (seat == null)
                return false;
            if (string.Equals(seat.Role, AetheriaRuntimeStarbridgePlayerSeatRoles.Commander, StringComparison.Ordinal))
                return CommanderRecordKeys.Contains(recordKey, StringComparer.Ordinal) ||
                    string.Equals(
                        recordKey,
                        CultMeshBodyPublicationDocument.CreateLatestRecordKey(
                            AetheriaRuntimeDaemonSoaFramePublisher.BodyId).ToString(),
                        StringComparison.Ordinal);
            return PilotRecordKeys(seat.RuntimeId).Contains(recordKey, StringComparer.Ordinal);
        }

        public static bool CanReadBody(
            string establishedRuntimeId,
            string bodyId,
            string sessionId,
            string runId,
            IReadOnlyList<AetheriaRuntimeStarbridgePlayerSeatDocument> seats,
            AetheriaRuntimeRunCheckpointCommit run)
        {
            var seat = ResolveRoleSeat(establishedRuntimeId, sessionId, runId, seats, run);
            if (seat == null)
                return false;
            if (string.Equals(seat.Role, AetheriaRuntimeStarbridgePlayerSeatRoles.Commander, StringComparison.Ordinal))
                return true;
            return string.Equals(bodyId, AetheriaRuntimeVerseRecordKeys.StarbridgePilotBodyId(seat.RuntimeId), StringComparison.Ordinal);
        }

        private static bool IsPublicRecord(string recordKey) =>
            !string.IsNullOrWhiteSpace(recordKey) &&
            (PublicRecordKeys.Contains(recordKey, StringComparer.Ordinal) ||
             PublicRecordPrefixes.Any(prefix => recordKey.StartsWith(prefix, StringComparison.Ordinal)));

        private static string[] PilotRecordKeys(string runtimeId)
        {
            var bodyId = AetheriaRuntimeVerseRecordKeys.StarbridgePilotBodyId(runtimeId);
            return new[]
            {
                AetheriaRuntimeVerseRecordKeys.StarbridgePilotSurface(runtimeId).ToString(),
                AetheriaRuntimeVerseRecordKeys.StarbridgePilotFrame(runtimeId).ToString(),
                AetheriaRuntimeVerseRecordKeys.StarbridgePilotEntitySoaView(runtimeId).ToString(),
                AetheriaRuntimeVerseRecordKeys.StarbridgePilotZoneRender(runtimeId).ToString(),
                AetheriaRuntimeVerseRecordKeys.StarbridgePilotInputCapability(runtimeId).ToString(),
                CultMeshBodyPublicationDocument.CreateLatestRecordKey(bodyId).ToString()
            };
        }
    }
}
