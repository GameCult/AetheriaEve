using System;
using System.Linq;
using GameCult.Mesh;
using GameCult.Networking;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaRuntimeArenaObservationAdmission
    {
        private static readonly string[] PrivateRecordPrefixes =
        {
            "daemon:aetheria.frame",
            "eve:surface:aetheria.daemon.game",
            "eve:surface:aetheria.starbridge.commander",
            "eve:surface:aetheria.map.zone_details",
            "eve:surface:aetheria.arena.pilot.",
            "eve:entity-view:aetheria.daemon",
            "daemon:aetheria.zone_render",
            "eve:input:aetheria.pilot",
            "eve:input:aetheria.arena.",
            "mesh:body:eve:entity-soa:aetheria.daemon",
            "aetheria.viewport.",
            "aetheria.object.selected.",
            "aetheria.inventory.",
            "daemon:facts:",
            "eve:commands:aetheria.daemon:",
            "eve:receipts:aetheria.daemon:",
            "hangar:command-envelopes:",
            "hangar:command-routes:",
            "daemon:commands:"
        };

        public static bool CanSubscribe(
            string establishedRuntimeId,
            CultNetDatabaseSubscribeMessage request,
            AetheriaRuntimeArenaRosterDocument? roster,
            AetheriaRuntimeRunCheckpointCommit? run)
        {
            if (string.IsNullOrWhiteSpace(establishedRuntimeId) ||
                !string.Equals(request.ConsumerRuntimeId, establishedRuntimeId, StringComparison.Ordinal))
                return false;
            if (roster == null)
                return true;
            return (request.RecordKeys ?? Array.Empty<string>())
                    .All(recordKey => CanReadRecord(establishedRuntimeId, recordKey, roster, run)) &&
                (request.BodyIds ?? Array.Empty<string>())
                    .All(bodyId => CanReadBody(establishedRuntimeId, bodyId, roster, run));
        }

        public static bool CanReadRecord(
            string establishedRuntimeId,
            string recordKey,
            AetheriaRuntimeArenaRosterDocument? roster,
            AetheriaRuntimeRunCheckpointCommit? run)
        {
            if (roster == null)
                return true;
            if (!IsPrivateRecord(recordKey))
                return true;
            if (run == null || !string.Equals(roster.RunId, run.RunId, StringComparison.Ordinal))
                return false;
            var seat = ResolveSeat(establishedRuntimeId, roster, run);
            return seat != null && SeatRecordKeys(seat.ControllerRuntimeId).Contains(recordKey, StringComparer.Ordinal);
        }

        public static bool CanReadBody(
            string establishedRuntimeId,
            string bodyId,
            AetheriaRuntimeArenaRosterDocument? roster,
            AetheriaRuntimeRunCheckpointCommit? run)
        {
            if (roster == null)
                return true;
            if (!string.Equals(bodyId, AetheriaRuntimeDaemonSoaFramePublisher.BodyId, StringComparison.Ordinal) &&
                !bodyId.StartsWith("eve:entity-soa:aetheria.daemon.arena.", StringComparison.Ordinal))
                return true;
            if (run == null || !string.Equals(roster.RunId, run.RunId, StringComparison.Ordinal))
                return false;
            var seat = ResolveSeat(establishedRuntimeId, roster, run);
            return seat != null && string.Equals(
                bodyId,
                AetheriaRuntimeVerseRecordKeys.ArenaPilotBodyId(seat.ControllerRuntimeId),
                StringComparison.Ordinal);
        }

        public static AetheriaRuntimeArenaSeat? ResolveSeat(
            string establishedRuntimeId,
            AetheriaRuntimeArenaRosterDocument roster,
            AetheriaRuntimeRunCheckpointCommit run)
        {
            var matches = (roster.Seats ?? Array.Empty<AetheriaRuntimeArenaSeat>())
                .Where(seat => seat != null &&
                    string.Equals(seat.Status, AetheriaRuntimeArenaSeatStatuses.Active, StringComparison.Ordinal) &&
                    string.Equals(seat.ControllerRuntimeId, establishedRuntimeId ?? "", StringComparison.Ordinal) &&
                    run.TryResolveEntityId(seat.ControlledEntityId, out _))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static bool IsPrivateRecord(string recordKey) =>
            !string.IsNullOrWhiteSpace(recordKey) &&
            PrivateRecordPrefixes.Any(prefix => recordKey.StartsWith(prefix, StringComparison.Ordinal));

        private static string[] SeatRecordKeys(string runtimeId)
        {
            var bodyId = AetheriaRuntimeVerseRecordKeys.ArenaPilotBodyId(runtimeId);
            return new[]
            {
                AetheriaRuntimeVerseRecordKeys.ArenaPilotSurface(runtimeId).ToString(),
                AetheriaRuntimeVerseRecordKeys.ArenaPilotFrame(runtimeId).ToString(),
                AetheriaRuntimeVerseRecordKeys.ArenaPilotEntitySoaView(runtimeId).ToString(),
                AetheriaRuntimeVerseRecordKeys.ArenaPilotZoneRender(runtimeId).ToString(),
                AetheriaRuntimeVerseRecordKeys.ArenaPilotInputCapability(runtimeId).ToString(),
                CultMeshBodyPublicationDocument.CreateLatestRecordKey(bodyId).ToString()
            };
        }
    }
}
