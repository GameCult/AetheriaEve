using System;
using GameCult.Eve.Surface;

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
            return AetheriaRuntimeDaemonFrameProjection.ForControlledEntity(frame, controlledEntityKey);
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
