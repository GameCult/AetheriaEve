using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Eve.Surface;
using MessagePack;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public sealed class AetheriaRuntimeArenaSeatViewProjection
    {
        public AetheriaRuntimeArenaSeatViewProjection(
            string controlledEntityKey,
            AetheriaRuntimeDaemonFrameDocument frame,
            AetheriaRuntimePilotObservationRefs observationRefs,
            EveSurfaceDocument surface,
            AetheriaRuntimeZoneRenderDocument zoneRender,
            EveInputCapabilityDocument inputCapability)
        {
            ControlledEntityKey = controlledEntityKey;
            Frame = frame;
            ObservationRefs = observationRefs;
            Surface = surface;
            ZoneRender = zoneRender;
            InputCapability = inputCapability;
        }

        public string ControlledEntityKey { get; }
        public AetheriaRuntimeDaemonFrameDocument Frame { get; }
        public AetheriaRuntimePilotObservationRefs ObservationRefs { get; }
        public EveSurfaceDocument Surface { get; }
        public AetheriaRuntimeZoneRenderDocument ZoneRender { get; }
        public EveInputCapabilityDocument InputCapability { get; }
    }

    public static class AetheriaRuntimeArenaSeatViewProjector
    {
        public static AetheriaRuntimeDaemonFrameDocument ResolveFrame(
            AetheriaRuntimeDaemonFrameDocument frame,
            AetheriaRuntimeArenaSeat seat)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (seat == null) throw new ArgumentNullException(nameof(seat));
            if (!frame.Run.TryResolveEntityId(seat.ControlledEntityId, out var controlledEntityKey))
                throw new InvalidOperationException("Arena seat view cannot resolve its stable controlled entity.");
            var projected = AetheriaRuntimeDaemonFrameProjection.ForControlledEntity(frame, controlledEntityKey);
            return ProjectVisibleObservation(projected);
        }

        private static AetheriaRuntimeDaemonFrameDocument ProjectVisibleObservation(
            AetheriaRuntimeDaemonFrameDocument frame)
        {
            var run = frame.Run;
            var zone = (run.Zones ?? Array.Empty<AetheriaRuntimeZoneSnapshotCommit>())
                .Single(candidate => candidate.ZoneIndex == run.CurrentZoneIndex);
            AetheriaRuntimeRunCheckpointCommit.TryParseEntityKey(
                run.CurrentEntityKey, out _, out var controlledEntityIndex);
            var visible = AetheriaRuntimeDaemonRenderQueries.QueryEffectiveContacts(zone, controlledEntityIndex)
                .Where(contact => contact.Contact.Visible)
                .Select(contact => contact.Contact.TargetEntityIndex)
                .Append(controlledEntityIndex)
                .ToHashSet();
            var dockParent = (zone.Entities ?? Array.Empty<AetheriaRuntimeEntitySnapshotCommit>())
                .FirstOrDefault(entity => entity != null &&
                    (entity.DockingBayAssignments ?? Array.Empty<int>()).Contains(controlledEntityIndex));
            if (dockParent != null)
                visible.Add(dockParent.EntityIndex);

            var entities = (zone.Entities ?? Array.Empty<AetheriaRuntimeEntitySnapshotCommit>())
                .Where(entity => entity != null && visible.Contains(entity.EntityIndex))
                .Select(entity => MessagePackSerializer.Deserialize<AetheriaRuntimeEntitySnapshotCommit>(
                    MessagePackSerializer.Serialize(entity)))
                .ToArray();
            foreach (var entity in entities)
            {
                entity.Contacts = (entity.Contacts ?? Array.Empty<AetheriaRuntimeEntityContactCommit>())
                    .Where(contact => contact != null && contact.Visible && visible.Contains(contact.TargetEntityIndex))
                    .ToArray();
                if (!visible.Contains(entity.TargetEntityIndex))
                    entity.TargetEntityIndex = -1;
                entity.ChildEntityIndices = (entity.ChildEntityIndices ?? Array.Empty<int>())
                    .Where(visible.Contains)
                    .ToArray();
                entity.DockingBayAssignments = (entity.DockingBayAssignments ?? Array.Empty<int>())
                    .Where(visible.Contains)
                    .ToArray();
            }

            var observedZone = new AetheriaRuntimeZoneSnapshotCommit
            {
                ZoneIndex = zone.ZoneIndex,
                Name = zone.Name,
                PositionX = zone.PositionX,
                PositionY = zone.PositionY,
                AdjacentZoneIndices = Array.Empty<int>(),
                FactionIndices = zone.FactionIndices,
                OwnerFactionIndex = zone.OwnerFactionIndex,
                Entities = entities,
                Orbits = zone.Orbits,
                Bodies = zone.Bodies,
                DroppedPickups = zone.DroppedPickups,
                GravityTerrainRadius = zone.GravityTerrainRadius,
                GravityTerrainDepth = zone.GravityTerrainDepth,
                GravityTerrainDepthExponent = zone.GravityTerrainDepthExponent,
                GravityTerrainBoundaryFog = zone.GravityTerrainBoundaryFog,
                GravityTerrainWaveFrequency = zone.GravityTerrainWaveFrequency,
                SimulationTimeSeconds = zone.SimulationTimeSeconds,
                PhysicalPayloads = (zone.PhysicalPayloads ?? Array.Empty<AetheriaRuntimePhysicalPayloadCommit>())
                    .Where(payload => payload != null &&
                        (visible.Contains(payload.SourceEntityIndex) || visible.Contains(payload.TargetEntityIndex)))
                    .ToArray(),
                NextPickupIndex = zone.NextPickupIndex
            };
            run.Zones = new[] { observedZone };
            run.DiscoveredZoneIndices = new[] { observedZone.ZoneIndex };
            run.FactionRelationships = Array.Empty<AetheriaRuntimeFactionRelationshipCommit>();
            run.AgentTasks = Array.Empty<AetheriaRuntimeAgentTaskCommit>();
            run.CorporationSurveys = Array.Empty<AetheriaRuntimeCorporationSurveyCommit>();
            run.GameEvents = Array.Empty<AetheriaRuntimeGameEventCommit>();
            run.ShotReceipts = Array.Empty<AetheriaRuntimeShotReceiptCommit>();
            run.PickupContactReceipts = Array.Empty<AetheriaRuntimePickupContactReceiptCommit>();
            run.HomeZones = Array.Empty<AetheriaRuntimeFactionZoneCommit>();
            run.BossZones = Array.Empty<AetheriaRuntimeFactionZoneCommit>();
            frame.AppliedCommandIds = Array.Empty<string>();
            frame.RejectedCommandIds = Array.Empty<string>();
            frame.AccountedCommandIds = Array.Empty<string>();
            frame.CumulativeAppliedCommandIds = Array.Empty<string>();
            frame.CumulativeRejectedCommandIds = Array.Empty<string>();
            frame.RejectedCommandReasons = new Dictionary<string, string>();
            return frame;
        }

        public static AetheriaRuntimeArenaSeatViewProjection Project(
            AetheriaRuntimeDaemonFrameDocument frame,
            AetheriaRuntimeArenaSeat seat,
            AetheriaRuntimeDaemonHealthDocument health,
            AetheriaRuntimeDaemonCommandBoundaryDocument commandBoundary,
            AetheriaRuntimeCatalogSnapshot? catalog = null,
            string activeMainMenuSurfaceId = "")
        {
            if (seat == null) throw new ArgumentNullException(nameof(seat));
            var projected = ResolveFrame(frame, seat);
            var refs = AetheriaRuntimePilotObservationRefs.Arena(seat.ControllerRuntimeId);
            var surface = AetheriaRuntimeDaemonGameSurfaceBuilder.Build(
                projected,
                health,
                commandBoundary,
                activeMainMenuSurfaceId,
                catalog,
                projected.Run.CurrentEntityKey,
                AetheriaRuntimeVerseRecordKeys.ArenaPilotSurfaceId(seat.ControllerRuntimeId),
                refs);
            return new AetheriaRuntimeArenaSeatViewProjection(
                projected.Run.CurrentEntityKey,
                projected,
                refs,
                surface,
                AetheriaRuntimeGameDocuments.ZoneRender(projected),
                AetheriaRuntimeInputCapabilityDocument.FromFrame(projected, false, catalog).ToEveDocument());
        }
    }
}
