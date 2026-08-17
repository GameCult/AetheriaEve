using GameCult.Eve.Surface;
using Aetheria.State;
using Aetheria.State.Daemon;
using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

var checks = new AuthoritySmokeChecks();
await checks.RunAsync();
Console.WriteLine("Authority smoke passed.");

internal sealed class AuthoritySmokeChecks
{
    private static readonly object ProcessOutputLock = new();
    private static readonly Dictionary<int, List<string>> ProcessOutput = new();

    public async Task RunAsync()
    {
        DefaultTrustedPolicyAcceptsCommand();
        DelegatedRuntimeAcceptsOnlyListedRuntime();
        HostAuthoritativeAcceptsOnlyHostRuntime();
        InterestLeaseAcceptsOnlyActiveMatchingLease();
        UnsupportedAuthorityModesReject();
        AuthorizedCommandsReportsRejectedIds();
        PreRejectedCommandsEnterFrameReceipts();
        ClientTargetCarriesRuntimeIdentity();
        TwoLocalRuntimeDelegatedPolicyHarness();
        GameDocumentsBuildLocalViewportFromFrame();
        StarbridgeSessionSummaryProjectsScenarioFacts();
        await AetheriaClientStateDocumentsProjectAndSubmitAsync().ConfigureAwait(false);
        await DaemonOncePublishesStarbridgeSessionFactsAsync().ConfigureAwait(false);
        await SamePolicyDocumentCanBeLoadedByTwoNodesAsync().ConfigureAwait(false);
    }

    private static void DefaultTrustedPolicyAcceptsCommand()
    {
        var command = Command("pilot-client", "entity:raven");
        var decision = AetheriaRuntimeAuthorityRouter.Authorize(
            command,
            policy: null,
            leases: null,
            localRuntimeId: "aetheria-daemon");

        Require(decision.Authorized, "default trusted policy should accept trusted local commands");
        RequireEqual(AetheriaRuntimeAuthorityModes.AnyTrustedRuntime, decision.Mode, "default policy mode");
        RequireEqual("entity:raven", decision.SubjectKey, "default policy subject");
        RequireEqual(AetheriaRuntimeClaimKinds.Movement, decision.ClaimKind, "default policy claim");
    }

