using System;
using System.Collections.Generic;
using GameCult.Eve.Surface;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public sealed class AetheriaRuntimeStarbridgeSeatViewProjection
    {
        public AetheriaRuntimeStarbridgeSeatViewProjection(
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

    /// <summary>
    /// Derives the complete Starbridge Pilot presentation from the same durable seat and
    /// stable entity identity used by operation admission.
    /// </summary>
    public static class AetheriaRuntimeStarbridgeSeatViewProjector
    {
        public static AetheriaRuntimeStarbridgeSeatViewProjection Project(
            AetheriaRuntimeDaemonFrameDocument frame,
            AetheriaRuntimeStarbridgePlayerSeatDocument seat,
            IReadOnlyList<AetheriaRuntimeStarbridgePlayerSeatDocument> seats,
            AetheriaRuntimeDaemonHealthDocument health,
            AetheriaRuntimeDaemonCommandBoundaryDocument commandBoundary,
            AetheriaRuntimeCatalogSnapshot? catalog = null,
            string activeMainMenuSurfaceId = "")
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (seat == null) throw new ArgumentNullException(nameof(seat));
            if (!AetheriaRuntimeStarbridgeOperationAdmission.TryResolvePilotSeat(
                    seats,
                    seat.SessionId,
                    seat.RunId,
                    seat.RuntimeId,
                    frame.Run,
                    out var resolvedSeat,
                    out var controlledEntityKey) ||
                !string.Equals(resolvedSeat.SeatId, seat.SeatId, StringComparison.Ordinal))
                throw new InvalidOperationException("Starbridge Pilot view cannot resolve its durable seat and stable controlled entity.");

            var projected = AetheriaRuntimeDaemonFrameProjection.ForPilotObservation(frame, controlledEntityKey);
            var refs = AetheriaRuntimePilotObservationRefs.Starbridge(seat.RuntimeId);
            var surface = AetheriaRuntimeDaemonGameSurfaceBuilder.Build(
                projected,
                health,
                commandBoundary,
                activeMainMenuSurfaceId,
                catalog,
                controlledEntityKey,
                AetheriaRuntimeVerseRecordKeys.StarbridgePilotSurfaceId(seat.RuntimeId),
                refs);
            return new AetheriaRuntimeStarbridgeSeatViewProjection(
                controlledEntityKey,
                projected,
                refs,
                surface,
                AetheriaRuntimeGameDocuments.ZoneRender(projected),
                AetheriaRuntimeInputCapabilityDocument.FromFrame(projected, false, catalog).ToEveDocument());
        }
    }
}
