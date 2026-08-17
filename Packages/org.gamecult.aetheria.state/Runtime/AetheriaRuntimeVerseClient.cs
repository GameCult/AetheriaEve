using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;
using R3;
using EveUiCommandRequest = GameCult.Eve.Surface.EveSurfaceCommandRequest;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaRuntimeVerseRecordKeys
    {
        public static CultRecordKey DaemonProviderAdvertisement { get; } =
            new CultRecordKey("daemon:aetheria.provider_advertisement.v1");

        public static CultRecordKey EveProviderAdvertisement { get; } =
            new CultRecordKey("eve:provider:aetheria.daemon");

        public const string EveCommandRecordPrefix = "eve:commands:aetheria.daemon";

        public const string EveReceiptRecordPrefix = "eve:receipts:aetheria.daemon";

        public static CultRecordKey DaemonHealth { get; } =
            new CultRecordKey("daemon:aetheria.health.v1");

        public static CultRecordKey DaemonCommandBoundary { get; } =
            new CultRecordKey("daemon:aetheria.command_boundary.v1");

        public static CultRecordKey DaemonAssetManifest { get; } =
            new CultRecordKey("daemon:aetheria.asset_manifest.latest.v1");

        public static CultRecordKey EveAssetCatalog { get; } =
            new CultRecordKey("eve:assets:aetheria.daemon");

        public static CultRecordKey VerseAuthorityPolicy { get; } =
            new CultRecordKey(AetheriaRuntimeVerseAuthorityPolicyDocument.DocumentKey);

        public static CultRecordKey DaemonFrameLatest { get; } =
            new CultRecordKey("daemon:aetheria.frame.latest.v1");

        public static CultRecordKey DaemonSoaViewLatest { get; } =
            new CultRecordKey("daemon:aetheria.soa_view.latest.v1");

        public static CultRecordKey EveEntitySoaViewLatest { get; } =
            new CultRecordKey("eve:entity-view:aetheria.daemon.pilot");

        public static CultRecordKey ZoneRenderLatest { get; } =
            new CultRecordKey("daemon:aetheria.zone_render.latest.v1");

        public static CultRecordKey StarbridgeScenarioLatest { get; } =
            new CultRecordKey("starbridge:aetheria.scenario.latest.v1");

        public static CultRecordKey StarbridgeSessionLatest { get; } =
            new CultRecordKey("starbridge:aetheria.session.latest.v1");

        public static CultRecordKey StarbridgeSessionSummary { get; } =
            new CultRecordKey("daemon:aetheria.starbridge.session.latest.v1");

        public static CultRecordKey StarbridgePlayerSeat(string seatId)
        {
            return new CultRecordKey(AetheriaRuntimeStarbridgePlayerSeatDocument.RecordKey(seatId));
        }

        public static CultRecordKey DaemonGameSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.daemon.game");

        public static CultRecordKey DaemonGameReactiveSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.daemon.game.reactive");

        public static CultRecordKey StarbridgeCommanderSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.starbridge.commander");

        public static CultRecordKey PilotInputCapability { get; } =
            new CultRecordKey("eve:input:aetheria.pilot");

        public static CultRecordKey DaemonGameTuiSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.daemon.game.tui");

        public static CultRecordKey DaemonEditorSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.daemon.editor");

        public static CultRecordKey DaemonEditorTuiSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.daemon.editor.tui");

        public static CultRecordKey MainMenuSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.main_menu.root");

        public static CultRecordKey MainMenuSettingsSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.main_menu.settings");

        public static CultRecordKey MainMenuInputSettingsSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.main_menu.input_settings");

        public static CultRecordKey MainMenuPlayerSettingsSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.main_menu.player_settings");

        public static CultRecordKey MainMenuVerseSettingsSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.main_menu.verse_settings");

        public static CultRecordKey InventoryPanelSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.inventory.panel");

        public static CultRecordKey InventoryDropdownSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.inventory.panel.dropdown");

        public static CultRecordKey MapMenuSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.map.zone_details");

        public static CultRecordKey TradeMenuSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.trade.menu");

        public static CultRecordKey HangarSurface { get; } =
            new CultRecordKey("eve:surface:aetheria.hangar");

        public static CultRecordKey ProgressionSource { get; } =
            new CultRecordKey(AetheriaProgressionSourceDocument.DocumentKey);

        public static CultRecordKey DaemonCommand(string commandId)
        {
            return new CultRecordKey($"daemon:commands:{StableToken(commandId)}:gamecult.aetheria.daemon_command.v1");
        }

        public static CultRecordKey EveCommand(string commandId)
        {
            return new CultRecordKey($"eve:commands:{StableToken(commandId)}:gamecult.eve.command.v1");
        }

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

            var chars = value
                .Select(character => char.IsLetterOrDigit(character) ? character : '-')
                .ToArray();
            var token = new string(chars).Trim('-').ToLowerInvariant();
            while (token.Contains("--", StringComparison.Ordinal))
                token = token.Replace("--", "-", StringComparison.Ordinal);
            return string.IsNullOrWhiteSpace(token) ? "empty" : token;
        }
    }

    public static class AetheriaRuntimeVerseContractRegistry
    {
        private static readonly Type[] RuntimeDocumentTypes =
        {
            typeof(AetheriaRuntimeDaemonProviderAdvertisementDocument),
            typeof(AetheriaRuntimeDaemonHealthDocument),
            typeof(AetheriaRuntimeDaemonCommandBoundaryDocument),
            typeof(AetheriaRuntimeVerseAuthorityPolicyDocument),
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

        public static CultDocumentRegistry CreateCultCacheRegistry()
        {
            return CultMesh.CreateCultCacheDocumentRegistry(RuntimeDocumentTypes);
        }

        public static CultNetDocumentRegistry CreateCultNetRegistry(CultDocumentRegistry? cacheRegistry = null)
        {
            var registry = cacheRegistry ?? CreateCultCacheRegistry();
            return CultMesh.CreateCultNetDocumentRegistry(RuntimeDocumentTypes, registry);
        }
    }

    public sealed class AetheriaRuntimeVerseClient : IDisposable
    {
        public const string DefaultRuntimeId = "aetheria-verse-client";

        private readonly CultMeshNode _node;
        private AetheriaClientState? _aetheriaState;
        private CultMeshObservedDocument<AetheriaRuntimeDaemonFrameDocument>? _managedDaemonFrame;
        private CultMeshObservedDocument<AetheriaRuntimeCatalogSnapshot>? _managedCatalog;
        private CultMeshObservedDocument<AetheriaRuntimeLoadoutTemplatesDocument>? _managedLoadoutTemplates;
        private CultMeshObservedDocument<AetheriaRuntimeStarbridgeScenarioDocument>? _managedStarbridgeScenario;
        private CultMeshObservedDocument<AetheriaRuntimeStarbridgeSessionDocument>? _managedStarbridgeSession;
        private bool _disposed;

        private AetheriaRuntimeVerseClient(string statePath, string runtimeId, CultMeshNode node)
        {
            StatePath = statePath;
            RuntimeId = runtimeId;
            _node = node ?? throw new ArgumentNullException(nameof(node));
        }

        public string StatePath { get; }

        public string RuntimeId { get; }

        public CultMeshNode Node => _node;

        public CultCache Cache => _node.Cache;

        public CultNetDatabase Database => _node.Database;

        public static async Task<AetheriaRuntimeVerseClient> OpenAsync(
            string statePath,
            string runtimeId = DefaultRuntimeId,
            bool startServer = false,
            bool pullOnOpen = true)
        {
            if (string.IsNullOrWhiteSpace(statePath))
                throw new ArgumentException("State path must be non-empty.", nameof(statePath));

            var fullPath = Path.GetFullPath(statePath);
            var effectiveRuntimeId = string.IsNullOrWhiteSpace(runtimeId) ? DefaultRuntimeId : runtimeId;
            var registry = AetheriaRuntimeVerseContractRegistry.CreateCultCacheRegistry();
            var runtimeSchemaIds = new HashSet<string>(
                registry.AllDescriptors.Select(descriptor => descriptor.SchemaId),
                StringComparer.Ordinal);
            var node = await CultMesh.CreateNodeAsync(
                    fullPath,
                    new CultMeshNodeOptions
                    {
                        StartServer = startServer,
                        EnableDurableShardLogs = true,
                        CacheOptions = new CultCacheOpenOptions
                        {
                            Registry = registry,
                            PullOnOpen = pullOnOpen,
                            StoreFlushOnDispose = true,
                            UseDirectoryStore = true,
                            // A thin runtime shares the daemon's physical CultCache, but it does not
                            // own or understand catalog, migration, Hangar, or other daemon schemas.
                            // Hydrate only the contract it can deserialize; untouched records remain
                            // owned by the backing store and are not projected into this client cache.
                            DirectoryStoreHydrationFilter = metadata => runtimeSchemaIds.Contains(metadata.SchemaId)
                        },
                        DatabaseOptions = new CultNetDatabaseOptions
                        {
                            RuntimeId = effectiveRuntimeId,
                            DocumentRegistry = AetheriaRuntimeVerseContractRegistry.CreateCultNetRegistry(registry)
                        }
                    })
                .ConfigureAwait(false);

            return new AetheriaRuntimeVerseClient(fullPath, effectiveRuntimeId, node);
        }

        public AetheriaClientState Aetheria()
        {
            ThrowIfDisposed();
            return _aetheriaState ??= CreateAetheriaState();
        }

        public CultMeshMutableStatePointer<TDocument> MutableDocument<TDocument>(
            CultRecordKey key)
            where TDocument : class
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(key.Value))
                throw new ArgumentException("Value must be non-empty.", nameof(key));
            return MutableDocumentPointer<TDocument>(key);
        }

        public CultMeshDocumentHandle<TDocument> Document<TDocument>(
            CultRecordKey key,
            string? documentId = null)
            where TDocument : class
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(key.Value))
                throw new ArgumentException("Value must be non-empty.", nameof(key));
            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            return CultMesh.Document<TDocument>(
                Database,
                key,
                CultMesh.Verse("aetheria.local", RuntimeId),
                documentId,
                new[]
                {
                    CultMesh.ProjectionSource(key.ToString(), descriptor.SchemaId, "Aetheria Verse database document")
                },
                new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "Aetheria Verse database"));
        }

        public Observable<CultNetDatabaseChange<TDocument>> WatchRecord<TDocument>(
            CultRecordKey key)
            where TDocument : class
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(key.Value))
                throw new ArgumentException("Value must be non-empty.", nameof(key));
            return Database.WatchRecord<TDocument>(key);
        }

        internal async Task<AetheriaRuntimeDaemonCommandEnvelope> SubmitDaemonCommandAsync(
            AetheriaRuntimeDaemonCommandDocument command,
            bool flush = true)
        {
            ThrowIfDisposed();
            if (command == null) throw new ArgumentNullException(nameof(command));

            command.Schema = AetheriaRuntimeDaemonSchemas.Command;
            if (string.IsNullOrWhiteSpace(command.CommandId))
                command.CommandId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(command.ClientId))
                command.ClientId = RuntimeId;
            if (string.IsNullOrWhiteSpace(command.IssuedAtUtc))
                command.IssuedAtUtc = DateTime.UtcNow.ToString("O");

            await Database.PutAsync(AetheriaRuntimeVerseRecordKeys.DaemonCommand(command.CommandId), command)
                .ConfigureAwait(false);
            if (flush)
                await FlushAsync().ConfigureAwait(false);

            return AetheriaRuntimeDaemonOperationClient.ToEnvelope(command);
        }

        internal async Task<AetheriaRuntimeEveCommandEnvelope> SubmitEveCommandAsync(
            AetheriaRuntimeEveCommandDocument command,
            bool flush = true)
        {
            ThrowIfDisposed();
            if (command == null) throw new ArgumentNullException(nameof(command));

            AetheriaRuntimeEveCommandClient.NormalizeDocument(command);
            command.Schema = AetheriaRuntimeEveCommandDocument.SchemaId;
            if (string.IsNullOrWhiteSpace(command.CommandId))
                command.CommandId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(command.IssuedAtUtc))
                command.IssuedAtUtc = DateTime.UtcNow.ToString("O");

            await Database.PutAsync(AetheriaRuntimeVerseRecordKeys.EveCommand(command.CommandId), command)
                .ConfigureAwait(false);
            if (flush)
                await FlushAsync().ConfigureAwait(false);

            return AetheriaRuntimeEveCommandClient.ToEnvelope(command);
        }

        internal Task<AetheriaRuntimeEveCommandEnvelope> SubmitInputSettingsCommandAsync(
            AetheriaRuntimeEveCommandKind command,
            AetheriaRuntimeInputSettingsCommandBody body,
            string clientId,
            bool flush = true)
        {
            ThrowIfDisposed();
            return SubmitEveCommandAsync(
                AetheriaRuntimeEveCommandClient.ToDocument(
                    AetheriaRuntimeEveCommandClient.CreateInputSettingsCommand(command, body, clientId)),
                flush);
        }

        internal Task<AetheriaRuntimeEveCommandEnvelope> SubmitLoadoutTemplateCommandAsync(
            AetheriaRuntimeLoadoutTemplateCommit loadoutTemplate,
            string clientId,
            bool flush = true)
        {
            ThrowIfDisposed();
            return SubmitEveCommandAsync(
                AetheriaRuntimeEveCommandClient.ToDocument(
                    AetheriaRuntimeEveCommandClient.CreateLoadoutTemplateCommand(loadoutTemplate, clientId)),
                flush);
        }

        internal Task<AetheriaRuntimeEveCommandEnvelope> SubmitKnownSurfaceCommandAsync(
            EveUiCommandRequest request,
            string clientId,
            bool flush = true)
        {
            ThrowIfDisposed();
            if (!AetheriaRuntimeEveCommandClient.TryCreateKnownSurfaceCommand(request, out var envelope))
            {
                throw new InvalidOperationException(
                    $"Unknown Aetheria Eve surface command: {request?.ProviderId}/{request?.SurfaceId}/{request?.Command}");
            }

            var document = AetheriaRuntimeEveCommandClient.ToDocument(envelope!);
            if (!string.IsNullOrWhiteSpace(clientId))
                document.ClientId = clientId;
            return SubmitEveCommandAsync(document, flush);
        }

        public Task FlushAsync(bool soft = false)
        {
            ThrowIfDisposed();
            return _node.FlushAsync(soft);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _aetheriaState?.Dispose();
            _aetheriaState = null;
            _managedDaemonFrame?.Dispose();
            _managedCatalog?.Dispose();
            _managedLoadoutTemplates?.Dispose();
            _managedStarbridgeScenario?.Dispose();
            _managedStarbridgeSession?.Dispose();
            _node.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AetheriaRuntimeVerseClient));
        }

        private AetheriaClientState CreateAetheriaState()
        {
            var frameChanges = WatchRecord<AetheriaRuntimeDaemonFrameDocument>(
                    AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest)
                .Where(change => change.Document != null)
                .Select(change => change.Document!);
            var catalogDocument = BootstrapCatalogDocument(
                "aetheria.catalog.runtime",
                () => Task.FromResult(BootstrapRuntimeCatalogSnapshot()),
                AetheriaRuntimeCatalogSnapshot.SchemaId,
                CatalogBootstrapSource("catalog:aetheria.runtime"));
            var loadoutTemplatesDocument = BootstrapCatalogDocument(
                "aetheria.catalog.loadout_templates",
                () => Task.FromResult(BootstrapLoadoutTemplatesDocument()),
                AetheriaRuntimeLoadoutTemplatesDocument.SchemaId,
                CatalogBootstrapSource("catalog:aetheria.loadout_templates"));
            var playerSettingsDocument = BootstrapCatalogDocument(
                "aetheria.settings.player",
                () => Task.FromResult(BootstrapPlayerSettingsDocument()),
                AetheriaRuntimePlayerSettingsDocument.SchemaId,
                CatalogBootstrapSource("catalog:aetheria.player_settings"));
            var verseHostSettingsDocument = BootstrapCatalogDocument(
                "aetheria.settings.verse_host",
                () => Task.FromResult(BootstrapVerseHostSettingsDocument()),
                AetheriaRuntimeVerseHostSettingsDocument.SchemaId,
                CatalogBootstrapSource("catalog:aetheria.verse_host_settings"));
            var starbridgeScenarioDocument = Document<AetheriaRuntimeStarbridgeScenarioDocument>(
                AetheriaRuntimeVerseRecordKeys.StarbridgeScenarioLatest);
            var starbridgeSessionDocument = Document<AetheriaRuntimeStarbridgeSessionDocument>(
                AetheriaRuntimeVerseRecordKeys.StarbridgeSessionLatest);
            var latestFrameDocument = Document<AetheriaRuntimeDaemonFrameDocument>(
                AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest);
            CultMeshObservedDocument<AetheriaRuntimeDaemonFrameDocument>? managedDaemonFrame = null;
            CultMeshObservedDocument<AetheriaRuntimeCatalogSnapshot>? managedCatalog = null;
            CultMeshObservedDocument<AetheriaRuntimeLoadoutTemplatesDocument>? managedLoadoutTemplates = null;
            CultMeshObservedDocument<AetheriaRuntimeStarbridgeScenarioDocument>? managedStarbridgeScenario = null;
            CultMeshObservedDocument<AetheriaRuntimeStarbridgeSessionDocument>? managedStarbridgeSession = null;

            var state = new AetheriaClientState(
                Document<AetheriaRuntimeDaemonProviderAdvertisementDocument>(
                    AetheriaRuntimeVerseRecordKeys.DaemonProviderAdvertisement),
                Document<AetheriaRuntimeDaemonHealthDocument>(
                    AetheriaRuntimeVerseRecordKeys.DaemonHealth),
                Document<AetheriaRuntimeDaemonCommandBoundaryDocument>(
                    AetheriaRuntimeVerseRecordKeys.DaemonCommandBoundary),
                Document<AetheriaRuntimeAssetManifestDocument>(
                    AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest),
                Document<AetheriaRuntimeVerseAuthorityPolicyDocument>(
                    AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy),
                latestFrameDocument,
                Document<AetheriaRuntimeDaemonSoaViewDocument>(
                    AetheriaRuntimeVerseRecordKeys.DaemonSoaViewLatest),
                Document<EveSurfaceDocument>(
                    AetheriaRuntimeVerseRecordKeys.DaemonGameSurface),
                Document<EveSurfaceDocument>(
                    AetheriaRuntimeVerseRecordKeys.DaemonGameTuiSurface),
                Document<EveSurfaceDocument>(
                    AetheriaRuntimeVerseRecordKeys.DaemonEditorSurface),
                Document<EveSurfaceDocument>(
                    AetheriaRuntimeVerseRecordKeys.DaemonEditorTuiSurface),
                catalogDocument,
                loadoutTemplatesDocument,
                playerSettingsDocument,
                verseHostSettingsDocument,
                ManagedFrameDocument(
                    "aetheria.current.zone",
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.CurrentZone(frame)),
                    AetheriaRuntimeDaemonSchemas.CurrentZone),
                ManagedFrameDocument(
                    "aetheria.current.entity",
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.CurrentEntity(frame)),
                    AetheriaRuntimeDaemonSchemas.CurrentEntity),
                ManagedFrameDocument(
                    "aetheria.current.docking",
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.CurrentDocking(frame)),
                    AetheriaRuntimeDaemonSchemas.CurrentDocking),
                ManagedFrameDocument(
                    "aetheria.zone.contacts",
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.ZoneContacts(frame)),
                    AetheriaRuntimeDaemonSchemas.ZoneContacts),
                ManagedFrameDocument(
                    "aetheria.station.refit",
                    StationRefitAsync,
                    AetheriaRuntimeDaemonSchemas.StationRefit,
                    CultMesh.ProjectionSource("catalog:aetheria.runtime"),
                    CultMesh.ProjectionSource("loadout-templates:aetheria.runtime")),
                ManagedFrameDocument(
                    "aetheria.sector.map",
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.SectorMap(frame)),
                    AetheriaRuntimeDaemonSchemas.SectorMap),
                ManagedFrameDocument(
                    "aetheria.zone.render",
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.ZoneRender(frame)),
                    AetheriaRuntimeDaemonSchemas.ZoneRender),
                viewport => ManagedFrameDocument(
                    ViewportDocumentId("aetheria.viewport.map", viewport),
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.Viewport(frame, viewport)),
                    AetheriaRuntimeDaemonSchemas.GameViewport),
                viewport => ManagedFrameDocument(
                    ViewportDocumentId("aetheria.viewport.objects", viewport),
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.ObjectsViewport(frame, viewport)),
                    AetheriaRuntimeDaemonSchemas.ObjectsViewport),
                viewport => ManagedFrameDocument(
                    ViewportDocumentId("aetheria.viewport.gravity", viewport),
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.GravityViewport(frame, viewport)),
                    AetheriaRuntimeDaemonSchemas.GravityViewport),
                viewport => ManagedFrameDocument(
                    ViewportDocumentId("aetheria.viewport.render_splats", viewport),
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.RenderSplatsViewport(frame, viewport)),
                    AetheriaRuntimeDaemonSchemas.RenderSplatsViewport),
                zoneIndex => ManagedFrameDocument(
                    IndexedDocumentId("aetheria.zone.details", zoneIndex),
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.ZoneDetails(frame, zoneIndex)),
                    AetheriaRuntimeDaemonSchemas.ZoneDetails),
                entityIndex => ManagedFrameDocument(
                    IndexedDocumentId("aetheria.object.selected", entityIndex),
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.SelectedObject(frame, entityIndex)),
                    AetheriaRuntimeDaemonSchemas.SelectedObject),
                entityIndex => ManagedFrameDocument(
                    IndexedDocumentId("aetheria.inventory", entityIndex),
                    frame => Task.FromResult(AetheriaRuntimeGameDocuments.Inventory(frame, entityIndex)),
                    AetheriaRuntimeDaemonSchemas.Inventory),
                starbridgeScenarioDocument,
                starbridgeSessionDocument,
                ManagedFrameDocument(
                    "aetheria.starbridge.summary",
                    StarbridgeSummaryAsync,
                    AetheriaRuntimeDaemonSchemas.StarbridgeSessionSummary,
                    CultMesh.ProjectionSource(AetheriaRuntimeVerseRecordKeys.StarbridgeScenarioLatest.ToString()),
                    CultMesh.ProjectionSource(AetheriaRuntimeVerseRecordKeys.StarbridgeSessionLatest.ToString()),
                    CultMesh.ProjectionSource("catalog:aetheria.runtime")),
                seatId => Document<AetheriaRuntimeStarbridgePlayerSeatDocument>(
                    AetheriaRuntimeVerseRecordKeys.StarbridgePlayerSeat(seatId)));

            return state;

            CultMeshDocumentHandle<TDocument> ManagedFrameDocument<TDocument>(
                string documentId,
                Func<AetheriaRuntimeDaemonFrameDocument, Task<TDocument>> derive,
                string schemaId,
                params CultMeshProjectionSource[] additionalSources)
                where TDocument : class
            {
                var sources = new[]
                    {
                        CultMesh.ProjectionSource(
                            AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest.ToString(),
                            AetheriaRuntimeDaemonSchemas.Frame,
                            "latest authoritative daemon frame")
                    }
                    .Concat(additionalSources ?? Array.Empty<CultMeshProjectionSource>())
                    .Append(CultMesh.ProjectionSource(documentId, schemaId, "managed Aetheria client document"))
                    .ToArray();

                var verse = CultMesh.Verse("aetheria.local", RuntimeId);
                return CultMesh.Document(
                    documentId,
                    verse,
                    async _ => await derive(RequireManagedFrame()).ConfigureAwait(false),
                    _ => frameChanges
                        .SelectAwait(async (frame, cancellationToken) =>
                            await derive(frame).ConfigureAwait(false)),
                    sources: sources,
                    routeHint: new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "Aetheria managed typed state"));
            }

            AetheriaRuntimeDaemonFrameDocument RequireManagedFrame()
            {
                managedDaemonFrame ??= latestFrameDocument.Observe();
                _managedDaemonFrame = managedDaemonFrame;
                return managedDaemonFrame?.Current
                    ?? throw new InvalidOperationException("Aetheria Verse client has no daemon frame yet.");
            }

            AetheriaRuntimeCatalogSnapshot RequireManagedCatalog()
            {
                managedCatalog ??= catalogDocument.Observe();
                _managedCatalog = managedCatalog;
                return managedCatalog?.Current
                    ?? throw new InvalidOperationException("Aetheria Verse client has no runtime catalog document yet.");
            }

            AetheriaRuntimeLoadoutTemplatesDocument RequireManagedLoadoutTemplates()
            {
                managedLoadoutTemplates ??= loadoutTemplatesDocument.Observe();
                _managedLoadoutTemplates = managedLoadoutTemplates;
                return managedLoadoutTemplates?.Current
                    ?? throw new InvalidOperationException("Aetheria Verse client has no loadout templates document yet.");
            }

            CultMeshDocumentHandle<TDocument> BootstrapCatalogDocument<TDocument>(
                string documentId,
                Func<Task<TDocument>> bootstrap,
                string schemaId,
                params CultMeshProjectionSource[] additionalSources)
                where TDocument : class
            {
                var sources = (additionalSources ?? Array.Empty<CultMeshProjectionSource>())
                    .Append(CultMesh.ProjectionSource(documentId, schemaId, "managed Aetheria catalog document"))
                    .ToArray();

                var verse = CultMesh.Verse("aetheria.local", RuntimeId);
                return CultMesh.Document(
                    documentId,
                    verse,
                    async _ => await bootstrap().ConfigureAwait(false),
                    _ => frameChanges
                        .SelectAwait(async (_, cancellationToken) =>
                            await bootstrap().ConfigureAwait(false)),
                    sources: sources,
                    routeHint: new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "Aetheria managed catalog state"));
            }

            Task<AetheriaRuntimeStationRefitDocument> StationRefitAsync(
                AetheriaRuntimeDaemonFrameDocument frame)
            {
                return Task.FromResult(AetheriaRuntimeGameDocuments.StationRefit(
                    frame,
                    RequireManagedLoadoutTemplates().Templates,
                    RequireManagedCatalog()));
            }

            Task<AetheriaRuntimeStarbridgeSessionSummaryDocument> StarbridgeSummaryAsync(
                AetheriaRuntimeDaemonFrameDocument frame)
            {
                return Task.FromResult(AetheriaRuntimeStarbridgeDocuments.SessionSummary(
                    frame,
                    CurrentStarbridgeScenario(),
                    CurrentStarbridgeSession(),
                    RequireManagedCatalog()));
            }

            AetheriaRuntimeStarbridgeScenarioDocument? CurrentStarbridgeScenario()
            {
                try
                {
                    managedStarbridgeScenario ??= starbridgeScenarioDocument.Observe();
                    _managedStarbridgeScenario = managedStarbridgeScenario;
                    return managedStarbridgeScenario.Current;
                }
                catch (KeyNotFoundException)
                {
                    return null;
                }
                catch (MessagePack.FormatterNotRegisteredException)
                {
                    return null;
                }
            }

            AetheriaRuntimeStarbridgeSessionDocument? CurrentStarbridgeSession()
            {
                try
                {
                    managedStarbridgeSession ??= starbridgeSessionDocument.Observe();
                    _managedStarbridgeSession = managedStarbridgeSession;
                    return managedStarbridgeSession.Current;
                }
                catch (KeyNotFoundException)
                {
                    return null;
                }
                catch (MessagePack.FormatterNotRegisteredException)
                {
                    return null;
                }
            }

            static string IndexedDocumentId(string prefix, int index)
            {
                return $"{prefix}.{index.ToString(CultureInfo.InvariantCulture)}";
            }

            static string ViewportDocumentId(string prefix, AetheriaRuntimeViewportBounds viewport)
            {
                var normalized = AetheriaRuntimeGameDocuments.Normalize(viewport);
                return string.Join(
                    ".",
                    prefix,
                    ViewportToken(normalized.MinX),
                    ViewportToken(normalized.MinY),
                    ViewportToken(normalized.MaxX),
                    ViewportToken(normalized.MaxY));
            }

            static string ViewportToken(double value)
            {
                return value
                    .ToString("0.###", CultureInfo.InvariantCulture)
                    .Replace('-', 'n')
                    .Replace('.', 'p');
            }
        }

        private CultMeshMutableStatePointer<T> MutableDocumentPointer<T>(CultRecordKey key) where T : class
        {
            return CultMesh.MutableStatePointer(
                key.ToString(),
                _ => Database.GetAsync<T>(key),
                _ => Database.WatchRecord<T>(key)
                    .Where(change => change.Document != null)
                    .Select(change => change.Document!),
                async (_, value) => { await Database.PutAsync(key, value).ConfigureAwait(false); },
                sources: new[]
                {
                    CultMesh.ProjectionSource(key.ToString())
                });
        }

        private static CultMeshProjectionSource CatalogBootstrapSource(string sourceId)
        {
            return CultMesh.ProjectionSource(
                sourceId,
                description: "managed Aetheria document bootstrap seed");
        }

        private AetheriaRuntimeCatalogSnapshot BootstrapRuntimeCatalogSnapshot()
        {
            return AetheriaRuntimeCatalogStore.OpenReadOnly(StatePath);
        }

        public IReadOnlyList<AetheriaRuntimeCommittedCommandFactDocument> CommittedCommandFacts()
        {
            ThrowIfDisposed();
            return Cache.GetAll<AetheriaRuntimeCommittedCommandFactDocument>().ToArray();
        }

        private AetheriaRuntimeLoadoutTemplatesDocument BootstrapLoadoutTemplatesDocument()
        {
            return new AetheriaRuntimeLoadoutTemplatesDocument(
                AetheriaRuntimeCatalogStore.ReadLoadoutTemplates(StatePath));
        }

        private AetheriaRuntimePlayerSettingsDocument BootstrapPlayerSettingsDocument()
        {
            return AetheriaRuntimePlayerSettingsDocument.FromSnapshot(
                AetheriaRuntimeCatalogStore.ReadPlayerSettings(StatePath));
        }

        private AetheriaRuntimeVerseHostSettingsDocument BootstrapVerseHostSettingsDocument()
        {
            return AetheriaRuntimeVerseHostSettingsDocument.FromSnapshot(
                AetheriaRuntimeCatalogStore.ReadVerseHostSettings(StatePath));
        }
    }
}
