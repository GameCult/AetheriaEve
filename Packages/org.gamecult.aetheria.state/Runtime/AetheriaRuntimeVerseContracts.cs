using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameCult.Caching;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaRuntimeVerseRecordKeys
    {
        public static CultRecordKey DaemonProviderAdvertisement { get; } = new("daemon:aetheria.provider_advertisement.v1");
        public static CultRecordKey EveProviderAdvertisement { get; } = new("eve:provider:aetheria.daemon");
        public const string EveCommandRecordPrefix = "eve:commands:aetheria.daemon";
        public const string EveReceiptRecordPrefix = "eve:receipts:aetheria.daemon";
        public static CultRecordKey DaemonHealth { get; } = new("daemon:aetheria.health.v1");
        public static CultRecordKey DaemonCommandBoundary { get; } = new("daemon:aetheria.command_boundary.v1");
        public static CultRecordKey DaemonAssetManifest { get; } = new("daemon:aetheria.asset_manifest.latest.v1");
        public static CultRecordKey EveAssetCatalog { get; } = new("eve:assets:aetheria.daemon");
        public static CultRecordKey EveAssetCatalogGeneration(long version)
        {
            if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
            return new CultRecordKey($"eve:assets:aetheria.daemon:version:{version}");
        }
        public static CultRecordKey VerseAuthorityPolicy { get; } = new(AetheriaRuntimeVerseAuthorityPolicyDocument.DocumentKey);
        public static CultRecordKey DaemonFrameLatest { get; } = new("daemon:aetheria.frame.latest.v1");
        public static CultRecordKey DaemonSoaViewLatest { get; } = new("daemon:aetheria.soa_view.latest.v1");
        public static CultRecordKey EveEntitySoaViewLatest { get; } = new("eve:entity-view:aetheria.daemon.pilot");
        public static CultRecordKey ZoneRenderLatest { get; } = new("daemon:aetheria.zone_render.latest.v1");
        public static CultRecordKey StarbridgeScenarioLatest { get; } = new("starbridge:aetheria.scenario.latest.v1");
        public static CultRecordKey StarbridgeSessionLatest { get; } = new("starbridge:aetheria.session.latest.v1");
        public static CultRecordKey StarbridgeSessionSummary { get; } = new("daemon:aetheria.starbridge.session.latest.v1");
        public static CultRecordKey StarbridgePlayerSeat(string seatId) => new(AetheriaRuntimeStarbridgePlayerSeatDocument.RecordKey(seatId));
        public static CultRecordKey DaemonGameSurface { get; } = new("eve:surface:aetheria.daemon.game");
        public static string ArenaPilotSurfaceId(string controllerRuntimeId) =>
            $"aetheria.arena.pilot.{StableIdentityToken(controllerRuntimeId)}";
        public static CultRecordKey ArenaPilotSurface(string controllerRuntimeId) =>
            new($"eve:surface:{ArenaPilotSurfaceId(controllerRuntimeId)}");
        public static CultRecordKey ArenaPilotFrame(string controllerRuntimeId) =>
            new($"daemon:aetheria.frame.arena.{StableIdentityToken(controllerRuntimeId)}.v1");
        public static string ArenaPilotBodyId(string controllerRuntimeId) =>
            $"eve:entity-soa:aetheria.daemon.arena.{StableIdentityToken(controllerRuntimeId)}";
        public static CultRecordKey ArenaPilotEntitySoaView(string controllerRuntimeId) =>
            new($"eve:entity-view:aetheria.daemon.arena.{StableIdentityToken(controllerRuntimeId)}");
        public static CultRecordKey ArenaPilotZoneRender(string controllerRuntimeId) =>
            new($"daemon:aetheria.zone_render.arena.{StableIdentityToken(controllerRuntimeId)}.v1");
        public static CultRecordKey ArenaPilotInputCapability(string controllerRuntimeId) =>
            new($"eve:input:aetheria.arena.{StableIdentityToken(controllerRuntimeId)}");
        public static CultRecordKey DaemonGameReactiveSurface { get; } = new("eve:surface:aetheria.daemon.game.reactive");
        public static CultRecordKey StarbridgeCommanderSurface { get; } = new("eve:surface:aetheria.starbridge.commander");
        public static CultRecordKey PilotInputCapability { get; } = new("eve:input:aetheria.pilot");
        public static CultRecordKey DaemonGameTuiSurface { get; } = new("eve:surface:aetheria.daemon.game.tui");
        public static CultRecordKey DaemonEditorSurface { get; } = new("eve:surface:aetheria.daemon.editor");
        public static CultRecordKey DaemonEditorTuiSurface { get; } = new("eve:surface:aetheria.daemon.editor.tui");
        public static CultRecordKey MainMenuSurface { get; } = new("eve:surface:aetheria.main_menu.root");
        public static CultRecordKey MainMenuSettingsSurface { get; } = new("eve:surface:aetheria.main_menu.settings");
        public static CultRecordKey MainMenuInputSettingsSurface { get; } = new("eve:surface:aetheria.main_menu.input_settings");
        public static CultRecordKey MainMenuPlayerSettingsSurface { get; } = new("eve:surface:aetheria.main_menu.player_settings");
        public static CultRecordKey InventoryPanelSurface { get; } = new("eve:surface:aetheria.inventory.panel");
        public static CultRecordKey InventoryDropdownSurface { get; } = new("eve:surface:aetheria.inventory.panel.dropdown");
        public static CultRecordKey MapMenuSurface { get; } = new("eve:surface:aetheria.map.zone_details");
        public static CultRecordKey TradeMenuSurface { get; } = new("eve:surface:aetheria.trade.menu");
        public static CultRecordKey HangarSurface { get; } = new("eve:surface:aetheria.hangar");
        public static CultRecordKey ArenaLobbySurface { get; } = new("eve:surface:aetheria.arena.lobby");
        public static CultRecordKey HangarProjection { get; } = new("global:gamecult.aetheria.hangar_projection.v1");
        public static CultRecordKey HangarDraft { get; } = new("global:gamecult.aetheria.hangar_draft.v1");
        public static CultRecordKey ProgressionSource { get; } = new(AetheriaProgressionSourceDocument.DocumentKey);

        public static CultRecordKey HangarCommandEnvelope(string commandId) =>
            new($"hangar:command-envelopes:{StableToken(commandId)}:gamecult.aetheria.hangar_command_envelope.v1");

        public static CultRecordKey ProgressionCommandRoute(string commandId) =>
            new($"hangar:command-routes:{StableToken(commandId)}:gamecult.aetheria.progression_command_route.v1");

        public static CultRecordKey DaemonCommand(string commandId) =>
            new($"daemon:commands:{StableToken(commandId)}:gamecult.aetheria.daemon_command.v1");

        public static CultRecordKey EveCommand(string commandId) =>
            new($"eve:commands:{StableToken(commandId)}:gamecult.eve.command.v1");

        public static CultRecordKey EveReceiptForCommand(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
                throw new ArgumentException("Eve receipt command id must be non-empty.", nameof(commandId));
            return new CultRecordKey($"{EveReceiptRecordPrefix}:{commandId}");
        }

        public static string StableToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "empty";
            var token = new string(value.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray())
                .Trim('-')
                .ToLowerInvariant();
            while (token.Contains("--", StringComparison.Ordinal))
                token = token.Replace("--", "-", StringComparison.Ordinal);
            return string.IsNullOrWhiteSpace(token) ? "empty" : token;
        }

        private static string StableIdentityToken(string value)
        {
            var identity = value ?? "";
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(identity));
            var suffix = string.Concat(hash.Take(8).Select(item => item.ToString("x2")));
            return $"{StableToken(identity)}-{suffix}";
        }
    }

    public sealed class AetheriaRuntimePilotObservationRefs
    {
        public AetheriaRuntimePilotObservationRefs(
            string statePointerId,
            string entityViewPointerId,
            string entityBodyId,
            string zoneRenderPointerId,
            string inputCapabilityId)
        {
            StatePointerId = statePointerId ?? "";
            EntityViewPointerId = entityViewPointerId ?? "";
            EntityBodyId = entityBodyId ?? "";
            ZoneRenderPointerId = zoneRenderPointerId ?? "";
            InputCapabilityId = inputCapabilityId ?? "";
        }

        public string StatePointerId { get; }
        public string EntityViewPointerId { get; }
        public string EntityBodyId { get; }
        public string ZoneRenderPointerId { get; }
        public string InputCapabilityId { get; }

        public static AetheriaRuntimePilotObservationRefs Primary { get; } = new(
            AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest.ToString(),
            AetheriaRuntimeVerseRecordKeys.EveEntitySoaViewLatest.ToString(),
            AetheriaRuntimeDaemonSoaFramePublisher.BodyId,
            AetheriaRuntimeVerseRecordKeys.ZoneRenderLatest.ToString(),
            AetheriaRuntimeVerseRecordKeys.PilotInputCapability.ToString());

        public static AetheriaRuntimePilotObservationRefs Arena(string controllerRuntimeId) => new(
            AetheriaRuntimeVerseRecordKeys.ArenaPilotFrame(controllerRuntimeId).ToString(),
            AetheriaRuntimeVerseRecordKeys.ArenaPilotEntitySoaView(controllerRuntimeId).ToString(),
            AetheriaRuntimeVerseRecordKeys.ArenaPilotBodyId(controllerRuntimeId),
            AetheriaRuntimeVerseRecordKeys.ArenaPilotZoneRender(controllerRuntimeId).ToString(),
            AetheriaRuntimeVerseRecordKeys.ArenaPilotInputCapability(controllerRuntimeId).ToString());
    }

    public static class AetheriaRuntimeVerseContractRegistry
    {
        private static readonly Type[] RuntimeDocumentTypes =
        {
            typeof(AetheriaRuntimeDaemonProviderAdvertisementDocument),
            typeof(AetheriaRuntimeDaemonHealthDocument),
            typeof(AetheriaRuntimeDaemonCommandBoundaryDocument),
            typeof(AetheriaRuntimeVerseAuthorityPolicyDocument),
            typeof(AetheriaRuntimeAuthorityLeaseDocument),
            typeof(AetheriaRuntimeArenaRosterDocument),
            typeof(AetheriaRuntimeDaemonFrameDocument),
            typeof(AetheriaRuntimeDaemonSoaViewDocument),
            typeof(AetheriaRuntimeCatalogSnapshot),
            typeof(AetheriaRuntimeLoadoutTemplatesDocument),
            typeof(AetheriaRuntimeAssetManifestDocument),
            typeof(AetheriaRuntimeObjectsViewportDocument),
            typeof(AetheriaRuntimeGravityViewportDocument),
            typeof(AetheriaRuntimeCurrentZoneDocument),
            typeof(AetheriaRuntimeCurrentEntityDocument),
            typeof(AetheriaRuntimeCurrentDockingDocument),
            typeof(AetheriaRuntimeZoneContactsDocument),
            typeof(AetheriaRuntimeStationRefitDocument),
            typeof(AetheriaRuntimeSectorMapDocument),
            typeof(AetheriaRuntimeZoneDetailsDocument),
            typeof(AetheriaRuntimeZoneRenderDocument),
            typeof(AetheriaRuntimeSelectedObjectDocument),
            typeof(AetheriaRuntimeInventoryDocument),
            typeof(AetheriaRuntimeStarbridgeScenarioDocument),
            typeof(AetheriaRuntimeStarbridgeSessionDocument),
            typeof(AetheriaRuntimeStarbridgeSessionSummaryDocument),
            typeof(AetheriaRuntimeStarbridgePlayerSeatDocument),
            typeof(AetheriaRuntimePlayerSettingsDocument),
            typeof(AetheriaRuntimeVerseHostSettingsDocument),
            typeof(AetheriaProgressionSourceDocument),
            typeof(AetheriaHangarCommandEnvelopeDocument),
            typeof(AetheriaProgressionCommandRouteDocument),
            typeof(AetheriaHangarState),
            typeof(AetheriaHangarDraftState),
            typeof(AetheriaHangarProjectionDocument),
            typeof(EveSurfaceDocument),
            typeof(EveProviderAdvertisementDocument),
            typeof(EveSurfaceCommandRequest),
            typeof(EveCommandReceiptDocument),
            typeof(EveAssetCatalogDocument),
            typeof(EveEntitySoaViewDocument),
            typeof(CultMeshBodyPublicationDocument),
            typeof(CultMeshCdnArtifactManifest),
            typeof(CultMeshCdnArtifactChunk),
            typeof(AetheriaRuntimeDaemonCommandDocument),
            typeof(AetheriaRuntimeEveCommandDocument),
            typeof(AetheriaRuntimeCommittedCommandFactDocument)
        };

        public static CultDocumentRegistry CreateCultCacheRegistry() =>
            CultMesh.CreateCultCacheDocumentRegistry(RuntimeDocumentTypes);

        public static CultNetDocumentRegistry CreateCultNetRegistry(CultDocumentRegistry? cacheRegistry = null) =>
            CultMesh.CreateCultNetDocumentRegistry(RuntimeDocumentTypes, cacheRegistry ?? CreateCultCacheRegistry());
    }
}