    private static void ClientTargetCarriesRuntimeIdentity()
    {
        var smokeId = Guid.NewGuid().ToString("N");
        var gameData = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"aetheria-client-target-{smokeId}"));
        try
        {
            var state = AetheriaState.At(gameData);
            var target = state.ClientTarget.Refresh();
            RequireEqual(AetheriaRuntimeStateBoundary.DefaultClientRuntimeId, target.RuntimeId, "client target default runtime id");

            target = state.ClientTarget.RequestRuntimeId("raven-test-runtime");
            RequireEqual("raven-test-runtime", target.RuntimeId, "client target should persist requested runtime id");

            var boot = AetheriaRuntimeStateBoot.Inspect(gameData);
            RequireEqual("raven-test-runtime", boot.RuntimeId, "boot report should expose client target runtime id");

            var previousRuntimeOverride = Environment.GetEnvironmentVariable(AetheriaRuntimeStateBoundary.RuntimeIdOverrideEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(AetheriaRuntimeStateBoundary.RuntimeIdOverrideEnvironmentVariable, "raven-env-runtime");
                boot = AetheriaRuntimeStateBoot.Inspect(gameData);
                RequireEqual("raven-env-runtime", boot.RuntimeId, "generic runtime id override should win");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AetheriaRuntimeStateBoundary.RuntimeIdOverrideEnvironmentVariable, previousRuntimeOverride);
            }
        }
        finally
        {
            try
            {
                gameData.Delete(recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void DelegatedRuntimeAcceptsOnlyListedRuntime()
    {
        var policy = Policy(
            AetheriaRuntimeAuthorityModes.AnyTrustedRuntime,
            new AetheriaRuntimeAuthorityRule
            {
                RuleId = "raven.movement",
                SubjectPrefix = "entity:raven",
                ClaimKinds = [AetheriaRuntimeClaimKinds.Movement],
                Mode = AetheriaRuntimeAuthorityModes.DelegatedRuntime,
                RuntimeIds = ["pilot-client"],
                Priority = 10
            });

        var accepted = AetheriaRuntimeAuthorityRouter.Authorize(
            Command("pilot-client", "entity:raven"),
            policy,
            leases: null,
            localRuntimeId: "commander-client");
        var rejected = AetheriaRuntimeAuthorityRouter.Authorize(
            Command("commander-client", "entity:raven"),
            policy,
            leases: null,
            localRuntimeId: "commander-client");

        Require(accepted.Authorized, "delegated runtime should accept listed runtime id");
        Require(!rejected.Authorized, "delegated runtime should reject unlisted runtime id");
        RequireEqual("delegated-runtime-required", rejected.Reason, "delegated rejection reason");
    }

    private static void HostAuthoritativeAcceptsOnlyHostRuntime()
    {
        var policy = Policy(AetheriaRuntimeAuthorityModes.HostAuthoritative);

        var accepted = AetheriaRuntimeAuthorityRouter.Authorize(
            Command("host-daemon", "entity:raven"),
            policy,
            leases: null,
            localRuntimeId: "host-daemon");
        var rejected = AetheriaRuntimeAuthorityRouter.Authorize(
            Command("pilot-client", "entity:raven"),
            policy,
            leases: null,
            localRuntimeId: "host-daemon");

        Require(accepted.Authorized, "host-authoritative should accept host runtime");
        Require(!rejected.Authorized, "host-authoritative should reject non-host runtime");
        RequireEqual("host-authority-required", rejected.Reason, "host-authoritative rejection reason");
    }

    private static void InterestLeaseAcceptsOnlyActiveMatchingLease()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = Policy(
            AetheriaRuntimeAuthorityModes.AnyTrustedRuntime,
            new AetheriaRuntimeAuthorityRule
            {
                RuleId = "hostile.combat.lease",
                SubjectPrefix = "entity:hostile",
                ClaimKinds = [AetheriaRuntimeClaimKinds.Combat],
                Mode = AetheriaRuntimeAuthorityModes.InterestLease,
                LeaseScope = "combat",
                Priority = 20
            });

        var leases = new[]
        {
            new AetheriaRuntimeAuthorityLeaseDocument
            {
                LeaseId = "lease:commander:hostile",
                RuntimeId = "commander-client",
                SubjectPrefix = "entity:hostile",
                ClaimKinds = [AetheriaRuntimeClaimKinds.Combat],
                Scope = "combat",
                ValidFromUtc = now.AddSeconds(-5).ToString("O"),
                ExpiresAtUtc = now.AddSeconds(30).ToString("O")
            }
        };

        var accepted = AetheriaRuntimeAuthorityRouter.Authorize(
            Command("commander-client", "entity:hostile:001", AetheriaRuntimeDaemonCommandKinds.FireWeaponGroup),
            policy,
            leases,
            localRuntimeId: "pilot-client");
        var rejected = AetheriaRuntimeAuthorityRouter.Authorize(
            Command("pilot-client", "entity:hostile:001", AetheriaRuntimeDaemonCommandKinds.FireWeaponGroup),
            policy,
            leases,
            localRuntimeId: "pilot-client");

        Require(accepted.Authorized, "interest lease should accept active matching lease");
        Require(!rejected.Authorized, "interest lease should reject missing runtime lease");
        RequireEqual("authority-lease-required", rejected.Reason, "interest lease rejection reason");
    }

    private static void UnsupportedAuthorityModesReject()
    {
        var policy = Policy(AetheriaRuntimeAuthorityModes.WitnessQuorum);
        var decision = AetheriaRuntimeAuthorityRouter.Authorize(
            Command("pilot-client", "entity:raven"),
            policy,
            leases: null,
            localRuntimeId: "pilot-client");

        Require(!decision.Authorized, "witness quorum should be represented but fail closed");
        RequireEqual("authority-mode-not-implemented", decision.Reason, "unsupported mode rejection reason");
    }

    private static void AuthorizedCommandsReportsRejectedIds()
    {
        var policy = Policy(AetheriaRuntimeAuthorityModes.HostAuthoritative);
        var host = Command("host-daemon", "entity:raven");
        var client = Command("pilot-client", "entity:raven");
        var rejectedIds = new List<string>();

        var accepted = AetheriaRuntimeAuthorityRouter.AuthorizedCommands(
            [host, client],
            policy,
            leases: null,
            localRuntimeId: "host-daemon",
            rejectedCommandIds: rejectedIds);

        RequireEqual(1, accepted.Count, "accepted command count");
        RequireEqual(host.CommandId, accepted[0].CommandId, "accepted command id");
        RequireEqual(1, rejectedIds.Count, "rejected command count");
        RequireEqual(client.CommandId, rejectedIds[0], "rejected command id");
    }

    private static void PreRejectedCommandsEnterFrameReceipts()
    {
        var command = Command("pilot-client", "entity:raven");
        using var physics = new AetheriaYmirWorldPhysics();
        var result = AetheriaRuntimeDaemonTickRunner.Tick(
            Path.Combine(Path.GetTempPath(), "aetheria-authority-smoke.cc"),
            new AetheriaRuntimeRunCheckpointCommit(),
            new AetheriaRuntimeDaemonTickOptions
            {
                DaemonId = "host-daemon",
                SessionId = "authority-smoke",
                FrameId = 42,
                SimulationTimeSeconds = 1,
                FixedDeltaSeconds = 0.02,
                WorldPhysics = physics,
                ObservedCommands = Array.Empty<AetheriaRuntimeDaemonCommandDocument>(),
                PreRejectedCommandIds = [command.CommandId],
                BuildPublications = false
            });

        Require(result.Frame.RejectedCommandIds.Contains(command.CommandId), "pre-rejected command should enter frame rejection receipts");
        Require(result.Frame.AccountedCommandIds.Contains(command.CommandId), "pre-rejected command should enter accounted receipts");
    }

    private static void TwoLocalRuntimeDelegatedPolicyHarness()
    {
        const string pilotRuntime = "pilot-client";
        const string commanderRuntime = "commander-client";
        var ravenKey = EntityKey("coop-smoke", 0, 0);
        var hostileKey = EntityKey("coop-smoke", 0, 1);
        var policy = CoopPolicy();

        var ravenByRaven = MovementCommand(pilotRuntime, ravenKey, directionX: 1, directionY: 0, magnitude: 1);
        var hostileByRaven = MovementCommand(pilotRuntime, hostileKey, directionX: 0, directionY: 1, magnitude: 1);
        var ravenFrame = TickWithPolicy(
            "pilot-local",
            InitialCoopRun(),
            policy,
            [ravenByRaven, hostileByRaven]);

        Require(ravenFrame.AppliedCommandIds.Contains(ravenByRaven.CommandId), "raven node should apply Raven-authored Raven movement");
        Require(ravenFrame.RejectedCommandIds.Contains(hostileByRaven.CommandId), "raven node should reject Raven-authored hostile movement");
        Require(ravenFrame.AccountedCommandIds.Contains(ravenByRaven.CommandId), "raven node should account applied Raven command");
        Require(ravenFrame.AccountedCommandIds.Contains(hostileByRaven.CommandId), "raven node should account rejected hostile command");

        var ravenByCommander = MovementCommand(commanderRuntime, ravenKey, directionX: -1, directionY: 0, magnitude: 1);
        var hostileByCommander = MovementCommand(commanderRuntime, hostileKey, directionX: 0, directionY: -1, magnitude: 1);
        var commanderFrame = TickWithPolicy(
            "commander-local",
            InitialCoopRun(),
            policy,
            [ravenByCommander, hostileByCommander]);

        Require(commanderFrame.RejectedCommandIds.Contains(ravenByCommander.CommandId), "commander node should reject Commander-authored Raven movement");
        Require(commanderFrame.AppliedCommandIds.Contains(hostileByCommander.CommandId), "commander node should apply Commander-authored hostile movement");
        Require(commanderFrame.AccountedCommandIds.Contains(ravenByCommander.CommandId), "commander node should account rejected Raven command");
        Require(commanderFrame.AccountedCommandIds.Contains(hostileByCommander.CommandId), "commander node should account applied hostile command");
    }

    private static void GameDocumentsBuildLocalViewportFromFrame()
    {
        var run = new AetheriaRuntimeRunCheckpointCommit
        {
            RunId = "game-documents-smoke",
            CurrentZoneIndex = 0,
            EntranceZoneIndex = 0,
            ExitZoneIndex = 1,
            DiscoveredZoneIndices = [0],
            CurrentEntityKey = EntityKey("game-documents-smoke", 0, 0),
            Zones =
            [
                new AetheriaRuntimeZoneSnapshotCommit
                {
                    ZoneIndex = 0,
                    Name = "Document Zone",
                    PositionX = 12,
                    PositionY = 34,
                    AdjacentZoneIndices = [1],
                    Entities =
                    [
                        SnapshotEntity(
                            0,
                            "Raven",
                            "player",
                            x: 0,
                            z: 0,
                            visibility: 80,
                            hull: 120,
                            cargoItem: "reactor-cell"),
                        SnapshotEntity(
                            1,
                            "Commander",
                            "player",
                            x: 240,
                            z: 0,
                            visibility: 160,
                            hull: 140,
                            cargoItem: "sensor-pack"),
                        SnapshotEntity(
                            2,
                            "Raider In Shared Sight",
                            "raider",
                            x: 360,
                            z: 0,
                            visibility: 60,
                            hull: 70,
                            cargoItem: "scrap"),
                        SnapshotEntity(
                            3,
                            "Outside Viewport",
                            "raider",
                            x: 900,
                            z: 0,
                            visibility: 60,
                            hull: 70,
                            cargoItem: "scrap")
                    ],
                    Bodies =
                    [
                        new AetheriaRuntimeBodySnapshotCommit
                        {
                            BodyKey = "body:near",
                            OrbitKey = "orbit:near",
                            Name = "Gravity Brush",
                            Kind = "planet",
                            GravityInfluenceCenterX = 390,
                            GravityInfluenceCenterZ = 0,
                            GravityInfluenceRadius = 75,
                            GravityWellDepth = 3,
                            GravityDepthExponent = 2,
                            GravityWaveRadius = 32,
                            GravityWaveDepth = 0.5,
                            GravityWaveSpeed = 1
                        },
                        new AetheriaRuntimeBodySnapshotCommit
                        {
                            BodyKey = "body:far",
                            OrbitKey = "orbit:far",
                            Name = "Too Far",
                            Kind = "planet",
                            GravityInfluenceCenterX = 1200,
                            GravityInfluenceCenterZ = 0,
                            GravityInfluenceRadius = 50
                        }
                    ]
                },
                new AetheriaRuntimeZoneSnapshotCommit
                {
                    ZoneIndex = 1,
                    Name = "Document Neighbor",
                    PositionX = 72,
                    PositionY = 34,
                    AdjacentZoneIndices = [0],
                    OwnerFactionIndex = 2,
                    FactionIndices = [2]
                }
            ]
        };
        var frame = AetheriaRuntimeDaemonFrameDocument.Create(
            run,
            "documents-daemon",
            "documents-session",
            12,
            0.24,
            0.02);

        var viewportDocument = AetheriaRuntimeGameDocuments.Viewport(
            frame,
            new AetheriaRuntimeViewportBounds
            {
                MinX = 500,
                MinY = -100,
                MaxX = -50,
                MaxY = 100
            });
        var objectsViewport = AetheriaRuntimeGameDocuments.ObjectsViewport(
            frame,
            viewportDocument.Viewport);
        var gravityViewport = AetheriaRuntimeGameDocuments.GravityViewport(
            frame,
            viewportDocument.Viewport);
        var currentZone = AetheriaRuntimeGameDocuments.CurrentZone(frame);
        var sectorMap = AetheriaRuntimeGameDocuments.SectorMap(frame);

        RequireEqual(AetheriaRuntimeDaemonSchemas.GameViewport, viewportDocument.Schema, "viewport document schema");
        RequireEqual(AetheriaRuntimeDaemonSchemas.ObjectsViewport, objectsViewport.Schema, "objects viewport document schema");
        RequireEqual(AetheriaRuntimeDaemonSchemas.GravityViewport, gravityViewport.Schema, "gravity viewport document schema");
        RequireEqual(AetheriaRuntimeDaemonSchemas.CurrentZone, currentZone.Schema, "current-zone document schema");
        RequireEqual(AetheriaRuntimeDaemonSchemas.SectorMap, sectorMap.Schema, "sector-map document schema");
        RequireEqual("Document Zone", currentZone.ZoneName, "current-zone document zone name");
        RequireEqual(12.0, currentZone.PositionX, "current-zone document position x");
        RequireEqual(34.0, currentZone.PositionY, "current-zone document position y");
        RequireEqual(2, sectorMap.Zones.Count, "sector-map document zone count");
        Require(sectorMap.Zones.Any(zone => zone.ZoneIndex == 0 && zone.Current && zone.Entrance && zone.Discovered), "sector-map should mark current entrance zone");
        Require(sectorMap.Zones.Any(zone => zone.ZoneIndex == 1 && zone.Exit && !zone.Discovered), "sector-map should mark undiscovered exit zone");
        Require(sectorMap.Links.Any(link => link.FromZoneIndex == 0 && link.ToZoneIndex == 1 && !link.Discovered), "sector-map should include normalized topology link");
        RequireEqual(12L, viewportDocument.FrameId, "viewport document frame id");
        RequireEqual(-50.0, viewportDocument.Viewport.MinX, "normalized viewport min x");
        RequireEqual(500.0, viewportDocument.Viewport.MaxX, "normalized viewport max x");
        Require(viewportDocument.ControlledEntityIndices.SequenceEqual([0, 1]), "viewport document should expose controlled unit set");
        Require(viewportDocument.Objects.Any(item => item.DisplayName == "Raider In Shared Sight"), "viewport document should use union of controlled unit visibility");
        Require(!viewportDocument.Objects.Any(item => item.DisplayName == "Outside Viewport"), "viewport document should exclude objects outside viewport");
        Require(objectsViewport.Objects.Select(item => item.EntityIndex).SequenceEqual(viewportDocument.Objects.Select(item => item.EntityIndex)), "objects viewport should match composed map object set");
        var raven = viewportDocument.Objects.First(item => item.DisplayName == "Raven");
        RequireEqual(120.0, raven.Status.Hull, "viewport document should include entity status");
        Require(raven.Inventory.Any(item => item.ItemKey == "reactor-cell" && item.Source == "cargo"), "viewport document should include inventory");
        Require(viewportDocument.GravityInfluences.Any(item => item.BodyKey == "body:near"), "viewport document should include intersecting gravity influence");
        Require(!viewportDocument.GravityInfluences.Any(item => item.BodyKey == "body:far"), "viewport document should exclude non-intersecting gravity influence");
        Require(gravityViewport.GravityInfluences.Select(item => item.BodyKey).SequenceEqual(viewportDocument.GravityInfluences.Select(item => item.BodyKey)), "gravity viewport should match composed map gravity set");
    }

    private static void StarbridgeSessionSummaryProjectsScenarioFacts()
    {
        var baseEntity = SnapshotEntity(
            4,
            "Starbridge Base",
            "player",
            x: 0,
            z: 0,
            visibility: 500,
            hull: 850,
            cargoItem: "coolant-beam");
        baseEntity.Kind = "station";

        var run = new AetheriaRuntimeRunCheckpointCommit
        {
            RunId = "starbridge-session-smoke",
            CurrentZoneIndex = 0,
            Zones =
            [
                new AetheriaRuntimeZoneSnapshotCommit
                {
                    ZoneIndex = 0,
                    Name = "Starbridge Defense",
                    Entities =
                    [
                        baseEntity,
                        SnapshotEntity(5, "Pilot Raven", "player", 120, 0, 300, 140, "repair-pod")
                    ]
                }
            ]
        };
        var frame = AetheriaRuntimeDaemonFrameDocument.Create(
            run,
            "starbridge-daemon",
            "starbridge-smoke-session",
            19,
            0.38,
            0.02);
        var scenario = new AetheriaRuntimeStarbridgeScenarioDocument
        {
            ScenarioId = "starbridge.frontier-fabricator",
            DisplayName = "Frontier Fabricator Defense",
            StartingBaseKey = EntityKey("starbridge-session-smoke", 0, 4),
            StationStock =
            [
                new AetheriaRuntimeStarbridgeStationStockItem
                {
                    ItemKey = "coolant-beam",
                    Quantity = 2,
                    Quality = 0.75,
                    Durability = 1
                }
            ],
            Waves =
            [
                new AetheriaRuntimeStarbridgeWaveDefinition
                {
                    WaveIndex = 0,
                    DisplayName = "Scout Probe",
                    AttackerKeys = ["scout"],
                    BossKey = "scout-captain",
                    RecoveredTechnologyKeys = ["sensor-calibration"]
                },
                new AetheriaRuntimeStarbridgeWaveDefinition
                {
                    WaveIndex = 1,
                    DisplayName = "Bomber Line",
                    AttackerKeys = ["bomber", "skirmisher"],
                    BossKey = "bomber-frame",
                    RecoveredTechnologyKeys = ["missile-rack-burst"]
                }
            ],
            RuntimeRoles =
            [
                new AetheriaRuntimeStarbridgeRuntimeRole
                {
                    RuntimeId = "commander-client",
                    Role = "commander"
                },
                new AetheriaRuntimeStarbridgeRuntimeRole
                {
                    RuntimeId = "pilot-client",
                    Role = "pilot",
                    EntityKey = EntityKey("starbridge-session-smoke", 0, 5)
                }
            ]
        };
        var session = new AetheriaRuntimeStarbridgeSessionDocument
        {
            SessionId = "starbridge-smoke-session",
            ScenarioId = scenario.ScenarioId,
            RunId = run.RunId,
            BaseEntityKey = EntityKey("starbridge-session-smoke", 0, 4),
            StationEntityKey = EntityKey("starbridge-session-smoke", 0, 4),
            Phase = "pre-wave",
            CurrentWaveIndex = 1
        };

        var summary = AetheriaRuntimeStarbridgeDocuments.SessionSummary(frame, scenario, session);

        RequireEqual(AetheriaRuntimeDaemonSchemas.StarbridgeSessionSummary, summary.Schema, "starbridge session summary schema");
        RequireEqual("starbridge.frontier-fabricator", summary.ScenarioId, "starbridge scenario id");
        RequireEqual("Frontier Fabricator Defense", summary.ScenarioName, "starbridge scenario name");
        RequireEqual("pre-wave", summary.Phase, "starbridge session phase");
        RequireEqual("Starbridge Base", summary.BaseStatus.DisplayName, "starbridge base status name");
        RequireEqual(850.0, summary.BaseStatus.Hull, "starbridge base hull");
        Require(summary.StationStock.Any(item => item.ItemKey == "coolant-beam"), "starbridge station stock should project scenario stock");
        Require(summary.WaveForecast.Length == 1 && summary.WaveForecast[0].DisplayName == "Bomber Line", "starbridge wave forecast should start at current wave");
        Require(summary.RuntimeRoles.Any(role => role.RuntimeId == "commander-client" && role.Role == "commander"), "starbridge runtime roles should project scenario roles");

        var gameSurface = AetheriaRuntimeDaemonGameSurfaceBuilder.BuildCommander(
            frame,
            new AetheriaRuntimeDaemonHealthDocument
            {
                DaemonId = "starbridge-daemon",
                VerseId = "aetheria.starbridge-smoke",
                Status = "healthy"
            },
            AetheriaRuntimeDaemonCommandBoundaryDocument.Create("starbridge-daemon"),
            summary);
        RequireSurfaceMetric(gameSurface, "aetheria.starbridge.commander.session.scenario", "Frontier Fabricator Defense", "commander surface starbridge scenario");
        RequireSurfaceMetric(gameSurface, "aetheria.starbridge.commander.session.base", "Starbridge Base", "commander surface starbridge base");
        RequireSurfaceMetric(gameSurface, "aetheria.daemon.game.starbridge.stock.0.item", "coolant-beam", "daemon game surface starbridge stock");
        RequireSurfaceMetric(gameSurface, "aetheria.daemon.game.starbridge.wave.0.name", "Bomber Line", "daemon game surface starbridge wave forecast");
        RequireSurfaceMetric(gameSurface, "aetheria.daemon.game.starbridge.role.0.runtime", "commander-client", "daemon game surface starbridge runtime role");
    }

    private static async Task AetheriaClientStateDocumentsProjectAndSubmitAsync()
    {
        var smokeId = Guid.NewGuid().ToString("N");
        var statePath = Path.Combine(Path.GetTempPath(), $"aetheria-client-state-documents-{smokeId}.cc");
        var frame = AetheriaRuntimeDaemonFrameDocument.Create(
            new AetheriaRuntimeRunCheckpointCommit
            {
                RunId = "client-state-documents-smoke",
                CurrentZoneIndex = 0,
                CurrentEntityKey = EntityKey("client-state-documents-smoke", 0, 0),
                Zones =
                [
                    new AetheriaRuntimeZoneSnapshotCommit
                    {
                        ZoneIndex = 0,
                        Name = "Client State Documents Zone",
                        Entities =
                        [
                            SnapshotEntity(0, "Document Raven", "player", 0, 0, 600, 140, "document-cargo"),
                            SnapshotEntity(1, "Document Raider", "raider", 100, 0, 120, 80, "raider-cargo")
                        ]
                    }
                ]
            },
            "state-documents-daemon",
            "state-documents-session",
            7,
            0.14,
            0.02);
        var health = new AetheriaRuntimeDaemonHealthDocument
        {
            DaemonId = "state-documents-daemon",
            VerseId = "aetheria.state-documents-smoke",
            FrameId = frame.FrameId,
            Status = "healthy",
            Transport = "cultcache-witness"
        };
        var policy = AetheriaRuntimeVerseAuthorityPolicyDocument.TrustedCoop(
            "aetheria.state-documents-smoke",
            "state-documents-daemon");
        var scenario = new AetheriaRuntimeStarbridgeScenarioDocument
        {
            ScenarioId = "starbridge.state-documents",
            DisplayName = "Document Starbridge",
            StartingBaseKey = EntityKey("client-state-documents-smoke", 0, 0),
            StationStock =
            [
                new AetheriaRuntimeStarbridgeStationStockItem
                {
                    ItemKey = "document-cargo",
                    Quantity = 3,
                    Quality = 1,
                    Durability = 1
                }
            ],
            Waves =
            [
                new AetheriaRuntimeStarbridgeWaveDefinition
                {
                    WaveIndex = 0,
                    DisplayName = "Document Wave",
                    AttackerKeys = ["raider"],
                    BossKey = "document-boss"
                }
            ]
        };
        var session = new AetheriaRuntimeStarbridgeSessionDocument
        {
            SessionId = "state-documents-session",
            ScenarioId = "starbridge.state-documents",
            BaseEntityKey = EntityKey("client-state-documents-smoke", 0, 0),
            Phase = "pre-wave"
        };

        using (var writer = await AetheriaRuntimeVerseClient
            .OpenAsync(statePath, "state-documents-writer", startServer: false, pullOnOpen: false)
            .ConfigureAwait(false))
        {
            await writer.Database
                .PutAsync(AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest, frame)
                .ConfigureAwait(false);
            await writer.Database
                .PutAsync(AetheriaRuntimeVerseRecordKeys.DaemonHealth, health)
                .ConfigureAwait(false);
            await writer.MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy)
                .ReplaceAsync(policy)
                .ConfigureAwait(false);
            await writer.MutableDocument<AetheriaRuntimeStarbridgeScenarioDocument>(AetheriaRuntimeVerseRecordKeys.StarbridgeScenarioLatest)
                .ReplaceAsync(scenario)
                .ConfigureAwait(false);
            await writer.MutableDocument<AetheriaRuntimeStarbridgeSessionDocument>(AetheriaRuntimeVerseRecordKeys.StarbridgeSessionLatest)
                .ReplaceAsync(session)
                .ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        using var client = await AetheriaClient
            .OpenAsync(statePath, "pilot-client", sessionId: "state-documents-session", pullOnOpen: true)
            .ConfigureAwait(false);
        var state = client.State;
        var viewportBounds = new AetheriaRuntimeViewportBounds { MinX = -20, MinY = -20, MaxX = 150, MaxY = 20 };
        var viewport = await state
            .GameViewport(viewportBounds)
            .LatestAsync()
            .ConfigureAwait(false);
        Require(viewport.Objects.Any(item => item.DisplayName == "Document Raven"), "managed map document should include controlled object");
        Require(viewport.Objects.Any(item => item.DisplayName == "Document Raider"), "managed map document should include visible hostile object");
        var objectsViewport = await state
            .ObjectsViewport(viewportBounds)
            .LatestAsync()
            .ConfigureAwait(false);
        RequireEqual(AetheriaRuntimeDaemonSchemas.ObjectsViewport, objectsViewport.Schema, "managed objects viewport document schema");
        Require(objectsViewport.Objects.Any(item => item.DisplayName == "Document Raven"), "managed objects viewport document should include controlled object");
        Require(objectsViewport.Objects.Any(item => item.DisplayName == "Document Raider"), "managed objects viewport document should include visible hostile object");
        var gravityViewport = await state
            .GravityViewport(viewportBounds)
            .LatestAsync()
            .ConfigureAwait(false);
        RequireEqual(AetheriaRuntimeDaemonSchemas.GravityViewport, gravityViewport.Schema, "managed gravity viewport document schema");

        var currentZone = await state.CurrentZone.LatestAsync().ConfigureAwait(false);
        RequireEqual(AetheriaRuntimeDaemonSchemas.CurrentZone, currentZone.Schema, "managed current-zone document schema");
        RequireEqual("Client State Documents Zone", currentZone.ZoneName, "managed current-zone document name");

        var sectorMap = await state.SectorMap.LatestAsync().ConfigureAwait(false);
        RequireEqual(AetheriaRuntimeDaemonSchemas.SectorMap, sectorMap.Schema, "managed sector-map document schema");
        Require(sectorMap.Zones.Any(zone => zone.ZoneIndex == currentZone.ZoneIndex && zone.Current), "managed sector-map document should include current zone marker");

        var selected = await state.SelectedObject(0).LatestAsync().ConfigureAwait(false);
        RequireEqual(AetheriaRuntimeDaemonSchemas.SelectedObject, selected.Schema, "managed selected-object document schema");
        Require(selected.Selected?.DisplayName == "Document Raven", "managed selected-object document should resolve entity");

        var inventory = await state.Inventory(0).LatestAsync().ConfigureAwait(false);
        RequireEqual(AetheriaRuntimeDaemonSchemas.Inventory, inventory.Schema, "managed inventory document schema");
        Require(inventory.Cargo.Any(item => item.ItemKey == "document-cargo"), "managed inventory document should expose cargo");

        var starbridgeSummary = await state.StarbridgeSummary.LatestAsync().ConfigureAwait(false);
        RequireEqual("Document Starbridge", starbridgeSummary.ScenarioName, "managed starbridge summary document should project scenario name");
        RequireEqual("pre-wave", starbridgeSummary.Phase, "managed starbridge summary document should project session phase");
        Require(starbridgeSummary.StationStock.Any(item => item.ItemKey == "document-cargo"), "managed starbridge summary document should expose station stock");

        var healthDocument = await state.Health.LatestAsync().ConfigureAwait(false);
        Require(healthDocument?.Status == "healthy", "managed daemon health document should read local health");

        var authorityPolicy = await state.AuthorityPolicy.LatestAsync().ConfigureAwait(false);
        RequireEqual("aetheria.trusted-coop.v1", authorityPolicy?.PolicyId, "managed authority policy document id");

        var command = client.SetMoveVector(1, 0, 0.5);
        RequireEqual(AetheriaRuntimeDaemonCommandKinds.SetMoveVector, command.Kind, "managed client command kind");
        RequireEqual("pilot-client", command.ClientId, "managed client command client id");
        RequireEqual(frame.FrameId, command.ObservedFrameId, "managed client command observed frame id");

        using var reader = await AetheriaRuntimeVerseClient
            .OpenAsync(statePath, "state-documents-reader", startServer: false, pullOnOpen: true)
            .ConfigureAwait(false);
        var stored = await reader.Database
            .GetAsync<AetheriaRuntimeDaemonCommandDocument>(
                AetheriaRuntimeVerseRecordKeys.DaemonCommand(command.CommandId))
            .ConfigureAwait(false);
        Require(stored != null, "managed client command should be stored as typed daemon command document");
        RequireEqual(AetheriaRuntimeClaimKinds.Movement, stored!.ClaimKind, "managed client command claim kind");
    }

    private static async Task DaemonOncePublishesStarbridgeSessionFactsAsync()
    {
        var smokeId = Guid.NewGuid().ToString("N");
        var statePath = Path.Combine(Path.GetTempPath(), $"aetheria-starbridge-daemon-{smokeId}.cc");
        var clientCultMeshPort = GetFreeUdpPort();
        const string runtimeId = "starbridge-daemon-smoke";

        await SeedCanonicalCatalogAsync(statePath, runtimeId).ConfigureAwait(false);
        await RunDaemonOnceAsync(
            statePath,
            runtimeId,
            clientCultMeshPort,
            useTerminusFixture: true).ConfigureAwait(false);

        using var client = await AetheriaClient
            .OpenAsync(statePath, "starbridge-smoke-client", sessionId: "authority-smoke", pullOnOpen: true)
            .ConfigureAwait(false);
        var summary = await client.State.StarbridgeSummary.LatestAsync().ConfigureAwait(false);

        RequireEqual(AetheriaRuntimeDaemonSchemas.StarbridgeSessionSummary, summary.Schema, "daemon starbridge summary schema");
        RequireEqual("starbridge.frontier-fabricator", summary.ScenarioId, "daemon starbridge scenario id");
        RequireEqual("Frontier Fabricator Defense", summary.ScenarioName, "daemon starbridge scenario name");
        RequireEqual("authority-smoke", summary.SessionId, "daemon starbridge session id");
        RequireEqual("pre-wave", summary.Phase, "daemon starbridge phase");
        RequireEqual("Anchor Station", summary.BaseStatus.DisplayName, "daemon starbridge base name");
        Require(summary.StationStock.Any(item => item.ItemKey == "repair-parts"), "daemon starbridge summary should expose station stock");
        Require(summary.WaveForecast.Any(wave => wave.DisplayName == "Scout Probe"), "daemon starbridge summary should expose wave forecast");
        Require(summary.RuntimeRoles.Any(role => role.RuntimeId == "commander-client" && role.Role == "commander"), "daemon starbridge summary should expose commander role");
        Require(summary.RuntimeRoles.Any(role => role.RuntimeId == "pilot-client" && role.Role == "pilot"), "daemon starbridge summary should expose pilot role");
    }

    private static async Task SamePolicyDocumentCanBeLoadedByTwoNodesAsync()
    {
        var smokeId = Guid.NewGuid().ToString("N");
        var ravenPath = Path.Combine(Path.GetTempPath(), $"aetheria-authority-raven-{smokeId}.cc");
        var commanderPath = Path.Combine(Path.GetTempPath(), $"aetheria-authority-commander-{smokeId}.cc");
        var policy = CoopPolicy();

        await using var ravenNode = await AetheriaStateNode.OpenAsync(
            ravenPath,
            runtimeId: "pilot-local",
            startServer: false,
            enableDurableShardLogs: false).ConfigureAwait(false);
        await using var commanderNode = await AetheriaStateNode.OpenAsync(
            commanderPath,
            runtimeId: "commander-local",
            startServer: false,
            enableDurableShardLogs: false).ConfigureAwait(false);

        await ravenNode.MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy)
            .ReplaceAsync(policy)
            .ConfigureAwait(false);
        await commanderNode.MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy)
            .ReplaceAsync(policy)
            .ConfigureAwait(false);

        var ravenPolicy = await ravenNode.MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy).ReadAsync().ConfigureAwait(false);
        var commanderPolicy = await commanderNode.MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy).ReadAsync().ConfigureAwait(false);

        Require(ravenPolicy != null, "raven node should load authority policy");
        Require(commanderPolicy != null, "commander node should load authority policy");
        RequireEqual(policy.PolicyId, ravenPolicy!.PolicyId, "raven loaded policy id");
        RequireEqual(policy.PolicyId, commanderPolicy!.PolicyId, "commander loaded policy id");
        RequireEqual(policy.Rules.Length, ravenPolicy.Rules.Length, "raven loaded rule count");
        RequireEqual(policy.Rules.Length, commanderPolicy.Rules.Length, "commander loaded rule count");
    }


    private static async Task SeedCanonicalCatalogAsync(string statePath, string runtimeId)
    {
        await using var node = await AetheriaStateNode.OpenAsync(
            statePath,
            runtimeId,
            startServer: false,
            enableDurableShardLogs: false).ConfigureAwait(false);

        const string manufacturerLegacyId = "authority-smoke-player";
        foreach (var corporationLegacyId in new[]
                 {
                     manufacturerLegacyId,
                     "authority-smoke-raider",
                     "authority-smoke-neutral"
                 })
        {
            await node.MutableDocument<AetheriaCorporation>(
                    AetheriaCatalogKeys.CorporationFromLegacyId(corporationLegacyId))
                .ReplaceAsync(new AetheriaCorporation
                {
                    Name = corporationLegacyId,
                    LegacyId = corporationLegacyId,
                    ShortName = corporationLegacyId,
                    InfluenceDistance = 1,
                    AllegianceCount = 1,
                    Allegiances =
                    [
                        new AetheriaCorporationAllegiance
                        {
                            CorporationLegacyId = manufacturerLegacyId,
                            Weight = 1
                        }
                    ]
                }).ConfigureAwait(false);
        }

        foreach (var item in AuthoritySmokeLoadoutItems(manufacturerLegacyId))
        {
            await node.MutableDocument<AetheriaItemDefinition>(
                    AetheriaCatalogKeys.ItemDefinitionFromLegacyId(item.LegacyId))
                .ReplaceAsync(item).ConfigureAwait(false);
        }
        var baseEntityKey = AetheriaRuntimeRunCheckpointCommit.EntityRecordKey("local-terminus", 0, 0);
        const string scenarioId = "starbridge.frontier-fabricator";
        await node.MutableDocument<AetheriaRuntimeStarbridgeScenarioDocument>(
                AetheriaRuntimeVerseRecordKeys.StarbridgeScenarioLatest)
            .ReplaceAsync(new AetheriaRuntimeStarbridgeScenarioDocument
            {
                ScenarioId = scenarioId,
                DisplayName = "Frontier Fabricator Defense",
                StartingBaseKey = baseEntityKey,
                StationStock =
                [
                    new AetheriaRuntimeStarbridgeStationStockItem
                    {
                        ItemKey = "repair-parts",
                        Quantity = 4,
                        Quality = 1,
                        Durability = 1
                    }
                ],
                Waves =
                [
                    new AetheriaRuntimeStarbridgeWaveDefinition
                    {
                        WaveIndex = 0,
                        DisplayName = "Scout Probe",
                        AttackerKeys = ["scout"]
                    }
                ],
                RuntimeRoles =
                [
                    new AetheriaRuntimeStarbridgeRuntimeRole { RuntimeId = "commander-client", Role = "commander" },
                    new AetheriaRuntimeStarbridgeRuntimeRole
                    {
                        RuntimeId = "pilot-client",
                        Role = "pilot",
                        EntityKey = AetheriaRuntimeRunCheckpointCommit.EntityRecordKey("local-terminus", 0, 1)
                    }
                ]
            }).ConfigureAwait(false);
        await node.MutableDocument<AetheriaRuntimeStarbridgeSessionDocument>(
                AetheriaRuntimeVerseRecordKeys.StarbridgeSessionLatest)
            .ReplaceAsync(new AetheriaRuntimeStarbridgeSessionDocument
            {
                SessionId = "authority-smoke",
                ScenarioId = scenarioId,
                RunId = "local-terminus",
                BaseEntityKey = baseEntityKey,
                StationEntityKey = baseEntityKey,
                Phase = "pre-wave"
            }).ConfigureAwait(false);
        await node.FlushAsync().ConfigureAwait(false);
        await node.RefreshRuntimeCatalogAsync().ConfigureAwait(false);

        var catalog = await node.RuntimeCatalog().LatestAsync().ConfigureAwait(false);
        Require(catalog.Corporations.Any(corporation => !string.IsNullOrWhiteSpace(corporation.CorporationKey)),
            "daemon fixture must contain a typed corporation key");
        Require(catalog.Items.Any(item => item.HullType == "Ship") &&
                catalog.Items.Any(item => item.HullType == "Station"),
            "daemon fixture must contain typed ship and station hulls");
    }

    private static IReadOnlyList<AetheriaItemDefinition> AuthoritySmokeLoadoutItems(string manufacturerLegacyId)
    {
        static AetheriaShapeCell[] Rectangle(int width, int height) =>
            Enumerable.Range(0, height)
                .SelectMany(y => Enumerable.Range(0, width).Select(x => new AetheriaShapeCell { X = x, Y = y }))
                .ToArray();

        static AetheriaBehaviorPayload Behavior(string kind) => new()
        {
            Kind = kind,
            BehaviorId = "authority-smoke." + kind.ToLowerInvariant()
        };

        AetheriaItemDefinition Item(
            string legacyId,
            string category,
            string hardpointType,
            string behaviorKind = "") => new()
        {
            Name = legacyId,
            LegacyId = legacyId,
            Category = category,
            ManufacturerLegacyId = manufacturerLegacyId,
            Price = 10,
            Mass = 1,
            Volume = 1,
            ShapeWidth = 1,
            ShapeHeight = 1,
            OccupiedCells = 1,
            ShapeCells = Rectangle(1, 1),
            HardpointType = hardpointType,
            BehaviorKinds = string.IsNullOrWhiteSpace(behaviorKind) ? [] : [behaviorKind],
            BehaviorCount = string.IsNullOrWhiteSpace(behaviorKind) ? 0 : 1,
            BehaviorPayloads = string.IsNullOrWhiteSpace(behaviorKind) ? [] : [Behavior(behaviorKind)]
        };

        AetheriaItemDefinition Hull(string legacyId, string hullType)
        {
            var hull = Item(legacyId, AetheriaRuntimeItemCategories.Hull, "Hull");
            hull.HullType = hullType;
            hull.ShapeWidth = 6;
            hull.ShapeHeight = 6;
            hull.OccupiedCells = 36;
            hull.ShapeCells = Rectangle(6, 6);
            hull.Hardpoints =
            [
                new AetheriaItemHardpoint
                {
                    Type = "ControlModule",
                    ShapeWidth = 1,
                    ShapeHeight = 1,
                    OccupiedCells = 1,
                    ShapeCells = Rectangle(1, 1)
                }
            ];
            return hull;
        }

        var cargo = Item("authority-smoke-cargo", AetheriaRuntimeItemCategories.CargoBay, "Internal");
        cargo.InteriorShapeWidth = 3;
        cargo.InteriorShapeHeight = 3;
        cargo.InteriorOccupiedCells = 9;
        cargo.InteriorShapeCells = Rectangle(3, 3);

        return
        [
            Hull("authority-smoke-ship", "Ship"),
            Hull("authority-smoke-station", "Station"),
            Hull("82efc0a5-1ba5-4ff3-a281-b2e6e247521d", "Station"),
            Item("authority-smoke-cockpit", AetheriaRuntimeItemCategories.Gear, "ControlModule", "Cockpit"),
            Item("authority-smoke-turret-controller", AetheriaRuntimeItemCategories.Gear, "ControlModule", "TurretController"),
            cargo,
            Item("authority-smoke-docking", AetheriaRuntimeItemCategories.DockingBay, "Internal"),
            Item("3e930a2c-ac72-4385-98aa-1c5b0b90db46", AetheriaRuntimeItemCategories.DockingBay, "Internal"),
            Item("authority-smoke-reactor", AetheriaRuntimeItemCategories.Gear, "Internal", "Reactor"),
            Item("authority-smoke-capacitor", AetheriaRuntimeItemCategories.Gear, "Internal", "Capacitor")
        ];
    }

    private static async Task RunDaemonOnceAsync(
        string statePath,
        string runtimeId,
        int clientCultMeshPort,
        bool useTerminusFixture = false)
    {
        using var process = StartDaemonProcess(
            statePath,
            runtimeId,
            clientCultMeshPort,
            once: true,
            useTerminusFixture);
        await WaitForProcessSuccessAsync(process, runtimeId, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
    }

    private static Process StartDaemonProcess(
        string statePath,
        string runtimeId,
        int clientCultMeshPort,
        bool once,
        bool useTerminusFixture = false)
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var executablePath = Path.Combine(
            repoRoot,
            "Aetheria.State.Daemon",
            "bin",
            "Debug",
            "net10.0",
            "Aetheria.State.Daemon.exe");
        var dllPath = Path.Combine(
            repoRoot,
            "Aetheria.State.Daemon",
            "bin",
            "Debug",
            "net10.0",
            "Aetheria.State.Daemon.dll");
        var projectPath = Path.Combine(
            repoRoot,
            "Aetheria.State.Daemon",
            "Aetheria.State.Daemon.csproj");
        var executableExists = File.Exists(executablePath);
        var dllExists = File.Exists(dllPath);
        var commandPrefix = executableExists
            ? Quote(executablePath)
            : dllExists
                ? "dotnet " + Quote(dllPath)
                : "dotnet run --project " + Quote(projectPath) + " --";
        var arguments = string.Join(
            " ",
            "--state",
            Quote(statePath),
            "--daemon-id",
            runtimeId,
            "--verse-id",
            "aetheria.coop-smoke",
            "--session-id",
            "authority-smoke",
            "--client-cultmesh-port",
            clientCultMeshPort.ToString(),
            "--api-publication-interval-ms",
            "50",
            useTerminusFixture ? "--terminus-scenario standard" : "",
            once ? "--once" : "");
        var startInfo = executableExists
            ? new ProcessStartInfo(executablePath, arguments)
            : dllExists
                ? new ProcessStartInfo("dotnet", Quote(dllPath) + " " + arguments)
                : new ProcessStartInfo("dotnet", "run --project " + Quote(projectPath) + " -- " + arguments);
        startInfo.WorkingDirectory = repoRoot;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        if (string.IsNullOrWhiteSpace(commandPrefix))
        {
            throw new InvalidOperationException("Cannot resolve daemon command.");
        }

        var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start Aetheria daemon child process.");
        CaptureProcessOutput(process, runtimeId);
        return process;
    }

    private static async Task WaitForProcessSuccessAsync(
        Process process,
        string runtimeId,
        TimeSpan timeout)
    {
        var waitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(timeout))
            .ConfigureAwait(false);
        if (completed != waitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException($"Aetheria daemon child process timed out for {runtimeId}.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Aetheria daemon child process failed for {runtimeId} with exit code {process.ExitCode}.\n{GetProcessOutputTail(process)}");
        }
    }

    private static void CaptureProcessOutput(Process process, string runtimeId)
    {
        lock (ProcessOutputLock)
            ProcessOutput[process.Id] = new List<string>();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                AppendProcessOutput(process.Id, $"{runtimeId} stdout: {eventArgs.Data}");
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                AppendProcessOutput(process.Id, $"{runtimeId} stderr: {eventArgs.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static void AppendProcessOutput(int processId, string line)
    {
        lock (ProcessOutputLock)
        {
            if (!ProcessOutput.TryGetValue(processId, out var lines))
            {
                lines = new List<string>();
                ProcessOutput[processId] = lines;
            }

            lines.Add(line);
            if (lines.Count > 80)
                lines.RemoveRange(0, lines.Count - 80);
        }
    }

    private static string GetProcessOutputTail(Process process)
    {
        lock (ProcessOutputLock)
        {
            if (!ProcessOutput.TryGetValue(process.Id, out var lines) || lines.Count == 0)
                return "No child output captured.";

            var builder = new StringBuilder();
            foreach (var line in lines)
                builder.AppendLine(line);
            return builder.ToString();
        }
    }


    private static string ProcessDiagnostics(IReadOnlyList<Process> childProcesses)
    {
        var lines = new List<string>();
        foreach (var process in childProcesses ?? Array.Empty<Process>())
        {
            try
            {
                lines.Add(process.HasExited
                    ? $" Child pid={process.Id} exited code={process.ExitCode}."
                    : $" Child pid={process.Id} still running.");
                lines.Add(Environment.NewLine);
                lines.Add(GetProcessOutputTail(process));
            }
            catch (Exception ex)
            {
                lines.Add($" Child diagnostics failed: {ex.GetType().Name}: {ex.Message}.");
            }
        }

        return lines.Count == 0 ? "" : string.Concat(lines);
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            process.WaitForExit(5000);
        }
        catch
        {
        }
    }

    private static AetheriaRuntimeDaemonFrameDocument TickWithPolicy(
        string daemonId,
        AetheriaRuntimeRunCheckpointCommit run,
        AetheriaRuntimeVerseAuthorityPolicyDocument policy,
        IReadOnlyList<AetheriaRuntimeDaemonCommandDocument> observedCommands)
    {
        var rejectedIds = new List<string>();
        var authorizedCommands = AetheriaRuntimeAuthorityRouter.AuthorizedCommands(
            observedCommands,
            policy,
            leases: null,
            localRuntimeId: daemonId,
            rejectedCommandIds: rejectedIds);

        using var physics = new AetheriaYmirWorldPhysics();
        var result = AetheriaRuntimeDaemonTickRunner.Tick(
            Path.Combine(Path.GetTempPath(), $"aetheria-authority-smoke-{daemonId}.cc"),
            run,
            new AetheriaRuntimeDaemonTickOptions
            {
                DaemonId = daemonId,
                SessionId = "authority-smoke",
                VerseId = policy.VerseId,
                FrameId = 1,
                SimulationTimeSeconds = 0.02,
                FixedDeltaSeconds = 0.02,
                WorldPhysics = physics,
                ObservedCommands = authorizedCommands,
                PreRejectedCommandIds = rejectedIds,
                BuildPublications = false
            });

        return result.Frame;
    }

    private static AetheriaRuntimeVerseAuthorityPolicyDocument Policy(
        string defaultMode,
        params AetheriaRuntimeAuthorityRule[] rules)
    {
        return new AetheriaRuntimeVerseAuthorityPolicyDocument
        {
            VerseId = "aetheria.test",
            HostRuntimeId = "host-daemon",
            DefaultMode = defaultMode,
            Rules = rules
        };
    }

    private static AetheriaRuntimeVerseAuthorityPolicyDocument CoopPolicy()
    {
        var ravenKey = EntityKey("coop-smoke", 0, 0);
        var hostileKey = EntityKey("coop-smoke", 0, 1);
        var policy = Policy(
            AetheriaRuntimeAuthorityModes.HostAuthoritative,
            new AetheriaRuntimeAuthorityRule
            {
                RuleId = "raven.runtime.owns.raven.movement",
                SubjectPrefix = ravenKey,
                ClaimKinds = [AetheriaRuntimeClaimKinds.Movement],
                Mode = AetheriaRuntimeAuthorityModes.DelegatedRuntime,
                RuntimeIds = ["pilot-client"],
                Priority = 100
            },
            new AetheriaRuntimeAuthorityRule
            {
                RuleId = "commander.runtime.owns.hostile.movement",
                SubjectPrefix = hostileKey,
                ClaimKinds = [AetheriaRuntimeClaimKinds.Movement],
                Mode = AetheriaRuntimeAuthorityModes.DelegatedRuntime,
                RuntimeIds = ["commander-client"],
                Priority = 100
            });
        policy.VerseId = "aetheria.coop-smoke";
        policy.PolicyId = "aetheria.coop-smoke.delegated.v1";
        return policy;
    }

    private static AetheriaRuntimeVerseAuthorityPolicyDocument CoopPolicyWithMetadata()
    {
        var policy = CoopPolicy();
        var ravenKey = EntityKey("coop-smoke", 0, 0);
        var hostileKey = EntityKey("coop-smoke", 0, 1);
        policy.Rules = policy.Rules
            .Concat(new[]
            {
                new AetheriaRuntimeAuthorityRule
                {
                    RuleId = "raven.runtime.owns.raven.metadata",
                    SubjectPrefix = ravenKey,
                    ClaimKinds = [AetheriaRuntimeClaimKinds.Metadata],
                    Mode = AetheriaRuntimeAuthorityModes.DelegatedRuntime,
                    RuntimeIds = ["pilot-client"],
                    Priority = 90
                },
                new AetheriaRuntimeAuthorityRule
                {
                    RuleId = "commander.runtime.owns.hostile.metadata",
                    SubjectPrefix = hostileKey,
                    ClaimKinds = [AetheriaRuntimeClaimKinds.Metadata],
                    Mode = AetheriaRuntimeAuthorityModes.DelegatedRuntime,
                    RuntimeIds = ["commander-client"],
                    Priority = 90
                }
            })
            .ToArray();
        return policy;
    }

    private static AetheriaRuntimeDaemonCommandDocument Command(
        string runtimeId,
        string subjectKey,
        AetheriaRuntimeDaemonCommandKinds kind = AetheriaRuntimeDaemonCommandKinds.SetMoveVector)
    {
        var command = AetheriaRuntimeDaemonCommandDocument.Create(
            kind,
            runtimeId,
            sessionId: "authority-smoke",
            observedFrameId: 0,
            actorEntityKey: subjectKey);
        command.SubjectKey = subjectKey;
        command.AuthorRuntimeId = runtimeId;
        return command;
    }

    private static AetheriaRuntimeDaemonCommandDocument MovementCommand(
        string runtimeId,
        string subjectKey,
        double directionX,
        double directionY,
        double magnitude)
    {
        var command = Command(runtimeId, subjectKey, AetheriaRuntimeDaemonCommandKinds.SetMoveVector);
        command.DirectionX = directionX;
        command.DirectionY = directionY;
        command.ScalarValue = magnitude;
        return command;
    }

    private static AetheriaRuntimeRunCheckpointCommit InitialCoopRun()
    {
        return new AetheriaRuntimeRunCheckpointCommit
        {
            RunId = "coop-smoke",
            CurrentZoneIndex = 0,
            CurrentEntityKey = EntityKey("coop-smoke", 0, 0),
            Zones =
            [
                new AetheriaRuntimeZoneSnapshotCommit
                {
                    ZoneIndex = 0,
                    Name = "Co-op Smoke Zone",
                    Entities =
                    [
                        new AetheriaRuntimeEntitySnapshotCommit
                        {
                            EntityIndex = 0,
                            Name = "Raven Pilot",
                            Kind = "ship",
                            FactionKey = "player",
                            IsActive = true
                        },
                        new AetheriaRuntimeEntitySnapshotCommit
                        {
                            EntityIndex = 1,
                            Name = "Commander Hostile",
                            Kind = "ship",
                            FactionKey = "player",
                            IsActive = true
                        }
                    ]
                }
            ]
        };
    }

    private static AetheriaRuntimeEntitySnapshotCommit Entity(
        AetheriaRuntimeRunCheckpointCommit run,
        string entityKey)
    {
        var parts = entityKey.Split('.');
        var zoneIndex = Array.IndexOf(parts, "zone") >= 0 && int.TryParse(parts[Array.IndexOf(parts, "zone") + 1], out var parsedZone)
            ? parsedZone
            : 0;
        var entityIndex = Array.IndexOf(parts, "entity") >= 0 && int.TryParse(parts[Array.IndexOf(parts, "entity") + 1], out var parsedEntity)
            ? parsedEntity
            : 0;
        var zone = (run.Zones ?? Array.Empty<AetheriaRuntimeZoneSnapshotCommit>())
            .First(candidate => candidate.ZoneIndex == zoneIndex);
        return (zone.Entities ?? Array.Empty<AetheriaRuntimeEntitySnapshotCommit>())
            .First(candidate => candidate.EntityIndex == entityIndex);
    }

    private static AetheriaRuntimeEntitySnapshotCommit SnapshotEntity(
        int entityIndex,
        string name,
        string factionKey,
        double x,
        double z,
        double visibility,
        double hull,
        string cargoItem)
    {
        return new AetheriaRuntimeEntitySnapshotCommit
        {
            EntityIndex = entityIndex,
            Name = name,
            Kind = "ship",
            FactionKey = factionKey,
            IsActive = true,
            PositionX = x,
            PositionZ = z,
            DirectionX = 1,
            DirectionY = 0,
            Visibility = visibility,
            TargetEntityIndex = -1,
            StatGrids =
            [
                new AetheriaRuntimeEntityStatGridCommit
                {
                    Name = "hull",
                    Width = 1,
                    Height = 1,
                    Values = [hull]
                },
                new AetheriaRuntimeEntityStatGridCommit
                {
                    Name = "shield",
                    Width = 1,
                    Height = 1,
                    Values = [25]
                },
                new AetheriaRuntimeEntityStatGridCommit
                {
                    Name = "heat",
                    Width = 1,
                    Height = 1,
                    Values = [4]
                }
            ],
            CargoContents =
            [
                new AetheriaRuntimeCargoBayLoadoutCommit
                {
                    Items =
                    [
                        new AetheriaRuntimeLoadoutItemSlotCommit
                        {
                            Item = new AetheriaRuntimeLoadoutItemCommit
                            {
                                ItemKey = cargoItem,
                                Quantity = 1,
                                Quality = 1,
                                Durability = 1,
                                Enabled = true
                            }
                        }
                    ]
                }
            ]
        };
    }

    private static string EntityKey(string runId, int zoneIndex, int entityIndex)
    {
        return $"global:aetheria.run_state.{runId}.zone.{zoneIndex}.entity.{entityIndex}.v1";
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    private static void RequireSurfaceMetric(
        EveSurfaceDocument surface,
        string componentId,
        string expectedValue,
        string message)
    {
        var component = Flatten(surface.Surface.Root)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, componentId, StringComparison.Ordinal));
        if (component == null)
            throw new InvalidOperationException($"{message}: missing component {componentId}");
        if (!component.Props.TryGetValue("value", out var actualValue) ||
            !string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message}: expected {expectedValue}, got {actualValue ?? "(missing)"}");
        }
    }

    private static IEnumerable<EveSurfaceComponent> Flatten(EveSurfaceComponent component)
    {
        yield return component;
        foreach (var child in component.Children ?? Array.Empty<EveSurfaceComponent>())
        {
            foreach (var nested in Flatten(child))
                yield return nested;
        }
    }
}
