using Aetheria.State;
using Aetheria.State.Documents;
using Aetheria.State.Daemon;
using GameCult.Aetheria.State.Verse;
using GameCult.Caching;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Mesh.Quic;
using GameCult.Networking;
using GameCult.Networking.WebSockets;
using MessagePack;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

var options = AetheriaDaemonHostOptions.Parse(args);
var startedAtUtc = DateTimeOffset.UtcNow.ToString("O");
var startup = Stopwatch.StartNew();
var startupPhase = Stopwatch.StartNew();
var traceStartupPhases = string.Equals(
    Environment.GetEnvironmentVariable("AETHERIA_TRACE_STARTUP_PHASES"),
    "1",
    StringComparison.Ordinal);
void TraceStartup(string name)
{
    if (traceStartupPhases)
        Console.WriteLine(
            $"Aetheria startup phase {name} took {startupPhase.Elapsed.TotalMilliseconds:0.###}ms " +
            $"(total {startup.Elapsed.TotalMilliseconds:0.###}ms).");
    startupPhase.Restart();
}
var traceClientTransport = string.Equals(
    Environment.GetEnvironmentVariable("AETHERIA_TRACE_CLIENT_TRANSPORT"),
    "1",
    StringComparison.Ordinal);

Console.WriteLine($"Aetheria Verse daemon starting: {options.StatePath}");
if (options.EnableOdinAnnouncements)
    Console.WriteLine($"Aetheria Odin announcement target: {options.OdinCultMeshUri}");

await using var node = await AetheriaStateNode.OpenAsync(
    options.StatePath,
    runtimeId: options.DaemonId,
    startServer: true,
    enableDurableShardLogs: false,
    hydrationProfile: AetheriaStateHydrationProfile.DaemonBoot).ConfigureAwait(false);
TraceStartup("open-state-node");
using var discoveryHost = new AetheriaVerseDiscoveryHost(node);
var latestFrame = await node.MutableDocument<AetheriaRuntimeDaemonFrameDocument>(AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest).ReadAsync().ConfigureAwait(false);
TraceStartup("latest-frame");
var unityBundles = BuildUnityBundleArtifactSet(options);
TraceStartup("provider-asset-bundles");
var persistedRuntimeCatalog = await node.Database
    .GetAsync<AetheriaRuntimeCatalogSnapshot>(AetheriaStateNode.RuntimeCatalogKey)
    .ConfigureAwait(false);
await node.CommitAsync(async () =>
{
    await EnsureWorldDocumentAsync(node).ConfigureAwait(false);
    TraceStartup("world-document");
    var tradePolicyChanged = await EnsureTradeValuePolicyAsync(node, startedAtUtc).ConfigureAwait(false);
    TraceStartup("trade-policy");
    var nativeCatalogChanged = await AetheriaDaemonNativeCatalog.EnsureAsync(node).ConfigureAwait(false);
    TraceStartup("native-catalog");
    if (persistedRuntimeCatalog == null ||
        string.IsNullOrWhiteSpace(persistedRuntimeCatalog.NameCorpusRecordKey) ||
        tradePolicyChanged ||
        nativeCatalogChanged)
        await node.RefreshRuntimeCatalogAsync().ConfigureAwait(false);
}).ConfigureAwait(false);
TraceStartup("initial-flush");
TraceStartup("runtime-catalog");
await AetheriaDaemonHangarCoordinator.EnsureAsync(node, node.RuntimeCatalog().Latest(), startedAtUtc).ConfigureAwait(false);
TraceStartup("hangar");
using (var progressionVerses = CreateProgressionVerseCoordinator(node, options))
    await progressionVerses.EnsureAndRefreshAsync(startedAtUtc).ConfigureAwait(false);
TraceStartup("progression-verses");
var verseHost = await EnsureVerseHostSettingsAsync(node, options, startedAtUtc).ConfigureAwait(false);
TraceStartup("verse-host");
await EnsureVerseAuthorityPolicyAsync(node, options).ConfigureAwait(false);
TraceStartup("authority-policy");
discoveryHost.Update(verseHost);
await PublishRuntimeSessionAsync(node, options, startedAtUtc, "starting").ConfigureAwait(false);
TraceStartup("runtime-session");
await PublishStateSurfacesAsync(node, options, startedAtUtc).ConfigureAwait(false);
TraceStartup("state-surfaces");
if (traceClientTransport)
{
    var hangarSnapshot = node.CreateRawSnapshotResponse(
        "startup-hangar-diagnostic",
        new CultNetSnapshotRequestMessage
        {
            MessageId = "startup-hangar-diagnostic",
            SchemaIds = [EveSurfaceDocument.SchemaId],
            RecordKeys = [AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString()]
        });
    Console.WriteLine(
        $"CultMesh startup Hangar cache={(!node.Cache.Contains(AetheriaRuntimeVerseRecordKeys.HangarSurface) ? "missing" : "present")} " +
        $"snapshotDocuments={hangarSnapshot.Documents.Length}");
}
var ingressState = new AetheriaDaemonIngressState();
await RefreshControlPlaneInputsAsync(node, ingressState).ConfigureAwait(false);
await using var cultMeshClientHost = await StartClientCultMeshHostAsync(
    node, options, unityBundles, () => latestFrame).ConfigureAwait(false);
TraceStartup("client-host");
Console.WriteLine($"Aetheria client CultMesh endpoint: {cultMeshClientHost.ControlEndpoint}");
if (!string.IsNullOrWhiteSpace(cultMeshClientHost.BrowserEndpoint))
    Console.WriteLine($"Aetheria client browser CultMesh endpoint: {cultMeshClientHost.BrowserEndpoint}");
Console.WriteLine($"Aetheria client CDN endpoint: {cultMeshClientHost.ContentEndpoint}");
Console.WriteLine($"Aetheria client realtime endpoint: {cultMeshClientHost.RealtimeEndpoint}");
Console.WriteLine($"Aetheria daemon transport-ready in {startup.Elapsed.TotalMilliseconds:0.###}ms.");
using var clientSubscriptions = new CultNetDatabaseSubscriptionServer(cultMeshClientHost.Protocol, node.Database);
var playableWorldDemand = new AetheriaPlayableWorldDemandState();
clientSubscriptions.DemandChanged += playableWorldDemand.Observe;
var managedViewportDemand = new AetheriaManagedViewportDemandState();
clientSubscriptions.DemandChanged += managedViewportDemand.Observe;
if (traceClientTransport)
    clientSubscriptions.DemandChanged += demand => Console.WriteLine(
        $"CultMesh state demand consumer={demand.ConsumerRuntimeId} subscription={demand.SubscriptionId} " +
        $"active={demand.Active} sameMachine={demand.SameMachine} " +
        $"records=[{string.Join(",", demand.RecordKeys)}] schemas=[{string.Join(",", demand.SchemaIds)}] " +
        $"bodies=[{string.Join(",", demand.BodyIds)}] transports=[{string.Join(",", demand.SupportedBodyTransports)}]");
using var bodyDemand = new CultMeshBodyDemandTracker(clientSubscriptions);
using var soaPublisher = new AetheriaRuntimeDaemonSoaFramePublisher(
    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    bodyDemand);
var stopped = new TaskCompletionSource<object?>();
var progressionForwardingTasks = new ConcurrentDictionary<string, Task>();
using var progressionForwardingShutdown = new CancellationTokenSource();
var hangarActivationRequested = false;
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    progressionForwardingShutdown.Cancel();
    stopped.TrySetResult(null);
};

while (!stopped.Task.IsCompleted)
{
    if (!options.Once && !options.UseTerminusFixture)
    {
        await PublishRuntimeSessionAsync(node, options, startedAtUtc, "ready").ConfigureAwait(false);
        Console.WriteLine("Aetheria daemon ready; waiting for a client to load or generate a world.");
        var nextProgressionRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!stopped.Task.IsCompleted)
        {
            ThrowIfClientHostFaulted(cultMeshClientHost);
            hangarActivationRequested |= await AcceptCoreEveInvocationsAsync(
                node,
                options,
                latestFrame,
                progressionForwardingTasks,
                progressionForwardingShutdown.Token).ConfigureAwait(false);
            await AcceptEveCommandsAsync(node, options).ConfigureAwait(false);
            if (options.OdinDiscoveryEndpoints.Count > 0 && DateTimeOffset.UtcNow >= nextProgressionRefreshUtc)
            {
                await PublishStateSurfacesAsync(node, options, DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);
                nextProgressionRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(5);
            }
            if (hangarActivationRequested && playableWorldDemand.IsActive &&
                (HasPlayableRun(latestFrame?.Run) ||
                 HasPlayableRun(await ReadRuntimeRunCheckpointAsync(node, options.RenderSettings).ConfigureAwait(false))))
                break;

            var completed = await Task.WhenAny(stopped.Task, Task.Delay(options.TickInterval)).ConfigureAwait(false);
            if (completed == stopped.Task)
                break;
        }

        if (stopped.Task.IsCompleted)
            break;
    }
    else
    {
        await EnsurePlayableRunDocumentsAsync(node, options, startedAtUtc, latestFrame).ConfigureAwait(false);
        TraceStartup("playable-run");
    }

    latestFrame = await node.MutableDocument<AetheriaRuntimeDaemonFrameDocument>(AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest)
        .ReadAsync().ConfigureAwait(false);
    var activatedGameSession = await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
        .ReadAsync().ConfigureAwait(false);
    if (!FrameBelongsToSession(latestFrame, activatedGameSession))
        latestFrame = null;
    using var worldPhysics = new AetheriaYmirWorldPhysics();
    await EnsureGameSessionAsync(node, options, startedAtUtc, latestFrame).ConfigureAwait(false);
    TraceStartup("game-session");
    using var physicsPersistence = await AetheriaYmirPersistenceCoordinator.OpenAsync(
        node,
        worldPhysics,
        latestFrame).ConfigureAwait(false);
    TraceStartup("ymir-persistence");
    var nextApiPublicationUtc = DateTimeOffset.UtcNow;
    var hotState = new AetheriaHotEntityPublicationState();
    var reactiveSurfaceState = new AetheriaReactiveSurfacePublicationState();
    var publishRestoredFrame = !options.Once &&
        latestFrame?.Run != null &&
        HasPlayableRun(latestFrame.Run) &&
        HasPersistedClientBootstrap(node, latestFrame);
    AetheriaRuntimeDaemonTickResult? initialPublication = null;
    AetheriaPreparedPublication? initialPrepared = null;
    Task publicationTask;
    if (publishRestoredFrame)
    {
        await RefreshControlPlaneInputsAsync(node, ingressState).ConfigureAwait(false);
        TraceStartup("restored-client-state");
    }
    else
    {
        var firstTick = await TickAsync(
            node, options, unityBundles, worldPhysics, latestFrame, ingressState,
            progressionForwardingTasks, progressionForwardingShutdown.Token,
            refreshControlPlane: false).ConfigureAwait(false);
        TraceStartup("first-tick");
        latestFrame = firstTick.Frame;
        initialPrepared = PreparePublication(
            node.StatePath,
            options,
            firstTick,
            ingressState,
            physicsPersistence);
        initialPublication = initialPrepared.Publication;
    }
    if (!publishRestoredFrame)
    {
        await node.CommitAsync(() => PublishClientGameplayDocumentsAsync(
                node,
                options,
                unityBundles,
                initialPublication!,
                ingressState.Catalog ?? throw new InvalidOperationException("Aetheria runtime catalog was not initialized."),
                reactiveSurfaceState,
                publishTopology: true))
            .ConfigureAwait(false);
    }
    TraceStartup("client-bootstrap");
    ThrowIfClientHostFaulted(cultMeshClientHost);
    await PublishHotEntityStateAsync(
        node, soaPublisher, hotState, latestFrame!, ingressState.Catalog, cultMeshClientHost).ConfigureAwait(false);
    TraceStartup("hot-entity-state");
    if (publishRestoredFrame)
    {
        publicationTask = Task.CompletedTask;
    }
    else
    {
        publicationTask = Task.Run(() => PersistPreparedDocumentsAsync(
            node,
            options,
            physicsPersistence,
            initialPrepared!,
            initialTopology: true));
    }
    nextApiPublicationUtc = DateTimeOffset.UtcNow.Add(options.ApiPublicationInterval);
    Console.WriteLine($"Aetheria Verse daemon published frame {latestFrame!.FrameId}.");
    Console.WriteLine($"Aetheria daemon playable-world-ready in {startup.Elapsed.TotalMilliseconds:0.###}ms.");

    if (options.Once)
    {
        await publicationTask.ConfigureAwait(false);
        await PublishRuntimeSessionAsync(node, options, startedAtUtc, "completed").ConfigureAwait(false);
        return;
    }
    await PublishRuntimeSessionAsync(node, options, startedAtUtc, "running").ConfigureAwait(false);
    Console.WriteLine("Aetheria Verse daemon is running. Press Ctrl+C to stop.");

    var nextTickUtc = DateTimeOffset.UtcNow.Add(options.TickInterval);
    while (!stopped.Task.IsCompleted && (options.UseTerminusFixture || playableWorldDemand.IsActive))
    {
        ThrowIfClientHostFaulted(cultMeshClientHost);
        var delay = nextTickUtc - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            var completed = await Task.WhenAny(stopped.Task, Task.Delay(delay)).ConfigureAwait(false);
            if (completed == stopped.Task)
                break;
        }

        var buildPublications = DateTimeOffset.UtcNow >= nextApiPublicationUtc;
        var tick = await TickAsync(
            node, options, unityBundles, worldPhysics, latestFrame, ingressState,
            progressionForwardingTasks, progressionForwardingShutdown.Token,
            refreshControlPlane: buildPublications).ConfigureAwait(false);
        ThrowIfClientHostFaulted(cultMeshClientHost);
        latestFrame = tick.Frame;
        await PublishHotEntityStateAsync(
            node, soaPublisher, hotState, tick.Frame, ingressState.Catalog, cultMeshClientHost).ConfigureAwait(false);
        nextTickUtc += options.TickInterval;
        if (nextTickUtc < DateTimeOffset.UtcNow - options.TickInterval)
            nextTickUtc = DateTimeOffset.UtcNow;
        if (buildPublications && publicationTask.IsCompleted)
        {
            if (publicationTask.IsFaulted)
                await publicationTask.ConfigureAwait(false);
            var publication = PreparePublication(
                node.StatePath,
                options,
                tick,
                ingressState,
                physicsPersistence);
            publicationTask = Task.Run(() => PersistPreparedDocumentsAsync(
                node,
                options,
                physicsPersistence,
                publication,
                initialTopology: false,
                unityBundles: unityBundles,
                reactiveSurfaceState: reactiveSurfaceState));
            await PublishDemandedManagedViewportsAsync(node, options, tick.Frame, managedViewportDemand).ConfigureAwait(false);
            nextApiPublicationUtc = DateTimeOffset.UtcNow.Add(options.ApiPublicationInterval);
        }
        if (tick.Frame.FrameId % (traceClientTransport ? 10 : 120) == 0)
        {
            Console.WriteLine(
                $"Aetheria Verse daemon published frame {tick.Frame.FrameId} at {tick.Frame.SimulationTimeSeconds:0.00}s; " +
                $"control peers={cultMeshClientHost.ControlPeerCount} " +
                $"quic peers={cultMeshClientHost.Realtime.ConnectionCount} " +
                $"cdn chunks={cultMeshClientHost.Content.ChunkRequestsServed}.");
        }
    }

    await publicationTask.ConfigureAwait(false);
    if (!stopped.Task.IsCompleted && !options.UseTerminusFixture)
    {
        var checkpointRun = latestFrame?.Run ?? throw new InvalidOperationException("Aetheria cannot checkpoint an inactive playable session without its run.");
        var checkpointTick = new AetheriaRuntimeDaemonTickResult(
            checkpointRun,
            new AetheriaRuntimeDaemonOperationResult(checkpointRun, [], []),
            latestFrame!);
        var checkpoint = PreparePublication(
            node.StatePath,
            options,
            checkpointTick,
            ingressState,
            physicsPersistence);
        await PersistPreparedDocumentsAsync(
            node,
            options,
            physicsPersistence,
            checkpoint,
            initialTopology: false,
            unityBundles: unityBundles,
            reactiveSurfaceState: reactiveSurfaceState).ConfigureAwait(false);
        Console.WriteLine($"Aetheria daemon saved frame {latestFrame!.FrameId} after the playable client disconnected.");
    }
}

progressionForwardingShutdown.Cancel();
try
{
    await Task.WhenAll(progressionForwardingTasks.Values.ToArray()).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
}
await PublishRuntimeSessionAsync(node, options, startedAtUtc, "stopped").ConfigureAwait(false);
Console.WriteLine("Aetheria Verse daemon stopping.");

static void ThrowIfClientHostFaulted(AetheriaClientCultMeshHost host)
{
    if (host.Control.BackgroundFailure.IsCompleted)
        throw new InvalidOperationException(
            "Aetheria client CultMesh TCP control host faulted.",
            host.Control.BackgroundFailure.GetAwaiter().GetResult());
    if (host.Realtime.BackgroundFailure.IsCompleted)
        throw new InvalidOperationException(
            "Aetheria client CultMesh QUIC realtime host faulted.",
            host.Realtime.BackgroundFailure.GetAwaiter().GetResult());
}

static async Task<AetheriaRuntimeDaemonTickResult> TickAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    AetheriaUnityBundleArtifactSet unityBundles,
    IAetheriaRuntimeWorldPhysics worldPhysics,
    AetheriaRuntimeDaemonFrameDocument? currentFrame,
    AetheriaDaemonIngressState ingressState,
    ConcurrentDictionary<string, Task> progressionForwardingTasks,
    CancellationToken progressionForwardingCancellation,
    bool refreshControlPlane)
{
    var traceTickPhases = string.Equals(
        Environment.GetEnvironmentVariable("AETHERIA_TRACE_TICK_PHASES"),
        "1",
        StringComparison.Ordinal);
    var phase = Stopwatch.StartNew();
    void TracePhase(string name)
    {
        if (traceTickPhases && phase.ElapsedMilliseconds >= 20)
            Console.WriteLine($"Aetheria tick phase {name} took {phase.ElapsedMilliseconds}ms.");
        phase.Restart();
    }

    await AcceptCoreEveInvocationsAsync(
        node,
        options,
        currentFrame,
        progressionForwardingTasks,
        progressionForwardingCancellation).ConfigureAwait(false);
    TracePhase("core-ingress");
    await AcceptEveCommandsAsync(node, options).ConfigureAwait(false);
    TracePhase("provider-ingress");
    if (!ingressState.ControlPlaneInitialized || refreshControlPlane)
        await RefreshControlPlaneInputsAsync(node, ingressState).ConfigureAwait(false);
    else
        await RefreshGameSessionInputsAsync(node, ingressState).ConfigureAwait(false);

    var fixedDeltaSeconds = currentFrame?.FixedDeltaSeconds > 0
        ? currentFrame.FixedDeltaSeconds
        : options.FixedDeltaSeconds;
    var nextFrameId = (currentFrame?.FrameId ?? -1) + 1;
    var sessionId = string.IsNullOrWhiteSpace(ingressState.SessionId)
        ? options.SessionId
        : ingressState.SessionId;
    var run = HasPlayableRun(currentFrame?.Run) &&
              string.Equals(currentFrame!.RunRecordKey, ingressState.RunRecordKey, StringComparison.Ordinal) &&
              string.Equals(currentFrame.Run.RunId, ingressState.RunId, StringComparison.Ordinal) &&
              string.Equals(currentFrame.GameMode, ingressState.GameMode, StringComparison.Ordinal)
        ? currentFrame.Run
        : await ReadRuntimeRunCheckpointAsync(node, options.RenderSettings, ingressState.RunRecordKey).ConfigureAwait(false)
          ?? throw new InvalidDataException(
              $"Active game session '{ingressState.RunId}' has no canonical run at '{ingressState.RunRecordKey}'.");
    if (!string.Equals(run.RunId, ingressState.RunId, StringComparison.Ordinal))
        throw new InvalidDataException(
            $"Active game session run '{ingressState.RunId}' does not match canonical run '{run.RunId}'.");
    ApplyDaemonRenderSettings(run, options.RenderSettings);

    var loadoutTemplates = ingressState.LoadoutTemplates;
    var observedCommands = node.Documents<AetheriaRuntimeDaemonCommandDocument>()
        .OrderBy(command => command.IssuedAtUtc ?? "", StringComparer.Ordinal)
        .ThenBy(command => command.CommandId ?? "", StringComparer.Ordinal)
        .ToArray();
    var pendingObservedCommands = observedCommands
        .Where(command => command != null &&
            !string.IsNullOrWhiteSpace(command.CommandId) &&
            node.Cache.Get<EveCommandReceiptDocument>(
                AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(command.CommandId)) == null)
        .ToArray();
    var policyRejectedCommandIds = new List<string>();
    var authorityPolicy = ingressState.AuthorityPolicy;
    var starbridgeScenario = ingressState.StarbridgeScenario;
    var starbridgeSession = ingressState.StarbridgeSession;
    var authorityLeases = ingressState.AuthorityLeases;
    var authorizedCommands = AetheriaRuntimeAuthorityRouter.AuthorizedCommands(
        pendingObservedCommands,
        authorityPolicy,
        authorityLeases,
        options.DaemonId,
        policyRejectedCommandIds);
    var terminus = string.Equals(ingressState.GameMode, AetheriaGameSessionState.TerminusMode, StringComparison.Ordinal);
    var simulationClockCommands = authorizedCommands
        .Where(command => command.Kind == AetheriaRuntimeDaemonCommandKinds.SetSimulationRate ||
            command.Kind == AetheriaRuntimeDaemonCommandKinds.AdvanceSimulationStep)
        .ToArray();
    if (!terminus && simulationClockCommands.Length > 0)
    {
        policyRejectedCommandIds.AddRange(simulationClockCommands.Select(command => command.CommandId));
        authorizedCommands = authorizedCommands
            .Where(command => command.Kind != AetheriaRuntimeDaemonCommandKinds.SetSimulationRate &&
                command.Kind != AetheriaRuntimeDaemonCommandKinds.AdvanceSimulationStep)
            .ToArray();
    }
    else if (simulationClockCommands.Length > 0)
    {
        await ApplySimulationClockCommandsAsync(node, ingressState, simulationClockCommands).ConfigureAwait(false);
    }
    var simulationStepCount = terminus ? ingressState.TakeTerminusSimulationSteps() : 1;
    var advanceSimulation = simulationStepCount > 0 && AetheriaRuntimeRunLifecycle.IsActive(run);
    if (terminus && !advanceSimulation && AetheriaRuntimeRunLifecycle.IsActive(run))
    {
        authorizedCommands = authorizedCommands
            .Where(command => !AetheriaRuntimeDaemonOperations.RequiresSimulationStep(command.Kind))
            .ToArray();
    }
    var simulationTimeSeconds = (currentFrame?.SimulationTimeSeconds ?? 0) +
        ((advanceSimulation ? simulationStepCount : 0) * fixedDeltaSeconds);
    TracePhase("tick-inputs");

    var result = AetheriaRuntimeDaemonTickRunner.Tick(
        node.StatePath,
        run,
        new AetheriaRuntimeDaemonTickOptions
        {
            DaemonId = options.DaemonId,
            SessionId = sessionId,
            VerseId = options.VerseId,
            CultMeshAddress = options.CultMeshAddress,
            FrameId = nextFrameId,
            SimulationTimeSeconds = simulationTimeSeconds,
            FixedDeltaSeconds = fixedDeltaSeconds,
            ObservedCommands = authorizedCommands,
            PreRejectedCommandIds = policyRejectedCommandIds,
            Catalog = ingressState.Catalog,
            RenderSettings = options.RenderSettings,
            SimulationSettings = options.SimulationSettings,
            WorldPhysics = worldPhysics,
            AdvanceSimulation = advanceSimulation,
            SimulationStepCount = simulationStepCount,
            StopCompressedSimulationOnAttention = terminus && simulationStepCount > 1,
            StarbridgeScenario = starbridgeScenario,
            StarbridgeSession = starbridgeSession,
            BuildPublications = false,
            OperationContext = new AetheriaRuntimeDaemonOperationContext
            {
                LoadoutTemplates = loadoutTemplates
            }
        });
    if (terminus && result.AttentionInterruption != null)
        await PauseTerminusForAttentionAsync(node, ingressState).ConfigureAwait(false);
    result.Frame.GameMode = ingressState.GameMode;
    result.Frame.RunRecordKey = ingressState.RunRecordKey;
    result.Frame.RequestedSimulationRate = ingressState.RequestedSimulationRate;
    result.Frame.EffectiveSimulationRate = ingressState.SimulationRate;
    result.Frame.SimulationStepsExecuted = result.SimulationStepsExecuted;
    result.Frame.AttentionCauseKind = result.AttentionInterruption?.CauseKind ?? "";
    TracePhase("simulation");
    if (string.Equals(Environment.GetEnvironmentVariable("AETHERIA_TRACE_EVE_SNAPSHOTS"), "1", StringComparison.Ordinal) &&
        result.Frame.AppliedCommandIds.Count > 0)
    {
        foreach (var entity in (result.Frame.Run?.Zones ?? Array.Empty<AetheriaRuntimeZoneSnapshotCommit>())
                     .SelectMany(zone => zone.Entities ?? Array.Empty<AetheriaRuntimeEntitySnapshotCommit>())
                     .Where(entity => string.Equals(entity.FactionKey, "player", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(
                $"Eve command result entity={entity.EntityIndex} position={entity.PositionX},{entity.PositionZ} " +
                $"velocity={entity.VelocityX},{entity.VelocityY}");
        }
    }
    var finalizesCommands = (result.Frame.AppliedCommandIds?.Count ?? 0) > 0 ||
        (result.Frame.RejectedCommandIds?.Count ?? 0) > 0 ||
        policyRejectedCommandIds.Count > 0;
    if (finalizesCommands)
    {
        await node.CommitAsync(async () =>
        {
            await PublishDurableDaemonDocumentsAsync(node, result).ConfigureAwait(false);
            await PublishCommittedCommandFactsAsync(
                node,
                options,
                result.Frame,
                pendingObservedCommands,
                authorizedCommands,
                policyRejectedCommandIds).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
    TracePhase("command-facts");

    return result;
}

static AetheriaPreparedPublication PreparePublication(
    string statePath,
    AetheriaDaemonHostOptions options,
    AetheriaRuntimeDaemonTickResult tick,
    AetheriaDaemonIngressState ingressState,
    AetheriaYmirPersistenceCoordinator physicsPersistence)
{
    var physics = physicsPersistence.Capture(tick.Frame);
    var frameBytes = MessagePackSerializer.Serialize(tick.Frame);
    var frame = MessagePackSerializer.Deserialize<AetheriaRuntimeDaemonFrameDocument>(frameBytes);
    var operation = new AetheriaRuntimeDaemonOperationResult(
        frame.Run ?? new AetheriaRuntimeRunCheckpointCommit(),
        tick.OperationResult.AppliedCommandIds.ToArray(),
        tick.OperationResult.RejectedCommandIds.ToArray(),
        rejectedCommandReasons: tick.OperationResult.RejectedCommandReasons);
    var publication = AetheriaRuntimeDaemonTickRunner.BuildPublications(
        statePath,
        operation,
        frame,
        new AetheriaRuntimeDaemonTickOptions
        {
            DaemonId = options.DaemonId,
            VerseId = options.VerseId,
            CultMeshAddress = options.CultMeshAddress,
            Catalog = ingressState.Catalog,
            StarbridgeScenario = ingressState.StarbridgeScenario,
            StarbridgeSession = ingressState.StarbridgeSession,
            RenderSettings = options.RenderSettings,
            SimulationSettings = options.SimulationSettings
        },
        frame.AccountedCommandIds?.Count ?? 0);
    return new AetheriaPreparedPublication(
        publication,
        physics,
        ingressState.Catalog ?? throw new InvalidOperationException("Aetheria runtime catalog was not initialized."));
}

static bool HasPersistedClientBootstrap(
    AetheriaStateNode node,
    AetheriaRuntimeDaemonFrameDocument frame)
{
    var input = node.Cache.Get<EveInputCapabilityDocument>(AetheriaRuntimeVerseRecordKeys.PilotInputCapability);
    var game = node.Cache.Get<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.DaemonGameSurface);
    var reactive = node.Cache.Get<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.DaemonGameReactiveSurface);
    var map = node.Cache.Get<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.MapMenuSurface);
    return node.Cache.Get<EveProviderAdvertisementDocument>(AetheriaRuntimeVerseRecordKeys.EveProviderAdvertisement) != null &&
           node.Cache.Get<EveAssetCatalogDocument>(AetheriaRuntimeVerseRecordKeys.EveAssetCatalog) != null &&
           input?.Version == frame.FrameId &&
           game?.Version == frame.FrameId &&
           HasCurrentEntityBodyContract(game.Surface.Root, requirePointer: true) &&
           reactive?.Version == frame.FrameId &&
           HasCurrentEntityBodyContract(reactive.Surface.Root, requirePointer: false) &&
           map?.Version == frame.FrameId;
}

static bool HasCurrentEntityBodyContract(EveSurfaceComponent component, bool requirePointer)
{
    var hasPointer = !string.IsNullOrWhiteSpace(component.GetProp("entityViewPointerId"));
    if (hasPointer && !string.Equals(
            component.GetProp("entityBodyId"),
            AetheriaRuntimeDaemonSoaFramePublisher.BodyId,
            StringComparison.Ordinal))
        return false;
    var children = component.Children ?? Array.Empty<EveSurfaceComponent>();
    var childHasPointer = false;
    foreach (var child in children)
    {
        if (!HasCurrentEntityBodyContract(child, requirePointer: false))
            return false;
        childHasPointer |= ContainsEntityViewPointer(child);
    }
    return !requirePointer || hasPointer || childHasPointer;
}

static bool ContainsEntityViewPointer(EveSurfaceComponent component) =>
    !string.IsNullOrWhiteSpace(component.GetProp("entityViewPointerId")) ||
    (component.Children ?? Array.Empty<EveSurfaceComponent>()).Any(ContainsEntityViewPointer);

static async Task PersistPreparedDocumentsAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    AetheriaYmirPersistenceCoordinator physicsPersistence,
    AetheriaPreparedPublication prepared,
    bool initialTopology,
    AetheriaUnityBundleArtifactSet? unityBundles = null,
    AetheriaReactiveSurfacePublicationState? reactiveSurfaceState = null)
{
    await physicsPersistence.PersistPrivateAsync(prepared.Physics).ConfigureAwait(false);
    await node.CommitAsync(async () =>
    {
        await PublishDurableDaemonDocumentsAsync(node, prepared.Publication).ConfigureAwait(false);
        if (initialTopology)
        {
            await PublishSecondaryTopologyDocumentsAsync(node, options, prepared.Publication).ConfigureAwait(false);
            await PublishStateSurfacesAsync(node, options, prepared.Publication.Frame.PublishedAtUtc, publishHangar: false).ConfigureAwait(false);
            await PublishOdinSurfaceAnnouncementsAsync(node, options, prepared.Publication.Frame.PublishedAtUtc).ConfigureAwait(false);
        }
        else
        {
            if (unityBundles == null)
                throw new InvalidOperationException("Periodic publication requires the provider asset set.");
            await PublishClientGameplayDocumentsAsync(
                node,
                options,
                unityBundles,
                prepared.Publication,
                prepared.Catalog,
                reactiveSurfaceState,
                publishTopology: false).ConfigureAwait(false);
        }
    }, soft: false).ConfigureAwait(false);
    await physicsPersistence.ActivateAsync().ConfigureAwait(false);
}

static async Task RefreshControlPlaneInputsAsync(
    AetheriaStateNode node,
    AetheriaDaemonIngressState ingressState)
{
    await RefreshGameSessionInputsAsync(node, ingressState).ConfigureAwait(false);
    ingressState.LoadoutTemplates = node.Cache
        .GetAll<AetheriaLoadoutTemplate>()
        .Select(ToLoadoutTemplateCommit)
        .ToArray();
    ingressState.Catalog = node.RuntimeCatalog().Latest();
    ingressState.AuthorityPolicy = await node
        .MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy)
        .ReadAsync().ConfigureAwait(false);
    ingressState.StarbridgeScenario = await node
        .MutableDocument<AetheriaRuntimeStarbridgeScenarioDocument>(AetheriaRuntimeVerseRecordKeys.StarbridgeScenarioLatest)
        .ReadAsync().ConfigureAwait(false);
    ingressState.StarbridgeSession = await node
        .MutableDocument<AetheriaRuntimeStarbridgeSessionDocument>(AetheriaRuntimeVerseRecordKeys.StarbridgeSessionLatest)
        .ReadAsync().ConfigureAwait(false);
    ingressState.AuthorityLeases = node.Documents<AetheriaRuntimeAuthorityLeaseDocument>().ToArray();
    ingressState.ControlPlaneInitialized = true;
}

static async Task RefreshGameSessionInputsAsync(
    AetheriaStateNode node,
    AetheriaDaemonIngressState ingressState)
{
    var gameSession = await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
        .ReadAsync().ConfigureAwait(false);
    ingressState.GameMode = gameSession?.Mode ?? "";
    ingressState.SessionId = gameSession?.SessionId ?? "";
    ingressState.RunId = gameSession?.RunId ?? "";
    ingressState.RunRecordKey = gameSession?.RunRecordKey ?? "";
    ingressState.RequestedSimulationRate = gameSession?.SimulationRate ?? 0;
    ingressState.SimulationRate = gameSession?.EffectiveSimulationRate ?? gameSession?.SimulationRate ?? 0;
}

static async Task ApplySimulationClockCommandsAsync(
    AetheriaStateNode node,
    AetheriaDaemonIngressState ingressState,
    IReadOnlyList<AetheriaRuntimeDaemonCommandDocument> commands)
{
    var session = await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
        .ReadAsync().ConfigureAwait(false) ?? new AetheriaGameSessionState();
    foreach (var command in commands.OrderBy(command => command.IssuedAtUtc, StringComparer.Ordinal))
    {
        if (command.Kind == AetheriaRuntimeDaemonCommandKinds.AdvanceSimulationStep)
        {
            ingressState.SimulationStepAccumulator += 1;
            continue;
        }
        if (AetheriaRuntimeDaemonOperations.IsSupportedSimulationRate(command.ScalarValue))
        {
            session.SimulationRate = command.ScalarValue;
            session.EffectiveSimulationRate = command.ScalarValue;
        }
    }
    session.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
    await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
        .ReplaceAsync(session).ConfigureAwait(false);
    ingressState.RequestedSimulationRate = session.SimulationRate;
    ingressState.SimulationRate = session.EffectiveSimulationRate ?? session.SimulationRate;
}

static async Task PauseTerminusForAttentionAsync(
    AetheriaStateNode node,
    AetheriaDaemonIngressState ingressState)
{
    var session = await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
        .ReadAsync().ConfigureAwait(false) ?? new AetheriaGameSessionState();
    session.EffectiveSimulationRate = 0;
    session.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
    await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
        .ReplaceAsync(session).ConfigureAwait(false);
    ingressState.SimulationRate = 0;
    ingressState.SimulationStepAccumulator = 0;
}

static async Task PublishCommittedCommandFactsAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    AetheriaRuntimeDaemonFrameDocument frame,
    IReadOnlyList<AetheriaRuntimeDaemonCommandDocument> pendingObservedCommands,
    IReadOnlyList<AetheriaRuntimeDaemonCommandDocument> authorizedCommands,
    IReadOnlyList<string> policyRejectedCommandIds)
{
    var byCommandId = (pendingObservedCommands ?? Array.Empty<AetheriaRuntimeDaemonCommandDocument>())
        .Where(command => command != null && !string.IsNullOrWhiteSpace(command.CommandId))
        .ToDictionary(command => command.CommandId, StringComparer.Ordinal);

    foreach (var commandId in frame.AppliedCommandIds ?? Array.Empty<string>())
    {
        if (string.IsNullOrWhiteSpace(commandId) || !byCommandId.TryGetValue(commandId, out var command))
            continue;

        await PublishCommittedFactAsync(
            node,
            AetheriaRuntimeCommittedCommandFactDocument.FromAppliedCommand(
                frame,
                command,
                options.VerseId)).ConfigureAwait(false);
        await node.DeleteDaemonCommandAsync(commandId).ConfigureAwait(false);
    }

    foreach (var commandId in frame.RejectedCommandIds ?? Array.Empty<string>())
    {
        if (string.IsNullOrWhiteSpace(commandId) ||
            !byCommandId.TryGetValue(commandId, out var command))
            continue;

        await PublishCommittedFactAsync(
            node,
            AetheriaRuntimeCommittedCommandFactDocument.FromRejectedCommand(
                frame,
                command,
                options.VerseId)).ConfigureAwait(false);
        await node.DeleteDaemonCommandAsync(commandId).ConfigureAwait(false);
    }
}

static async Task PublishCommittedFactAsync(
    AetheriaStateNode node,
    AetheriaRuntimeCommittedCommandFactDocument fact)
{
    await node.CommitAsync(async () =>
    {
        await node.PutCommittedCommandFactAsync(fact).ConfigureAwait(false);
        var receipt = AetheriaRuntimeDaemonReceiptProjector.Project(
            fact,
            AetheriaRuntimeDaemonGameSurfaceBuilder.SurfaceId);
        await node.Database.PutAsync(AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(receipt.CommandId), receipt)
            .ConfigureAwait(false);
        if (string.Equals(Environment.GetEnvironmentVariable("AETHERIA_TRACE_EVE_SNAPSHOTS"), "1", StringComparison.Ordinal))
            Console.WriteLine($"Eve command receipt command={receipt.CommandId} state={receipt.State} provider={receipt.ProviderId}");
    }).ConfigureAwait(false);
}

static async Task<AetheriaClientCultMeshHost> StartClientCultMeshHostAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    AetheriaUnityBundleArtifactSet unityBundles,
    Func<AetheriaRuntimeDaemonFrameDocument?> latestFrame)
{
    var traceStartup = string.Equals(
        Environment.GetEnvironmentVariable("AETHERIA_TRACE_STARTUP_PHASES"),
        "1",
        StringComparison.Ordinal);
    var startupPhase = Stopwatch.StartNew();
    void Trace(string phase)
    {
        if (traceStartup)
            Console.WriteLine($"Aetheria client-host phase {phase} took {startupPhase.Elapsed.TotalMilliseconds:0.###}ms.");
        startupPhase.Restart();
    }

    var bundleCdnManifests = BuildBundleCdnManifestIndex(unityBundles);
    Trace("cdn-documents");
    var clientBindAddress = ParseBindAddress(options.ClientCultMeshHost);
    var remotePublication = !IPAddress.IsLoopback(clientBindAddress) ||
        !IsLoopbackHost(options.ClientCultMeshAdvertiseHost);
    if (remotePublication)
    {
        throw new InvalidOperationException(
            "Production remote Aetheria publication is disabled until an authenticated player principal " +
            "is bound to CultMesh session identity and per-principal Hangar record keys.");
    }
    if (remotePublication &&
        (options.ClientCultMeshWebSocketPort <= 0 ||
         options.ClientCultMeshQuicPort <= 0 ||
         string.IsNullOrWhiteSpace(options.ProviderSigningKeyPath) ||
         string.IsNullOrWhiteSpace(options.ProviderKeyId) ||
         string.IsNullOrWhiteSpace(options.AuthorityRouteGrantPath) ||
         !options.ClientCultMeshCertificateWasExplicit))
    {
        throw new InvalidOperationException(
            "Remote CultMesh publication requires explicit WebSocket and QUIC ports, a TLS certificate, " +
            "provider P-256 key/id, and an Odin-issued authority route grant.");
    }
    var realtimeCertificate = LoadOrCreateRealtimeCertificate(
        options.ClientCultMeshAdvertiseHost,
        options.ClientCultMeshCertificatePath,
        options.ClientCultMeshCertificatePassword,
        requireExisting: remotePublication);
    const string localRouteGeneration = "aetheria-client-local-v2";
    var advertisedEndpoint = "";
    var advertisedBrowserEndpoint = remotePublication
        ? $"wss://{options.ClientCultMeshAdvertiseHost}:{options.ClientCultMeshWebSocketPort}/cultmesh"
        : "";
    var advertisedContentEndpoint = remotePublication
        ? $"https://{options.ClientCultMeshAdvertiseHost}:{options.ClientCultMeshWebSocketPort}/content"
        : "";
    var certificatePin = Convert.ToHexString(SHA256.HashData(realtimeCertificate.RawData));
    var advertisedRealtimeEndpoint = remotePublication
        ? $"{CultMeshQuicRealtimeTransportConnector.Scheme}://{options.ClientCultMeshAdvertiseHost}:{options.ClientCultMeshQuicPort}" +
          $"?cert-sha256={certificatePin}"
        : "";
    ECDsa? providerSigningKey = null;
    CultMeshAuthorityRoute[]? certifiedRoutes = null;
    try
    {
        if (remotePublication)
        {
            providerSigningKey = LoadP256PrivateKey(options.ProviderSigningKeyPath);
            var providerPublicKey = CultMeshEcdsaP256PublicKey.From(options.ProviderKeyId, providerSigningKey);
            certifiedRoutes = LoadAuthorityRouteGrant(
                options.AuthorityRouteGrantPath,
                options.VerseId,
                options.DaemonId,
                providerPublicKey,
                options.ProgressionTrust,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CultMeshProtocols.Documents.Value] = advertisedBrowserEndpoint,
                    [CultMeshProtocols.Content.Value] = advertisedContentEndpoint,
                    [CultMeshProtocols.RealtimeState.Value] = advertisedRealtimeEndpoint
                });
        }
    }
    catch
    {
        providerSigningKey?.Dispose();
        realtimeCertificate.Dispose();
        throw;
    }
    var localTransportBindAddress = remotePublication ? IPAddress.Loopback : clientBindAddress;
    var tcpServer = new TcpFramedCultNetSchemaServer(new TcpListener(
        localTransportBindAddress,
        options.ClientCultMeshPort));
    CultNetWebSocketSchemaServer? browserServer = null;
    WebApplication? browserApp = null;
    CultNetSchemaServerGroup? serverGroup = null;
    ICultNetSchemaServer server = tcpServer;
    if (options.ClientCultMeshWebSocketPort >= 0)
    {
        browserServer = new CultNetWebSocketSchemaServer();
        var browserBuilder = WebApplication.CreateSlimBuilder();
        browserBuilder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(
                clientBindAddress,
                options.ClientCultMeshWebSocketPort,
                listen =>
                {
                    if (remotePublication)
                        listen.UseHttps(realtimeCertificate);
                }));
        browserApp = browserBuilder.Build();
        browserApp.UseWebSockets();
        browserApp.MapCultNetWebSocket(
            "/cultmesh",
            browserServer,
            remotePublication
                ? new CultNetWebSocketEndpointOptions { AuthorizeAsync = _ => ValueTask.FromResult(true) }
                : new CultNetWebSocketEndpointOptions { AllowAnonymousDevelopment = true });
        browserApp.MapGet("/content", async context =>
        {
            var hash = context.Request.Query["chunkHash"].ToString();
            var chunk = string.IsNullOrWhiteSpace(hash) ? null : unityBundles.ResolveChunk(hash);
            if (chunk == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = chunk.Payload.LongLength;
            await context.Response.Body.WriteAsync(chunk.Payload, context.RequestAborted).ConfigureAwait(false);
        });
        serverGroup = new CultNetSchemaServerGroup(tcpServer, browserServer);
        server = serverGroup;
    }
    var content = new CultMeshTcpContentServer(
        new TcpListener(localTransportBindAddress, options.ClientCultMeshContentPort),
        unityBundles.ResolveChunk,
        new CultMeshTcpContentServerOptions
        {
            VerseId = options.VerseId,
            AuthorityRuntimeId = options.DaemonId,
            RouteGeneration = localRouteGeneration
        });
    Trace("tcp-listeners");
    Trace("realtime-certificate");
    CultMeshQuicRealtimeServer realtime;
    try
    {
        realtime = await CultMeshQuicRealtimeServer.ListenAsync(new CultMeshQuicRealtimeServerOptions
        {
            ListenEndPoint = new IPEndPoint(
                ParseBindAddress(options.ClientCultMeshHost),
                options.ClientCultMeshQuicPort),
            ServerCertificate = realtimeCertificate
        }).ConfigureAwait(false);
        Trace("quic-listen");
    }
    catch
    {
        providerSigningKey?.Dispose();
        realtimeCertificate.Dispose();
        content.Dispose();
        serverGroup?.Dispose();
        if (browserApp != null) await browserApp.DisposeAsync().ConfigureAwait(false);
        if (browserServer != null) await browserServer.DisposeAsync().ConfigureAwait(false);
        tcpServer.Dispose();
        throw;
    }
    advertisedEndpoint = remotePublication
        ? ""
        : $"cultnet+tcp://{options.ClientCultMeshAdvertiseHost}:{tcpServer.LocalEndPoint.Port}";
    if (!remotePublication)
    {
        advertisedContentEndpoint =
            $"{CultMeshTcpContentTransportConnector.Scheme}://{options.ClientCultMeshAdvertiseHost}:{content.LocalEndPoint.Port}";
        advertisedRealtimeEndpoint =
            $"{CultMeshQuicRealtimeTransportConnector.Scheme}://{options.ClientCultMeshAdvertiseHost}:{realtime.LocalEndPoint.Port}" +
            $"?cert-sha256={certificatePin}";
    }

    CultMeshAuthorityRoute[] CurrentAuthorityRoutes() => certifiedRoutes ?? new[]
    {
        (Endpoint: advertisedEndpoint, Protocol: CultMeshProtocols.Documents.Value),
        (Endpoint: advertisedBrowserEndpoint, Protocol: CultMeshProtocols.Documents.Value),
        (Endpoint: advertisedContentEndpoint, Protocol: CultMeshProtocols.Content.Value),
        (Endpoint: advertisedRealtimeEndpoint, Protocol: CultMeshProtocols.RealtimeState.Value)
    }
        .Where(route => !string.IsNullOrWhiteSpace(route.Endpoint))
        .Select(route => new CultMeshAuthorityRoute(
            options.DaemonId,
            route.Endpoint,
            new[] { route.Protocol },
            generation: localRouteGeneration))
        .ToArray();

    var proofSigners = remotePublication
        ? certifiedRoutes!
            .Where(route => route.Supports(CultMeshProtocols.Documents))
            .Select(route => new CultMeshSessionProofSigner(route, providerSigningKey!))
            .ToArray()
        : Array.Empty<CultMeshSessionProofSigner>();
    var sessionIdentity = new CultMeshSessionIdentityServer(
        server,
        options.DaemonId,
        new[] { options.VerseId },
        new[] { CultMeshProtocols.Documents.Value },
        CurrentAuthorityRoutes().Select(route => route.Generation).Distinct(StringComparer.Ordinal),
        proofSigners);
    tcpServer.PeerFailed += (endpoint, error) => Console.Error.WriteLine(
        $"Aetheria client CultMesh peer {endpoint} failed: {error.GetType().Name}: {error.Message}");
    server.OnCultNet<CultMeshVerseCatalogRequestMessage>((request, peer) =>
    {
        var descriptor = new CultMeshVerseDescriptor(
            options.VerseId,
            "Aetheria",
            CultMeshVerseAuthorityModel.OperatorCluster,
            new CultMeshVerseCompatibility(
                "cultmesh.v0",
                CultMeshVerseDescriptor.ComputeRulesHash("aetheria", "runtime-world.v1")),
            authorityRoutes: CurrentAuthorityRoutes(),
            description: "Aetheria provider Verse");
        peer.SendCultNet(new CultMeshVerseCatalogResponseMessage
        {
            MessageId = string.IsNullOrWhiteSpace(request.MessageId) ? Guid.NewGuid().ToString("N") : request.MessageId,
            Verses = new[] { descriptor.ToMessage() }
        });
        return Task.CompletedTask;
    });
    AetheriaBrowserEveCommandIngress.Register(server, node, options);
    server.OnCultNet<CultNetSnapshotRequestMessage>(async (request, peer) =>
    {
        try
        {
            var response = node.CreateRawSnapshotResponse(
                string.IsNullOrWhiteSpace(request.MessageId) ? Guid.NewGuid().ToString("N") : request.MessageId,
                request);
            if (TryGetScopedEveSurfaceRequest(request, out var scopedRecordKey, out var scopedSurfaceKind))
            {
                if (!response.Documents.Any(document =>
                        string.Equals(document.RecordKey, scopedRecordKey, StringComparison.Ordinal)))
                {
                    await InjectEveSurfaceSnapshotAsync(
                        node, options, request, response, scopedRecordKey, scopedSurfaceKind).ConfigureAwait(false);
                }
                peer.SendCultNet(response);
                return;
            }
            var frame = latestFrame();
            var hasFrame = frame != null;
            if (hasFrame && frame != null && SnapshotWants(
                request,
                AetheriaRuntimeDaemonSchemas.Frame,
                "daemon:aetheria.frame.latest.v1"))
            {
                var framePut = node.Database.Documents.CreateRawDocumentPutMessage(
                    response.MessageId,
                    new CultRecordHandle<AetheriaRuntimeDaemonFrameDocument>(
                        new CultRecordKey("daemon:aetheria.frame.latest.v1")),
                    frame,
                    new CultNetDocumentMessageOptions
                    {
                        SourceRuntimeId = options.DaemonId,
                        SourceRole = "aetheria-daemon"
                    });
                response.Documents = response.Documents
                    .Where(document => !string.Equals(document.SchemaId, framePut.Document.SchemaId, StringComparison.Ordinal) ||
                        !string.Equals(document.RecordKey, framePut.Document.RecordKey, StringComparison.Ordinal))
                    .Concat(new[] { framePut.Document })
                    .ToArray();
            }

            var health = await node.MutableDocument<AetheriaRuntimeDaemonHealthDocument>(AetheriaRuntimeVerseRecordKeys.DaemonHealth).ReadAsync().ConfigureAwait(false);
            if (health != null && SnapshotWants(
                request,
                AetheriaRuntimeDaemonSchemas.Health,
                AetheriaRuntimeVerseRecordKeys.DaemonHealth.ToString()))
            {
                var healthPut = node.Database.Documents.CreateRawDocumentPutMessage(
                    response.MessageId,
                    new CultRecordHandle<AetheriaRuntimeDaemonHealthDocument>(
                        AetheriaRuntimeVerseRecordKeys.DaemonHealth),
                    health,
                    new CultNetDocumentMessageOptions
                    {
                        SourceRuntimeId = options.DaemonId,
                        SourceRole = "aetheria-daemon"
                    });
                response.Documents = response.Documents
                    .Where(document => !string.Equals(document.SchemaId, healthPut.Document.SchemaId, StringComparison.Ordinal) ||
                        !string.Equals(document.RecordKey, healthPut.Document.RecordKey, StringComparison.Ordinal))
                    .Concat(new[] { healthPut.Document })
                    .ToArray();
            }

            var authorityPolicy = await node.MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy).ReadAsync().ConfigureAwait(false);
            if (authorityPolicy != null && SnapshotWants(
                request,
                AetheriaRuntimeDaemonSchemas.VerseAuthorityPolicy,
                AetheriaRuntimeVerseAuthorityPolicyDocument.DocumentKey))
            {
                var authorityPut = node.Database.Documents.CreateRawDocumentPutMessage(
                    response.MessageId,
                    new CultRecordHandle<AetheriaRuntimeVerseAuthorityPolicyDocument>(
                        new CultRecordKey(AetheriaRuntimeVerseAuthorityPolicyDocument.DocumentKey)),
                    authorityPolicy,
                    new CultNetDocumentMessageOptions
                    {
                        SourceRuntimeId = options.DaemonId,
                        SourceRole = "aetheria-daemon"
                    });
                response.Documents = response.Documents
                    .Where(document => !string.Equals(document.SchemaId, authorityPut.Document.SchemaId, StringComparison.Ordinal) ||
                        !string.Equals(document.RecordKey, authorityPut.Document.RecordKey, StringComparison.Ordinal))
                    .Concat(new[] { authorityPut.Document })
                    .ToArray();
            }

            var starbridgeScenario = await node.MutableDocument<AetheriaRuntimeStarbridgeScenarioDocument>(AetheriaRuntimeVerseRecordKeys.StarbridgeScenarioLatest).ReadAsync().ConfigureAwait(false);
            if (starbridgeScenario != null && SnapshotWants(
                request,
                AetheriaRuntimeDaemonSchemas.StarbridgeScenario,
                AetheriaRuntimeVerseRecordKeys.StarbridgeScenarioLatest.ToString()))
            {
                var scenarioPut = node.Database.Documents.CreateRawDocumentPutMessage(
                    response.MessageId,
                    new CultRecordHandle<AetheriaRuntimeStarbridgeScenarioDocument>(
                        AetheriaRuntimeVerseRecordKeys.StarbridgeScenarioLatest),
                    starbridgeScenario,
                    new CultNetDocumentMessageOptions
                    {
                        SourceRuntimeId = options.DaemonId,
                        SourceRole = "aetheria-daemon"
                    });
                response.Documents = response.Documents
                    .Where(document => !string.Equals(document.SchemaId, scenarioPut.Document.SchemaId, StringComparison.Ordinal) ||
                        !string.Equals(document.RecordKey, scenarioPut.Document.RecordKey, StringComparison.Ordinal))
                    .Concat(new[] { scenarioPut.Document })
                    .ToArray();
            }

            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.DaemonGameSurface.ToString(),
                "game").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.DaemonGameTuiSurface.ToString(),
                "game-tui").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.DaemonEditorSurface.ToString(),
                "editor").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.DaemonEditorTuiSurface.ToString(),
                "editor-tui").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.MainMenuSurface.ToString(),
                "main-menu").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.MainMenuSettingsSurface.ToString(),
                "main-menu-settings").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.MainMenuInputSettingsSurface.ToString(),
                "main-menu-input-settings").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.MainMenuPlayerSettingsSurface.ToString(),
                "main-menu-player-settings").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.InventoryPanelSurface.ToString(),
                "inventory-panel").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.InventoryDropdownSurface.ToString(),
                "inventory-dropdown").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.MapMenuSurface.ToString(),
                "map-menu").ConfigureAwait(false);
            await InjectEveSurfaceSnapshotAsync(
                node,
                options,
                request,
                response,
                AetheriaRuntimeVerseRecordKeys.TradeMenuSurface.ToString(),
                "trade-menu").ConfigureAwait(false);

            var starbridgeSession = await node.MutableDocument<AetheriaRuntimeStarbridgeSessionSummaryDocument>(AetheriaRuntimeVerseRecordKeys.StarbridgeSessionSummary).ReadAsync().ConfigureAwait(false);
            if (starbridgeSession != null &&
                SnapshotWants(
                    request,
                    AetheriaRuntimeDaemonSchemas.StarbridgeSessionSummary,
                    AetheriaRuntimeVerseRecordKeys.StarbridgeSessionSummary.ToString()))
            {
                var starbridgePut = node.Database.Documents.CreateRawDocumentPutMessage(
                    response.MessageId,
                    new CultRecordHandle<AetheriaRuntimeStarbridgeSessionSummaryDocument>(
                        AetheriaRuntimeVerseRecordKeys.StarbridgeSessionSummary),
                    starbridgeSession,
                    new CultNetDocumentMessageOptions
                    {
                        SourceRuntimeId = options.DaemonId,
                        SourceRole = "aetheria-daemon"
                    });
                response.Documents = response.Documents
                    .Where(document => !string.Equals(document.SchemaId, starbridgePut.Document.SchemaId, StringComparison.Ordinal) ||
                        !string.Equals(document.RecordKey, starbridgePut.Document.RecordKey, StringComparison.Ordinal))
                    .Concat(new[] { starbridgePut.Document })
                    .ToArray();
            }

            var assetManifest = await node.MutableDocument<AetheriaRuntimeAssetManifestDocument>(AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest).ReadAsync().ConfigureAwait(false);
            if (assetManifest != null &&
                SnapshotWants(
                    request,
                    AetheriaRuntimeDaemonSchemas.AssetManifest,
                    AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest.ToString()))
            {
                var assetManifestPut = node.Database.Documents.CreateRawDocumentPutMessage(
                    response.MessageId,
                    new CultRecordHandle<AetheriaRuntimeAssetManifestDocument>(
                        AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest),
                    assetManifest,
                    new CultNetDocumentMessageOptions
                    {
                        SourceRuntimeId = options.DaemonId,
                        SourceRole = "aetheria-daemon"
                    });
                response.Documents = response.Documents
                    .Where(document => !string.Equals(document.SchemaId, assetManifestPut.Document.SchemaId, StringComparison.Ordinal) ||
                        !string.Equals(document.RecordKey, assetManifestPut.Document.RecordKey, StringComparison.Ordinal))
                    .Concat(new[] { assetManifestPut.Document })
                    .ToArray();
            }

            var factPuts = node.Documents<AetheriaRuntimeCommittedCommandFactDocument>()
                .OrderBy(fact => fact.CommittedAtUtc ?? "", StringComparer.Ordinal)
                .ThenBy(fact => fact.FactId ?? "", StringComparer.Ordinal)
                .Select(fact => new
                {
                    Fact = fact,
                    RecordKey = AetheriaRuntimeCommittedCommandFactDocument.CreateRecordKey(fact.FactId)
                })
                .Where(item => SnapshotWants(
                    request,
                    AetheriaRuntimeDaemonSchemas.CommittedCommandFact,
                    item.RecordKey))
                .Select(item => node.Database.Documents.CreateRawDocumentPutMessage(
                    response.MessageId,
                    new CultRecordHandle<AetheriaRuntimeCommittedCommandFactDocument>(
                        new CultRecordKey(item.RecordKey)),
                    item.Fact,
                    new CultNetDocumentMessageOptions
                    {
                        SourceRuntimeId = options.DaemonId,
                        SourceRole = "aetheria-daemon"
                    }).Document)
                .ToArray();
            if (factPuts.Length > 0)
            {
                var factKeys = factPuts
                    .Select(document => document.RecordKey)
                    .ToHashSet(StringComparer.Ordinal);
                response.Documents = response.Documents
                    .Where(document => !string.Equals(document.SchemaId, AetheriaRuntimeDaemonSchemas.CommittedCommandFact, StringComparison.Ordinal) ||
                        !factKeys.Contains(document.RecordKey))
                    .Concat(factPuts)
                    .ToArray();
            }

            if (hasFrame && frame != null && TryGetGameViewportRequest(request, out var viewportRecordKey, out var viewportSchemaId, out var viewport))
            {
                if (string.Equals(viewportSchemaId, AetheriaRuntimeDaemonSchemas.ObjectsViewport, StringComparison.Ordinal))
                {
                    var viewportPut = node.Database.Documents.CreateRawDocumentPutMessage(
                        response.MessageId,
                        new CultRecordHandle<AetheriaRuntimeObjectsViewportDocument>(
                            new CultRecordKey(viewportRecordKey)),
                        AetheriaRuntimeGameDocuments.ObjectsViewport(frame, viewport),
                        new CultNetDocumentMessageOptions
                        {
                            SourceRuntimeId = options.DaemonId,
                            SourceRole = "aetheria-daemon"
                        });
                    response.Documents = response.Documents
                        .Where(document => !string.Equals(document.SchemaId, viewportPut.Document.SchemaId, StringComparison.Ordinal) ||
                            !string.Equals(document.RecordKey, viewportPut.Document.RecordKey, StringComparison.Ordinal))
                        .Concat(new[] { viewportPut.Document })
                        .ToArray();
                }
                else if (string.Equals(viewportSchemaId, AetheriaRuntimeDaemonSchemas.GravityViewport, StringComparison.Ordinal))
                {
                    var viewportPut = node.Database.Documents.CreateRawDocumentPutMessage(
                        response.MessageId,
                        new CultRecordHandle<AetheriaRuntimeGravityViewportDocument>(
                            new CultRecordKey(viewportRecordKey)),
                        AetheriaRuntimeGameDocuments.GravityViewport(frame, viewport),
                        new CultNetDocumentMessageOptions
                        {
                            SourceRuntimeId = options.DaemonId,
                            SourceRole = "aetheria-daemon"
                        });
                    response.Documents = response.Documents
                        .Where(document => !string.Equals(document.SchemaId, viewportPut.Document.SchemaId, StringComparison.Ordinal) ||
                            !string.Equals(document.RecordKey, viewportPut.Document.RecordKey, StringComparison.Ordinal))
                        .Concat(new[] { viewportPut.Document })
                        .ToArray();
                }
                else if (string.Equals(viewportSchemaId, AetheriaRuntimeDaemonSchemas.RenderSplatsViewport, StringComparison.Ordinal))
                {
                    var viewportPut = node.Database.Documents.CreateRawDocumentPutMessage(
                        response.MessageId,
                        new CultRecordHandle<GameCult.Eve.PluginFields.EveFieldsSplatsDocument>(
                            new CultRecordKey(viewportRecordKey)),
                        AetheriaRuntimeGameDocuments.RenderSplatsViewport(frame, viewport),
                        new CultNetDocumentMessageOptions
                        {
                            SourceRuntimeId = options.DaemonId,
                            SourceRole = "aetheria-daemon"
                        });
                    response.Documents = response.Documents
                        .Where(document => !string.Equals(document.SchemaId, viewportPut.Document.SchemaId, StringComparison.Ordinal) ||
                            !string.Equals(document.RecordKey, viewportPut.Document.RecordKey, StringComparison.Ordinal))
                        .Concat(new[] { viewportPut.Document })
                        .ToArray();
                }
                else
                {
                    var viewportPut = node.Database.Documents.CreateRawDocumentPutMessage(
                        response.MessageId,
                        new CultRecordHandle<AetheriaRuntimeGameViewportDocument>(
                            new CultRecordKey(viewportRecordKey)),
                        AetheriaRuntimeGameDocuments.Viewport(frame, viewport),
                        new CultNetDocumentMessageOptions
                        {
                            SourceRuntimeId = options.DaemonId,
                            SourceRole = "aetheria-daemon"
                        });
                    response.Documents = response.Documents
                        .Where(document => !string.Equals(document.SchemaId, viewportPut.Document.SchemaId, StringComparison.Ordinal) ||
                            !string.Equals(document.RecordKey, viewportPut.Document.RecordKey, StringComparison.Ordinal))
                        .Concat(new[] { viewportPut.Document })
                        .ToArray();
                }
            }

            if (hasFrame && frame != null && TryGetIndexedGameDocumentRequest(request, out var indexedRecordKey, out var indexedSchemaId, out var indexedEntityIndex))
            {
                if (string.Equals(indexedSchemaId, AetheriaRuntimeDaemonSchemas.SelectedObject, StringComparison.Ordinal))
                {
                    var selectedObjectPut = node.Database.Documents.CreateRawDocumentPutMessage(
                        response.MessageId,
                        new CultRecordHandle<AetheriaRuntimeSelectedObjectDocument>(
                            new CultRecordKey(indexedRecordKey)),
                        AetheriaRuntimeGameDocuments.SelectedObject(frame, indexedEntityIndex),
                        new CultNetDocumentMessageOptions
                        {
                            SourceRuntimeId = options.DaemonId,
                            SourceRole = "aetheria-daemon"
                        });
                    response.Documents = response.Documents
                        .Where(document => !string.Equals(document.SchemaId, selectedObjectPut.Document.SchemaId, StringComparison.Ordinal) ||
                            !string.Equals(document.RecordKey, selectedObjectPut.Document.RecordKey, StringComparison.Ordinal))
                        .Concat(new[] { selectedObjectPut.Document })
                        .ToArray();
                }
                else if (string.Equals(indexedSchemaId, AetheriaRuntimeDaemonSchemas.Inventory, StringComparison.Ordinal))
                {
                    var inventoryPut = node.Database.Documents.CreateRawDocumentPutMessage(
                        response.MessageId,
                        new CultRecordHandle<AetheriaRuntimeInventoryDocument>(
                            new CultRecordKey(indexedRecordKey)),
                        AetheriaRuntimeGameDocuments.Inventory(frame, indexedEntityIndex),
                        new CultNetDocumentMessageOptions
                        {
                            SourceRuntimeId = options.DaemonId,
                            SourceRole = "aetheria-daemon"
                        });
                    response.Documents = response.Documents
                        .Where(document => !string.Equals(document.SchemaId, inventoryPut.Document.SchemaId, StringComparison.Ordinal) ||
                            !string.Equals(document.RecordKey, inventoryPut.Document.RecordKey, StringComparison.Ordinal))
                        .Concat(new[] { inventoryPut.Document })
                        .ToArray();
                }
            }

            InjectCultMeshCdnManifestSnapshots(
                node,
                options.DaemonId,
                bundleCdnManifests,
                request,
                response);
            peer.SendCultNet(response);
        }
        catch (Exception ex)
        {
            peer.SendCultNet(new CultNetErrorMessage
            {
                Error = $"Aetheria snapshot response failed: {ex.GetType().Name}: {ex.Message}"
            });
        }
    });
    server.OnCultNet<CultNetDocumentPutRawMessage>(async (message, peer) =>
    {
        try
        {
            if (!sessionIdentity.TryGetSourceRuntimeId(peer, out var sourceRuntimeId))
                throw new InvalidOperationException("CultMesh session identity must be established before command ingress.");
            if (!string.Equals(message.Document.PayloadEncoding, "messagepack", StringComparison.OrdinalIgnoreCase) ||
                node.Database.Documents.DeserializeRawDocument(message.Document) is not EveSurfaceCommandRequest request)
                throw new InvalidOperationException("The public Aetheria CultMesh boundary accepts typed Eve command intents only.");
            var expectedRecordKey = AetheriaRuntimeVerseRecordKeys.EveCommandRecordPrefix + ":" + request.CommandId;
            if (string.IsNullOrWhiteSpace(request.CommandId) ||
                !string.Equals(message.Document.RecordKey, expectedRecordKey, StringComparison.Ordinal))
                throw new InvalidOperationException("Eve command record identity does not match its typed command id.");
            if (string.IsNullOrWhiteSpace(request.ProviderId) ||
                string.IsNullOrWhiteSpace(request.SurfaceId) ||
                string.IsNullOrWhiteSpace(request.Command))
                throw new InvalidOperationException("Eve command provider, surface, and command are required.");
            if (!string.Equals(request.ClientId, sourceRuntimeId, StringComparison.Ordinal))
                throw new InvalidOperationException("Eve command client identity does not match the established CultMesh session.");

            await AetheriaHangarCommandJournal.AdmitAsync(
                node,
                new CultRecordKey(expectedRecordKey),
                request,
                DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            peer.SendCultNet(new CultNetErrorMessage
            {
                Error = $"Aetheria command ingress rejected document put: {error.Message}"
            });
        }
    });
    if (browserApp != null)
    {
        try
        {
            await browserApp.StartAsync().ConfigureAwait(false);
            var browserAddress = browserApp.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            advertisedBrowserEndpoint =
                browserAddress
                    .Replace("https://", "wss://", StringComparison.Ordinal)
                    .Replace("http://", "ws://", StringComparison.Ordinal) + "/cultmesh";
            if (!string.Equals(options.ClientCultMeshAdvertiseHost, "127.0.0.1", StringComparison.Ordinal) &&
                !string.Equals(options.ClientCultMeshAdvertiseHost, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                var browserUri = new Uri(advertisedBrowserEndpoint);
                advertisedBrowserEndpoint =
                    $"{(remotePublication ? "wss" : "ws")}://{options.ClientCultMeshAdvertiseHost}:{browserUri.Port}{browserUri.AbsolutePath}";
            }
        }
        catch
        {
            sessionIdentity.Dispose();
            serverGroup?.Dispose();
            await browserApp.DisposeAsync().ConfigureAwait(false);
            await browserServer!.DisposeAsync().ConfigureAwait(false);
            await realtime.DisposeAsync().ConfigureAwait(false);
            realtimeCertificate.Dispose();
            providerSigningKey?.Dispose();
            content.Dispose();
            tcpServer.Dispose();
            throw;
        }
    }
    return new AetheriaClientCultMeshHost(
        server,
        serverGroup,
        sessionIdentity,
        tcpServer,
        browserServer,
        browserApp,
        content,
        realtime,
        realtimeCertificate,
        providerSigningKey,
        advertisedEndpoint,
        advertisedBrowserEndpoint,
        advertisedContentEndpoint,
        advertisedRealtimeEndpoint);
}

static X509Certificate2 CreateRealtimeCertificate(string advertisedHost)
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest(
        $"CN={advertisedHost}",
        key,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        new OidCollection { new("1.3.6.1.5.5.7.3.1") },
        false));
    var names = new SubjectAlternativeNameBuilder();
    if (IPAddress.TryParse(advertisedHost, out var address)) names.AddIpAddress(address);
    else names.AddDnsName(advertisedHost);
    request.CertificateExtensions.Add(names.Build());
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    return request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow.AddDays(7));
}

static X509Certificate2 LoadOrCreateRealtimeCertificate(
    string advertisedHost,
    string certificatePath,
    string certificatePassword = "",
    bool requireExisting = false)
{
    if (string.IsNullOrWhiteSpace(certificatePath))
        throw new ArgumentException("Realtime certificate path must be non-empty.", nameof(certificatePath));

    if (File.Exists(certificatePath))
    {
        try
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                string.IsNullOrEmpty(certificatePassword) ? null : certificatePassword,
                X509KeyStorageFlags.DefaultKeySet);
            if (existing.HasPrivateKey &&
                existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(1) &&
                string.Equals(
                    existing.GetNameInfo(X509NameType.DnsName, forIssuer: false),
                    advertisedHost,
                    StringComparison.OrdinalIgnoreCase))
                return existing;
            existing.Dispose();
        }
        catch (CryptographicException)
        {
            if (requireExisting)
                throw;
            // Replace an unreadable or obsolete development certificate below.
        }
    }

    if (requireExisting)
        throw new InvalidOperationException(
            $"Remote CultMesh publication requires a valid, unexpired TLS certificate for '{advertisedHost}' at '{certificatePath}'.");

    var generated = CreateRealtimeCertificate(advertisedHost);
    Directory.CreateDirectory(Path.GetDirectoryName(certificatePath) ?? ".");
    var temporaryPath = certificatePath + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
        File.WriteAllBytes(temporaryPath, generated.Export(X509ContentType.Pfx));
        File.Move(temporaryPath, certificatePath, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
    return generated;
}

static void InjectCultMeshCdnManifestSnapshots(
    AetheriaStateNode node,
    string daemonId,
    IReadOnlyDictionary<string, CultMeshCdnArtifactManifest> bundleCdnManifests,
    CultNetSnapshotRequestMessage request,
    CultNetSnapshotResponseRawMessage response)
{
    var recordKeys = request.RecordKeys ?? Array.Empty<string>();
    if (recordKeys.Length == 0)
        return;

    var documents = new List<CultNetRawDocumentRecord>();
    foreach (var recordKey in recordKeys.Distinct(StringComparer.Ordinal))
    {
        if (bundleCdnManifests.TryGetValue(recordKey, out var manifest))
        {
            var bundleDocument = node.Database.Documents.CreateRawDocumentPutMessage(
                $"aetheria-cdn:{recordKey}",
                new CultRecordHandle<CultMeshCdnArtifactManifest>(new CultRecordKey(recordKey)),
                manifest,
                new CultNetDocumentMessageOptions
                {
                    SourceRuntimeId = daemonId,
                    SourceRole = "aetheria-cultmesh-cdn",
                    Tags = ["aetheria", "cultmesh-cdn", "asset-bundle"]
                }).Document;
            if (string.Equals(Environment.GetEnvironmentVariable("AETHERIA_TRACE_EVE_SNAPSHOTS"), "1", StringComparison.Ordinal))
                Console.WriteLine($"Eve CDN snapshot schema={bundleDocument.SchemaId} record={recordKey}");
            documents.Add(bundleDocument);
        }
    }

    if (documents.Count == 0)
        return;

    var keys = documents.Select(document => document.RecordKey).ToHashSet(StringComparer.Ordinal);
    response.Documents = response.Documents
        .Where(document => !keys.Contains(document.RecordKey))
        .Concat(documents)
        .ToArray();
}

static IReadOnlyDictionary<string, CultMeshCdnArtifactManifest> BuildBundleCdnManifestIndex(
    AetheriaUnityBundleArtifactSet unityBundles)
{
    var manifests = new Dictionary<string, CultMeshCdnArtifactManifest>(StringComparer.Ordinal);
    foreach (var bundle in unityBundles.Bundles)
    {
        var artifact = bundle.Artifact;
        manifests[artifact.ManifestKey.ToString()] = artifact.Manifest;
    }
    return manifests;
}

static async Task InjectEveSurfaceSnapshotAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    CultNetSnapshotRequestMessage request,
    CultNetSnapshotResponseRawMessage response,
    string recordKey,
    string surfaceKind)
{
    const string EveSurfaceSchema = "gamecult.eve.surface.v1";
    var trace = string.Equals(Environment.GetEnvironmentVariable("AETHERIA_TRACE_EVE_SNAPSHOTS"), "1", StringComparison.Ordinal);
    if (!SnapshotWants(request, EveSurfaceSchema, recordKey))
        return;

    var surfaceState = await ReadEveSurfacePublicationAsync(node, surfaceKind).ConfigureAwait(false);
    if (trace)
    {
        Console.WriteLine(
            $"Eve snapshot request kind={surfaceKind} schemas=[{string.Join(",", request.SchemaIds ?? Array.Empty<string>())}] " +
            $"records=[{string.Join(",", request.RecordKeys ?? Array.Empty<string>())}] surface={(surfaceState == null ? "missing" : surfaceState.Surface.Id)} " +
            $"playerPosition={(surfaceState == null ? "missing" : FindControllablePosition(surfaceState.Surface.Root))}");
    }
    if (surfaceState == null)
        return;

    var surfacePut = node.Database.Documents.CreateRawDocumentPutMessage(
        response.MessageId,
        new CultRecordHandle<EveSurfaceDocument>(new CultRecordKey(recordKey)),
        surfaceState,
        new CultNetDocumentMessageOptions
        {
            SourceRuntimeId = options.DaemonId,
            SourceRole = "aetheria-daemon"
        });
    response.Documents = response.Documents
        .Where(document => !string.Equals(document.RecordKey, surfacePut.Document.RecordKey, StringComparison.Ordinal))
        .Concat(new[] { surfacePut.Document })
        .ToArray();
    if (trace)
        Console.WriteLine($"Eve snapshot response schema={surfacePut.Document.SchemaId} record={surfacePut.Document.RecordKey}");
}

static string FindControllablePosition(EveSurfaceComponent component)
{
    if (component.Props.TryGetValue("controllable", out var controllable) &&
        string.Equals(controllable, "true", StringComparison.OrdinalIgnoreCase))
        return component.GetProp("position", "missing");
    foreach (var child in component.Children)
    {
        var position = FindControllablePosition(child);
        if (!string.Equals(position, "missing", StringComparison.Ordinal))
            return position;
    }
    return "missing";
}

static bool TryGetScopedEveSurfaceRequest(
    CultNetSnapshotRequestMessage request,
    out string recordKey,
    out string surfaceKind)
{
    recordKey = "";
    surfaceKind = "";
    if (request.RecordKeys is not { Length: 1 })
        return false;
    if (request.SchemaIds is { Length: > 0 } &&
        !request.SchemaIds.Contains(EveSurfaceDocument.SchemaId, StringComparer.Ordinal))
        return false;

    recordKey = request.RecordKeys[0];
    surfaceKind = recordKey switch
    {
        var value when value == AetheriaRuntimeVerseRecordKeys.DaemonGameSurface.ToString() => "game",
        var value when value == AetheriaRuntimeVerseRecordKeys.DaemonGameTuiSurface.ToString() => "game-tui",
        var value when value == AetheriaRuntimeVerseRecordKeys.DaemonEditorSurface.ToString() => "editor",
        var value when value == AetheriaRuntimeVerseRecordKeys.DaemonEditorTuiSurface.ToString() => "editor-tui",
        var value when value == AetheriaRuntimeVerseRecordKeys.MainMenuSurface.ToString() => "main-menu",
        var value when value == AetheriaRuntimeVerseRecordKeys.MainMenuSettingsSurface.ToString() => "main-menu-settings",
        var value when value == AetheriaRuntimeVerseRecordKeys.MainMenuInputSettingsSurface.ToString() => "main-menu-input-settings",
        var value when value == AetheriaRuntimeVerseRecordKeys.MainMenuPlayerSettingsSurface.ToString() => "main-menu-player-settings",
        var value when value == AetheriaRuntimeVerseRecordKeys.InventoryPanelSurface.ToString() => "inventory-panel",
        var value when value == AetheriaRuntimeVerseRecordKeys.InventoryDropdownSurface.ToString() => "inventory-dropdown",
        var value when value == AetheriaRuntimeVerseRecordKeys.MapMenuSurface.ToString() => "map-menu",
        var value when value == AetheriaRuntimeVerseRecordKeys.TradeMenuSurface.ToString() => "trade-menu",
        _ => ""
    };
    return surfaceKind.Length > 0;
}

static Task<EveSurfaceDocument?> ReadEveSurfacePublicationAsync(AetheriaStateNode node, string surfaceKind)
{
    var key = surfaceKind switch
    {
        "game" => AetheriaRuntimeVerseRecordKeys.DaemonGameSurface,
        "game-tui" => AetheriaRuntimeVerseRecordKeys.DaemonGameTuiSurface,
        "editor" => AetheriaRuntimeVerseRecordKeys.DaemonEditorSurface,
        "editor-tui" => AetheriaRuntimeVerseRecordKeys.DaemonEditorTuiSurface,
        "main-menu" => AetheriaRuntimeVerseRecordKeys.MainMenuSurface,
        "main-menu-settings" => AetheriaRuntimeVerseRecordKeys.MainMenuSettingsSurface,
        "main-menu-input-settings" => AetheriaRuntimeVerseRecordKeys.MainMenuInputSettingsSurface,
        "main-menu-player-settings" => AetheriaRuntimeVerseRecordKeys.MainMenuPlayerSettingsSurface,
        "inventory-panel" => AetheriaRuntimeVerseRecordKeys.InventoryPanelSurface,
        "inventory-dropdown" => AetheriaRuntimeVerseRecordKeys.InventoryDropdownSurface,
        "map-menu" => AetheriaRuntimeVerseRecordKeys.MapMenuSurface,
        "trade-menu" => AetheriaRuntimeVerseRecordKeys.TradeMenuSurface,
        _ => throw new ArgumentOutOfRangeException(nameof(surfaceKind), surfaceKind, "Unknown Eve surface publication.")
    };
    return ReadPortableSurfaceAsync(node, key);
}

static async Task<EveSurfaceDocument?> ReadPortableSurfaceAsync(AetheriaStateNode node, CultRecordKey key)
{
    return await node.MutableDocument<EveSurfaceDocument>(key).ReadAsync().ConfigureAwait(false);
}

static IPAddress ParseBindAddress(string host)
{
    if (string.IsNullOrWhiteSpace(host) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        return IPAddress.Loopback;
    }

    if (string.Equals(host, "*", StringComparison.Ordinal) ||
        string.Equals(host, "0.0.0.0", StringComparison.Ordinal) ||
        string.Equals(host, "any", StringComparison.OrdinalIgnoreCase))
    {
        return IPAddress.Any;
    }

    return IPAddress.Parse(host);
}

static ECDsa LoadP256PrivateKey(string path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        throw new InvalidOperationException($"P-256 signing key does not exist: '{path}'.");
    var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    try
    {
        key.ImportFromPem(File.ReadAllText(path));
        if (key.KeySize != 256)
            throw new InvalidOperationException("CultMesh route signing requires a P-256 key.");
        return key;
    }
    catch
    {
        key.Dispose();
        throw;
    }
}

static CultMeshAuthorityRoute[] LoadAuthorityRouteGrant(
    string path,
    string verseId,
    string authorityRuntimeId,
    CultMeshEcdsaP256PublicKey providerKey,
    CultMeshAuthorityTrustPolicy trust,
    IReadOnlyDictionary<string, string> expectedEndpoints)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        throw new InvalidOperationException($"Odin authority route grant does not exist: '{path}'.");

    CultMeshVerseDescriptor descriptor;
    try
    {
        var message = MessagePackSerializer.Deserialize<CultMeshVerseDescriptorMessage>(File.ReadAllBytes(path));
        descriptor = message.ToVerseDescriptor();
    }
    catch (Exception error) when (error is MessagePackSerializationException or InvalidOperationException or ArgumentException)
    {
        throw new InvalidOperationException("Odin authority route grant is not a valid typed CultMesh Verse descriptor.", error);
    }

    if (!string.Equals(descriptor.VerseId, verseId, StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"Odin authority route grant names Verse '{descriptor.VerseId}', expected '{verseId}'.");

    var now = DateTimeOffset.UtcNow;
    var selected = new List<CultMeshAuthorityRoute>();
    foreach (var expected in expectedEndpoints)
    {
        var matches = descriptor.AuthorityRoutes
            .Where(route => string.Equals(route.AuthorityRuntimeId, authorityRuntimeId, StringComparison.Ordinal))
            .Where(route => string.Equals(route.Endpoint, expected.Value, StringComparison.Ordinal))
            .Where(route => route.ProtocolIds.Contains(expected.Key, StringComparer.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"Odin authority route grant must contain exactly one '{expected.Key}' route for " +
                $"runtime '{authorityRuntimeId}' at '{expected.Value}'.");

        var route = matches[0];
        var certificate = route.Certificate ?? throw new InvalidOperationException(
            $"Odin authority route grant contains an unsigned remote route: '{route.Endpoint}'.");
        if (!string.Equals(certificate.ProviderKey.KeyId, providerKey.KeyId, StringComparison.Ordinal) ||
            !string.Equals(certificate.ProviderKey.X, providerKey.X, StringComparison.Ordinal) ||
            !string.Equals(certificate.ProviderKey.Y, providerKey.Y, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Odin authority route grant provider key does not match '{providerKey.KeyId}'.");
        }
        trust.Validate(verseId, route, now);
        selected.Add(route);
    }

    return selected.ToArray();
}

static bool IsLoopbackHost(string host)
{
    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        return true;
    return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}

static bool SnapshotWants(
    CultNetSnapshotRequestMessage request,
    string schemaId,
    string recordKey)
{
    var schemaIds = request.SchemaIds ?? Array.Empty<string>();
    var recordKeys = request.RecordKeys ?? Array.Empty<string>();
    var schemaMatches = schemaIds.Length == 0 || schemaIds.Contains(schemaId, StringComparer.Ordinal);
    var recordMatches = recordKeys.Length == 0 || recordKeys.Contains(recordKey, StringComparer.Ordinal);
    return schemaMatches && recordMatches;
}

static bool TryGetGameViewportRequest(
    CultNetSnapshotRequestMessage request,
    out string recordKey,
    out string schemaId,
    out AetheriaRuntimeViewportBounds viewport)
{
    const string prefix = "daemon:aetheria.game.viewport.v1;";
    recordKey = "";
    schemaId = "";
    viewport = new AetheriaRuntimeViewportBounds();

    var schemaIds = request.SchemaIds ?? Array.Empty<string>();
    var allowedSchemas = new[]
    {
        AetheriaRuntimeDaemonSchemas.GameViewport,
        AetheriaRuntimeDaemonSchemas.ObjectsViewport,
        AetheriaRuntimeDaemonSchemas.GravityViewport,
        AetheriaRuntimeDaemonSchemas.RenderSplatsViewport
    };
    if (schemaIds.Length > 0 && !schemaIds.Any(candidate => allowedSchemas.Contains(candidate, StringComparer.Ordinal)))
        return false;

    foreach (var candidate in request.RecordKeys ?? Array.Empty<string>())
    {
        if (candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            var values = candidate
                .Substring(prefix.Length)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split(new[] { '=' }, 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            if (!TryGetDouble(values, "minX", out var minX) ||
                !TryGetDouble(values, "minY", out var minY) ||
                !TryGetDouble(values, "maxX", out var maxX) ||
                !TryGetDouble(values, "maxY", out var maxY))
            {
                continue;
            }

            recordKey = candidate;
            schemaId = schemaIds.FirstOrDefault(known => allowedSchemas.Contains(known, StringComparer.Ordinal))
                ?? AetheriaRuntimeDaemonSchemas.GameViewport;
            viewport = new AetheriaRuntimeViewportBounds
            {
                MinX = Math.Min(minX, maxX),
                MinY = Math.Min(minY, maxY),
                MaxX = Math.Max(minX, maxX),
                MaxY = Math.Max(minY, maxY)
            };
            return true;
        }

        if (TryGetManagedViewportRequest(candidate, out var managedSchemaId, out viewport))
        {
            if (schemaIds.Length > 0 && !schemaIds.Contains(managedSchemaId, StringComparer.Ordinal))
                continue;

            recordKey = candidate;
            schemaId = managedSchemaId;
            return true;
        }
    }

    return false;
}

static bool TryGetManagedViewportRequest(
    string recordKey,
    out string schemaId,
    out AetheriaRuntimeViewportBounds viewport)
{
    schemaId = "";
    viewport = new AetheriaRuntimeViewportBounds();
    var prefix = "";
    if (recordKey.StartsWith("aetheria.viewport.map.", StringComparison.Ordinal))
    {
        prefix = "aetheria.viewport.map.";
        schemaId = AetheriaRuntimeDaemonSchemas.GameViewport;
    }
    else if (recordKey.StartsWith("aetheria.viewport.objects.", StringComparison.Ordinal))
    {
        prefix = "aetheria.viewport.objects.";
        schemaId = AetheriaRuntimeDaemonSchemas.ObjectsViewport;
    }
    else if (recordKey.StartsWith("aetheria.viewport.gravity.", StringComparison.Ordinal))
    {
        prefix = "aetheria.viewport.gravity.";
        schemaId = AetheriaRuntimeDaemonSchemas.GravityViewport;
    }
    else if (recordKey.StartsWith("aetheria.viewport.render_splats.", StringComparison.Ordinal))
    {
        prefix = "aetheria.viewport.render_splats.";
        schemaId = AetheriaRuntimeDaemonSchemas.RenderSplatsViewport;
    }
    else
    {
        return false;
    }

    var parts = recordKey.Substring(prefix.Length).Split('.');
    if (parts.Length != 4 ||
        !TryParseViewportToken(parts[0], out var minX) ||
        !TryParseViewportToken(parts[1], out var minY) ||
        !TryParseViewportToken(parts[2], out var maxX) ||
        !TryParseViewportToken(parts[3], out var maxY))
    {
        return false;
    }

    viewport = new AetheriaRuntimeViewportBounds
    {
        MinX = Math.Min(minX, maxX),
        MinY = Math.Min(minY, maxY),
        MaxX = Math.Max(minX, maxX),
        MaxY = Math.Max(minY, maxY)
    };
    return true;
}

static bool TryGetIndexedGameDocumentRequest(
    CultNetSnapshotRequestMessage request,
    out string recordKey,
    out string schemaId,
    out int entityIndex)
{
    recordKey = "";
    schemaId = "";
    entityIndex = -1;
    var schemaIds = request.SchemaIds ?? Array.Empty<string>();
    var allowedSchemas = new[]
    {
        AetheriaRuntimeDaemonSchemas.SelectedObject,
        AetheriaRuntimeDaemonSchemas.Inventory
    };
    if (schemaIds.Length > 0 && !schemaIds.Any(candidate => allowedSchemas.Contains(candidate, StringComparer.Ordinal)))
        return false;

    foreach (var candidate in request.RecordKeys ?? Array.Empty<string>())
    {
        var candidateSchemaId = "";
        var prefix = "";
        if (candidate.StartsWith("aetheria.object.selected.", StringComparison.Ordinal))
        {
            prefix = "aetheria.object.selected.";
            candidateSchemaId = AetheriaRuntimeDaemonSchemas.SelectedObject;
        }
        else if (candidate.StartsWith("aetheria.inventory.", StringComparison.Ordinal))
        {
            prefix = "aetheria.inventory.";
            candidateSchemaId = AetheriaRuntimeDaemonSchemas.Inventory;
        }
        else
        {
            continue;
        }

        if (schemaIds.Length > 0 && !schemaIds.Contains(candidateSchemaId, StringComparer.Ordinal))
            continue;
        if (!int.TryParse(candidate.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedEntityIndex))
            continue;

        recordKey = candidate;
        schemaId = candidateSchemaId;
        entityIndex = parsedEntityIndex;
        return true;
    }

    return false;
}

static bool TryParseViewportToken(string token, out double value)
{
    return double.TryParse(
        (token ?? "").Replace('n', '-').Replace('p', '.'),
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out value);
}

static bool TryGetDouble(
    IReadOnlyDictionary<string, string> values,
    string key,
    out double value)
{
    value = 0;
    return values.TryGetValue(key, out var text) &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

static async Task PublishDemandedManagedViewportsAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    AetheriaRuntimeDaemonFrameDocument frame,
    AetheriaManagedViewportDemandState demand)
{
    foreach (var request in demand.Snapshot())
    {
        if (!TryGetGameViewportRequest(
                new CultNetSnapshotRequestMessage
                {
                    RecordKeys = request.RecordKeys.ToArray(),
                    SchemaIds = request.SchemaIds.ToArray()
                },
                out var recordKey,
                out var schemaId,
                out var viewport))
        {
            continue;
        }

        CultNetDocumentPutRawMessage put;
        var messageId = $"aetheria-managed-viewport:{frame.FrameId}:{recordKey}";
        var messageOptions = new CultNetDocumentMessageOptions
        {
            SourceRuntimeId = options.DaemonId,
            SourceRole = "aetheria-daemon"
        };
        if (string.Equals(schemaId, AetheriaRuntimeDaemonSchemas.RenderSplatsViewport, StringComparison.Ordinal))
        {
            put = node.Database.Documents.CreateRawDocumentPutMessage(
                messageId,
                new CultRecordHandle<GameCult.Eve.PluginFields.EveFieldsSplatsDocument>(new CultRecordKey(recordKey)),
                AetheriaRuntimeGameDocuments.RenderSplatsViewport(frame, viewport),
                messageOptions);
        }
        else if (string.Equals(schemaId, AetheriaRuntimeDaemonSchemas.GravityViewport, StringComparison.Ordinal))
        {
            put = node.Database.Documents.CreateRawDocumentPutMessage(
                messageId,
                new CultRecordHandle<AetheriaRuntimeGravityViewportDocument>(new CultRecordKey(recordKey)),
                AetheriaRuntimeGameDocuments.GravityViewport(frame, viewport),
                messageOptions);
        }
        else if (string.Equals(schemaId, AetheriaRuntimeDaemonSchemas.ObjectsViewport, StringComparison.Ordinal))
        {
            put = node.Database.Documents.CreateRawDocumentPutMessage(
                messageId,
                new CultRecordHandle<AetheriaRuntimeObjectsViewportDocument>(new CultRecordKey(recordKey)),
                AetheriaRuntimeGameDocuments.ObjectsViewport(frame, viewport),
                messageOptions);
        }
        else
        {
            put = node.Database.Documents.CreateRawDocumentPutMessage(
                messageId,
                new CultRecordHandle<AetheriaRuntimeGameViewportDocument>(new CultRecordKey(recordKey)),
                AetheriaRuntimeGameDocuments.Viewport(frame, viewport),
                messageOptions);
        }

        await node.CommitAsync(async () =>
        {
            await node.Database.ApplyPutAsync(put).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}

static async Task PublishHotEntityStateAsync(
    AetheriaStateNode node,
    AetheriaRuntimeDaemonSoaFramePublisher publisher,
    AetheriaHotEntityPublicationState state,
    AetheriaRuntimeDaemonFrameDocument frame,
    AetheriaRuntimeCatalogSnapshot? catalog,
    AetheriaClientCultMeshHost clientHost)
{
    var hasRealtimeConsumers = clientHost.Realtime.ConnectionCount > 0;
    using var hotFrame = publisher.BuildCurrentZoneEntities(
        frame,
        catalog,
        realtimeDemand: hasRealtimeConsumers);
    if (hotFrame == null)
        return;

    var publication = await publisher.PublishAsync(
        hotFrame,
        includeRealtimePayload: hasRealtimeConsumers).ConfigureAwait(false);
    var layoutChanged = !state.Matches(publication.View);
    if (layoutChanged)
    {
        if (layoutChanged && string.Equals(Environment.GetEnvironmentVariable("AETHERIA_TRACE_CLIENT_TRANSPORT"), "1", StringComparison.Ordinal))
            Console.WriteLine(
                $"CultMesh entity layout changed sequence={publication.View.Sequence} bytes={MessagePackSerializer.Serialize(publication.View).Length} " +
                $"identities={publication.View.Identities.Length} representations=[{string.Join(",", publication.Body.Representations.Select(value => value.TransportKind))}]");
        await node.CommitAsync(async () =>
        {
            await node.MutableDocument<CultMeshBodyPublicationDocument>(
                    CultMeshBodyPublicationDocument.CreateLatestRecordKey(publication.Body.BodyId))
                .ReplaceAsync(publication.Body)
                .ConfigureAwait(false);
            await node.MutableDocument<EveEntitySoaViewDocument>(AetheriaRuntimeVerseRecordKeys.EveEntitySoaViewLatest)
                .ReplaceAsync(publication.View)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
        state.Set(publication.View);
    }
    if (!publication.RealtimePayload.IsEmpty)
    {
        await clientHost.Realtime.BroadcastAsync(new CultMeshRealtimeFrame
        {
            ChannelId = "aetheria.entities",
            SchemaId = AetheriaRuntimeDaemonSoaFramePublisher.BodySchemaId,
            BodyId = AetheriaRuntimeDaemonSoaFramePublisher.BodyId,
            ProducerEpoch = publication.Body.ProducerEpoch,
            Sequence = publication.Body.Sequence,
            Delivery = CultMeshRealtimeDelivery.LatestOnly,
            Payload = publication.RealtimePayload
        }).ConfigureAwait(false);
    }
}

static async Task PublishDurableDaemonDocumentsAsync(
    AetheriaStateNode node,
    AetheriaRuntimeDaemonTickResult result)
{
    await node.MutableDocument<AetheriaRuntimeDaemonFrameDocument>(AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest)
        .ReplaceAsync(result.Frame)
        .ConfigureAwait(false);
    await node.MutableDocument<AetheriaRuntimeZoneRenderDocument>(AetheriaRuntimeVerseRecordKeys.ZoneRenderLatest)
        .ReplaceAsync(AetheriaRuntimeGameDocuments.ZoneRender(result.Frame))
        .ConfigureAwait(false);
    if (result.Health != null)
        await node.MutableDocument<AetheriaRuntimeDaemonHealthDocument>(AetheriaRuntimeVerseRecordKeys.DaemonHealth)
            .ReplaceAsync(result.Health)
            .ConfigureAwait(false);
    if (result.CommandBoundary != null)
        await node.MutableDocument<AetheriaRuntimeDaemonCommandBoundaryDocument>(AetheriaRuntimeVerseRecordKeys.DaemonCommandBoundary)
            .ReplaceAsync(result.CommandBoundary)
            .ConfigureAwait(false);
    if (result.StarbridgeSessionSummary != null)
        await node.MutableDocument<AetheriaRuntimeStarbridgeSessionSummaryDocument>(AetheriaRuntimeVerseRecordKeys.StarbridgeSessionSummary)
            .ReplaceAsync(result.StarbridgeSessionSummary)
            .ConfigureAwait(false);
}

static async Task PublishClientGameplayDocumentsAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    AetheriaUnityBundleArtifactSet unityBundles,
    AetheriaRuntimeDaemonTickResult result,
    AetheriaRuntimeCatalogSnapshot inputCatalog,
    AetheriaReactiveSurfacePublicationState? reactiveSurfaceState,
    bool publishTopology)
{
    var trace = publishTopology && string.Equals(
        Environment.GetEnvironmentVariable("AETHERIA_TRACE_STARTUP_PHASES"),
        "1",
        StringComparison.Ordinal);
    var phase = Stopwatch.StartNew();
    void TraceClientDocumentPhase(string name)
    {
        if (trace)
            Console.WriteLine($"Aetheria client-bootstrap phase {name} took {phase.Elapsed.TotalMilliseconds:0.###}ms.");
        phase.Restart();
    }

    if (publishTopology && result.ProviderAdvertisement != null)
        await node.MutableDocument<AetheriaRuntimeDaemonProviderAdvertisementDocument>(AetheriaRuntimeVerseRecordKeys.DaemonProviderAdvertisement)
            .ReplaceAsync(result.ProviderAdvertisement)
            .ConfigureAwait(false);
    if (publishTopology)
        await node.MutableDocument<EveProviderAdvertisementDocument>(AetheriaRuntimeVerseRecordKeys.EveProviderAdvertisement)
            .ReplaceAsync(BuildCoreProviderAdvertisement(options, result.Frame.PublishedAtUtc))
            .ConfigureAwait(false);
    TraceClientDocumentPhase("provider-advertisements");
    if (publishTopology && result.AssetManifest != null)
    {
        await node.MutableDocument<AetheriaRuntimeAssetManifestDocument>(AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest)
            .ReplaceAsync(result.AssetManifest)
            .ConfigureAwait(false);
        await node.MutableDocument<EveAssetCatalogDocument>(AetheriaRuntimeVerseRecordKeys.EveAssetCatalog)
            .ReplaceAsync(BuildCoreAssetCatalog(unityBundles, result.AssetManifest))
            .ConfigureAwait(false);
    }
    TraceClientDocumentPhase("asset-catalog");
    var gameSession = await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
        .ReadAsync().ConfigureAwait(false);
    await node.MutableDocument<EveInputCapabilityDocument>(AetheriaRuntimeVerseRecordKeys.PilotInputCapability)
        .ReplaceAsync(AetheriaRuntimeInputCapabilityDocument.FromFrame(
            result.Frame,
            string.Equals(gameSession?.Mode, AetheriaGameSessionState.TerminusMode, StringComparison.Ordinal),
            inputCatalog).ToEveDocument())
        .ConfigureAwait(false);
    TraceClientDocumentPhase("input-capability");
    var mainMenuState = await node.MutableDocument<AetheriaMainMenuState>(AetheriaStateNode.MainMenuStateKey)
        .ReadAsync()
        .ConfigureAwait(false);
    var hasActivePilot = !string.IsNullOrWhiteSpace(result.Frame.Run?.CurrentEntityKey) &&
        result.Frame.Run.Zones?.Any(zone =>
            zone != null &&
            zone.ZoneIndex == result.Frame.Run.CurrentZoneIndex &&
            (zone.Entities?.Count ?? 0) > 0) == true;
    var activeMainMenuSurfaceId = string.IsNullOrWhiteSpace(mainMenuState?.ActiveSurfaceId)
        ? (hasActivePilot ? "" : AetheriaRuntimeMainMenuCommands.RootSurfaceId)
        : mainMenuState.ActiveSurfaceId;
    var reactiveGameSurface = AetheriaRuntimeDaemonGameSurfaceBuilder.BuildReactiveGameplay(
        result.Frame,
        reactiveSurfaceState?.LastPublishedFrame ?? -1);
    if (reactiveSurfaceState?.Matches(reactiveGameSurface.Version) != true)
    {
        await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.DaemonGameReactiveSurface)
            .ReplaceAsync(reactiveGameSurface)
            .ConfigureAwait(false);
        reactiveSurfaceState?.Set(reactiveGameSurface.Version);
    }
    TraceClientDocumentPhase("reactive-surface");
    if (publishTopology)
    {
        var gameSurface = AetheriaRuntimeDaemonGameSurfaceBuilder.Build(
            result.Frame,
            result.Health ?? new AetheriaRuntimeDaemonHealthDocument(),
            result.CommandBoundary ?? AetheriaRuntimeDaemonCommandBoundaryDocument.Create(options.DaemonId),
            activeMainMenuSurfaceId,
            inputCatalog);
        var portableGameSurface = gameSurface;
        if (string.Equals(Environment.GetEnvironmentVariable("AETHERIA_TRACE_CLIENT_TRANSPORT"), "1", StringComparison.Ordinal))
            Console.WriteLine($"Eve game topology bytes={MessagePackSerializer.Serialize(portableGameSurface).Length}");
        await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.DaemonGameSurface)
            .ReplaceAsync(portableGameSurface)
            .ConfigureAwait(false);
        await PublishDaemonSectorMapSurfaceAsync(node, result.Frame, inputCatalog).ConfigureAwait(false);
    }
    TraceClientDocumentPhase("game-topology");
}

static async Task PublishSecondaryTopologyDocumentsAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    AetheriaRuntimeDaemonTickResult result)
{
    var commanderSurface = AetheriaRuntimeDaemonGameSurfaceBuilder.BuildCommander(
        result.Frame,
        result.Health ?? new AetheriaRuntimeDaemonHealthDocument(),
        result.CommandBoundary ?? AetheriaRuntimeDaemonCommandBoundaryDocument.Create(options.DaemonId),
        result.StarbridgeSessionSummary);
    await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.StarbridgeCommanderSurface)
        .ReplaceAsync(commanderSurface)
        .ConfigureAwait(false);
    if (result.GameTuiSurface != null)
        await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.DaemonGameTuiSurface)
            .ReplaceAsync(result.GameTuiSurface)
            .ConfigureAwait(false);
    if (result.EditorSurface != null)
        await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.DaemonEditorSurface)
            .ReplaceAsync(result.EditorSurface)
            .ConfigureAwait(false);
    if (result.EditorTuiSurface != null)
        await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.DaemonEditorTuiSurface)
            .ReplaceAsync(result.EditorTuiSurface)
            .ConfigureAwait(false);

    await PublishDaemonMenuSurfacesAsync(node, options, result.Frame).ConfigureAwait(false);
}

static async Task PublishDaemonSectorMapSurfaceAsync(
    AetheriaStateNode node,
    AetheriaRuntimeDaemonFrameDocument frame,
    AetheriaRuntimeCatalogSnapshot catalog)
{
    if (frame == null)
        return;

    var updatedAtUtc = string.IsNullOrWhiteSpace(frame.PublishedAtUtc)
        ? DateTimeOffset.UtcNow.ToString("O")
        : frame.PublishedAtUtc;
    var surface = AetheriaRuntimeSectorMapSurfaceBuilder.Build(
        AetheriaRuntimeGameDocuments.SectorMap(frame),
        updatedAtUtc,
        frame.FrameId,
        catalog);
    await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.MapMenuSurface)
        .ReplaceAsync(surface)
        .ConfigureAwait(false);
}

static EveProviderAdvertisementDocument BuildCoreProviderAdvertisement(
    AetheriaDaemonHostOptions options,
    string updatedAtUtc)
{
    var interaction = new EveWorldInteractionAdvertisement(
        "provider-authored-world-surface",
        new[] { AetheriaRuntimeDaemonSchemas.Frame },
        "aetheria.daemon.commands",
        AetheriaRuntimeVerseRecordKeys.EveCommandRecordPrefix,
        EveCommandReceiptDocument.SchemaId,
        AetheriaRuntimeVerseRecordKeys.EveReceiptRecordPrefix,
        AetheriaRuntimeVerseRecordKeys.EveAssetCatalog.ToString(),
        new[] { "unity-scene", "web-reference", "electron-shell", "tui" },
        "provider-owns-world-state-command-acceptance-and-receipts");
    var mapInteraction = new EveWorldInteractionAdvertisement(
        "provider-authored-map-surface",
        new[] { AetheriaRuntimeDaemonSchemas.SectorMap },
        "",
        "",
        "",
        "",
        AetheriaRuntimeVerseRecordKeys.EveAssetCatalog.ToString(),
        new[] { "unity-scene", "web-reference", "electron-shell" },
        "provider-owns-topology-discovery-landmarks-influence-and-assets");
    return new EveProviderAdvertisementDocument(
        AetheriaRuntimeProviderIdentity.ProviderId,
        options.DaemonId,
        options.VerseId,
        "Aetheria Daemon",
        "game.daemon",
        options.CultMeshAddress,
        updatedAtUtc,
        new GameCult.Eve.Surface.EveProviderFreshness("fresh", updatedAtUtc, 5000),
        new[]
        {
            EveSurfaceDocument.SchemaId,
            EveSurfaceCommandRequest.SchemaId,
            EveCommandReceiptDocument.SchemaId,
            EveAssetCatalogDocument.SchemaId
        },
        Array.Empty<GameCult.Eve.Surface.EveProviderWitness>(),
        new[]
        {
            new EveAdvertisedSurface(
                AetheriaRuntimeDaemonGameSurfaceBuilder.PilotSurfaceId,
                EveSurfaceDocument.SchemaId,
                AetheriaRuntimeVerseRecordKeys.DaemonGameSurface.ToString(),
                "cultmesh-record",
                "active",
                "interactive-world",
                interaction),
            new EveAdvertisedSurface(
                AetheriaRuntimeDaemonGameSurfaceBuilder.CommanderSurfaceId,
                EveSurfaceDocument.SchemaId,
                AetheriaRuntimeVerseRecordKeys.StarbridgeCommanderSurface.ToString(),
                "cultmesh-record",
                "active",
                "interactive-world",
                interaction),
            new EveAdvertisedSurface(
                AetheriaRuntimeSectorMapSurfaceBuilder.SurfaceId,
                EveSurfaceDocument.SchemaId,
                AetheriaRuntimeVerseRecordKeys.MapMenuSurface.ToString(),
                "cultmesh-record",
                "active",
                "graph",
                mapInteraction),
            new EveAdvertisedSurface(
                AetheriaRuntimeHangarCommands.SurfaceId,
                EveSurfaceDocument.SchemaId,
                AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(),
                "cultmesh-record",
                "active",
                "interactive-world",
                interaction)
        },
        Array.Empty<EveAdvertisedCommand>(),
        new[] { AetheriaRuntimeDaemonSoaFramePublisher.ProducerId });
}

static async Task PublishDaemonMenuSurfacesAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    AetheriaRuntimeDaemonFrameDocument frame)
{
    if (frame == null)
        return;

    var updatedAtUtc = string.IsNullOrWhiteSpace(frame.PublishedAtUtc)
        ? DateTimeOffset.UtcNow.ToString("O")
        : frame.PublishedAtUtc;
    var catalog = node.RuntimeCatalog().Latest();
    var assetManifest = AetheriaRuntimeAssets.ProjectManifest(
        catalog,
        frame.Run?.RunId ?? frame.SessionId,
        "cultmesh://aetheria/assets");
    var loadoutTemplates = node.Cache.GetAll<AetheriaLoadoutTemplate>()
        .Select(ToLoadoutTemplateSnapshot)
        .ToArray();
    var playerSettings = await ReadRuntimePlayerSettingsAsync(node).ConfigureAwait(false);
    var currentEntity = AetheriaRuntimeGameDocuments.CurrentEntity(frame);
    var stationRefit = AetheriaRuntimeGameDocuments.StationRefit(frame, loadoutTemplates, catalog);
    var dropdownRequest = new AetheriaRuntimeInventoryDropdownSurfaceRequest
    {
        CurrentView = "Current Entity",
        DisplayedEntityKey = currentEntity.EntityKey,
        DisplayedCargoEntityKey = currentEntity.EntityKey,
        DisplayedCargoIndex = 0,
        CanSaveLoadout = !string.IsNullOrWhiteSpace(currentEntity.EntityKey)
    };
    var inventoryRequest = new AetheriaRuntimeInventoryPanelSurfaceRequest
    {
        ViewTitle = string.IsNullOrWhiteSpace(currentEntity.Entity?.DisplayName)
            ? "Current Entity Inventory"
            : currentEntity.Entity.DisplayName,
        DisplayedEntityKey = currentEntity.EntityKey,
        DisplayedEntityIndex = currentEntity.EntityIndex,
        DisplayedCargoEntityKey = currentEntity.EntityKey,
        DisplayedCargoEntityIndex = currentEntity.EntityIndex,
        DisplayedCargoIndex = 0
    };
    var inventory = currentEntity.EntityIndex < 0
        ? new AetheriaRuntimeInventoryDocument()
        : AetheriaRuntimeGameDocuments.Inventory(frame, currentEntity.EntityIndex);

    var mainMenu = AetheriaRuntimeMainMenuSurfaceBuilder.BuildRoot(
        inGame: true,
        updatedAtUtc);
    var mainMenuSettings = AetheriaRuntimeMainMenuSurfaceBuilder.BuildSettings(updatedAtUtc);
    var mainMenuInputSettings = AetheriaRuntimeMainMenuSurfaceBuilder.BuildInputSettings(
        playerSettings.BindingOverrides?.Length ?? 0,
        playerSettings.ActionBarInputs?.Length ?? 0,
        canOpenRuntimeInputScreen: true,
        inGame: true,
        updatedAtUtc);
    var mainMenuPlayerSettings = AetheriaRuntimeMainMenuSurfaceBuilder.BuildPlayerSettings(
        playerSettings,
        updatedAtUtc);
    var inventoryPanel = AetheriaRuntimeInventoryPanelSurfaceBuilder.BuildFromDocuments(
        currentEntity,
        stationRefit,
        inventory,
        catalog,
        playerSettings,
        inventoryRequest,
        AetheriaRuntimeVerseRecordKeys.InventoryDropdownSurface.ToString(),
        updatedAtUtc);
    var inventoryDropdown = AetheriaRuntimeInventoryDropdownSurfaceBuilder.BuildFromDocuments(
        stationRefit,
        dropdownRequest,
        updatedAtUtc);
    var tradeMenu = BuildTradeMenuSurface(stationRefit, catalog, updatedAtUtc, frame.FrameId);

    await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.MainMenuSurface)
        .ReplaceAsync(mainMenu)
        .ConfigureAwait(false);
    await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.MainMenuSettingsSurface)
        .ReplaceAsync(mainMenuSettings)
        .ConfigureAwait(false);
    await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.MainMenuInputSettingsSurface)
        .ReplaceAsync(mainMenuInputSettings)
        .ConfigureAwait(false);
    await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.MainMenuPlayerSettingsSurface)
        .ReplaceAsync(mainMenuPlayerSettings)
        .ConfigureAwait(false);
    await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.InventoryPanelSurface)
        .ReplaceAsync(inventoryPanel)
        .ConfigureAwait(false);
    await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.InventoryDropdownSurface)
        .ReplaceAsync(inventoryDropdown)
        .ConfigureAwait(false);
    await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.TradeMenuSurface)
        .ReplaceAsync(tradeMenu)
        .ConfigureAwait(false);
    await node.MutableDocument<AetheriaRuntimeAssetManifestDocument>(AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest)
        .ReplaceAsync(assetManifest)
        .ConfigureAwait(false);
}

static async Task<AetheriaRuntimePlayerSettingsDocument> ReadRuntimePlayerSettingsAsync(AetheriaStateNode node)
{
    var settings = await node.MutableDocument<AetheriaPlayerSettings>(AetheriaStateNode.PlayerSettingsKey)
        .ReadAsync()
        .ConfigureAwait(false);
    settings ??= new AetheriaPlayerSettings();
    return new AetheriaRuntimePlayerSettingsDocument
    {
        PlayerName = settings.PlayerName ?? "",
        TutorialPassed = settings.TutorialPassed,
        TemperatureUnit = settings.Gameplay?.TemperatureUnit ?? "",
        SignificantDigits = settings.Gameplay?.SignificantDigits ?? 3,
        DefaultShutdownPerformance = settings.Gameplay?.DefaultShutdownPerformance ?? 0,
        NebulaQuality = settings.Graphics?.NebulaQuality ?? "",
        ShowAsteroidsInMinimap = settings.Graphics?.ShowAsteroidsInMinimap ?? false,
        BindingOverrides = Array.Empty<AetheriaRuntimeInputBindingOverrideDocument>(),
        ActionBarInputs = settings.Input?.ActionBarInputs ?? Array.Empty<string>()
    };
}

static EveSurfaceDocument BuildTradeMenuSurface(
    AetheriaRuntimeStationRefitDocument stationRefit,
    AetheriaRuntimeCatalogSnapshot catalog,
    string updatedAtUtc,
    long version)
{
    stationRefit ??= new AetheriaRuntimeStationRefitDocument();
    var rows = (stationRefit.StationStock ?? Array.Empty<AetheriaRuntimeStationStockItem>())
        .Take(12)
        .Select((item, index) =>
        {
            var typedItem = catalog?.FindItem(item.ItemKey);
            return SurfaceLeaf(
                $"aetheria.trade.menu.stock.{index}",
                "row",
                ("item", typedItem?.Name ?? item.ItemKey),
                ("key", item.ItemKey ?? ""),
                ("qty", item.Quantity.ToString(CultureInfo.InvariantCulture)),
                ("price", item.Price.ToString("N0", CultureInfo.InvariantCulture)),
                ("owned", item.OwnedQuantity.ToString(CultureInfo.InvariantCulture)),
                ("afford", item.CanAfford ? "yes" : "no"));
        })
        .DefaultIfEmpty(SurfaceLeaf(
            "aetheria.trade.menu.stock.empty",
            "text",
            ("value", "No station stock is available in the current daemon frame.")))
        .ToArray();

    return new EveSurfaceDocument(
        providerId: AetheriaRuntimeProviderIdentity.ProviderId,
        providerKind: "trade.menu",
        title: "Trade Menu",
        version: version,
        updatedAtUtc: updatedAtUtc ?? "",
        surface: new EveSurfaceTree(
            "aetheria.trade.menu",
            SurfaceNode(
                "aetheria.trade.menu.root",
                "surface",
                Array.Empty<(string Key, string Value)>(),
                SurfaceNode(
                    "aetheria.trade.menu.summary",
                    "card",
                    new[] { ("title", "Station Trade") },
                    SurfaceLeaf("aetheria.trade.menu.summary.docked", "metric", ("label", "Docked"), ("value", stationRefit.IsDocked ? "yes" : "no")),
                    SurfaceLeaf("aetheria.trade.menu.summary.station", "metric", ("label", "Station"), ("value", stationRefit.DockParent?.DisplayName ?? stationRefit.DockParentEntityKey ?? "")),
                    SurfaceLeaf("aetheria.trade.menu.summary.credits", "metric", ("label", "Credits"), ("value", stationRefit.Credits.ToString("N0", CultureInfo.InvariantCulture)))),
                SurfaceNode(
                    "aetheria.trade.menu.stock",
                    "card",
                    new[] { ("title", "Station Stock") },
                    rows)),
            Array.Empty<EveStyleToken>()),
        commands: Array.Empty<EveCommandTemplate>());
}

static EveSurfaceComponent SurfaceLeaf(
    string id,
    string kind,
    params (string Key, string Value)[] props)
{
    return SurfaceNode(id, kind, props, Array.Empty<EveSurfaceComponent>());
}

static EveSurfaceComponent SurfaceNode(
    string id,
    string kind,
    (string Key, string Value)[] props,
    params EveSurfaceComponent[] children)
{
    return new EveSurfaceComponent(
        id,
        kind,
        props.ToDictionary(prop => prop.Key, prop => prop.Value),
        children);
}

static async Task<bool> AcceptCoreEveInvocationsAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    AetheriaRuntimeDaemonFrameDocument? currentFrame,
    ConcurrentDictionary<string, Task> progressionForwardingTasks,
    CancellationToken progressionForwardingCancellation)
{
    var activatedSession = false;
    var pendingRequests = node.Cache.GetStoredDocuments<EveSurfaceCommandRequest>()
        .Where(stored => !string.IsNullOrWhiteSpace(((EveSurfaceCommandRequest)stored.Document).CommandId))
        .Select(stored => (stored.Key, Request: (EveSurfaceCommandRequest)stored.Document))
        .OrderBy(stored => stored.Request.IssuedAt)
        .ToArray();
    foreach (var storedRequest in pendingRequests)
    {
        if (ShouldForwardHangarRequest(node, storedRequest.Request))
        {
            StartProgressionForwarding(
                node,
                options,
                storedRequest.Key,
                storedRequest.Request,
                progressionForwardingTasks,
                progressionForwardingCancellation);
            continue;
        }

        try
        {
            var requestActivatedSession = await node.CommitAsync(
                () => AcceptCoreEveInvocationAsync(
                    node,
                    options,
                    currentFrame,
                    storedRequest.Key,
                    storedRequest.Request)).ConfigureAwait(false);
            activatedSession |= requestActivatedSession;
            if (string.Equals(storedRequest.Request.SurfaceId, AetheriaRuntimeHangarCommands.SurfaceId, StringComparison.Ordinal))
                StartHangarProjectionRefresh(
                    node,
                    options,
                    storedRequest.Request.CommandId,
                    progressionForwardingTasks,
                    progressionForwardingCancellation);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // This request's transaction has already rolled back. Keep only this
            // request pending; unrelated commands retain independent finality.
            Console.Error.WriteLine(
                $"Aetheria Eve command '{storedRequest.Request.CommandId}' rolled back and remains pending: {error}");
        }
    }
    return activatedSession;
}

static bool ShouldForwardHangarRequest(
    AetheriaStateNode node,
    EveSurfaceCommandRequest request)
{
    if (!string.Equals(request.SurfaceId, AetheriaRuntimeHangarCommands.SurfaceId, StringComparison.Ordinal) ||
        string.Equals(request.Command, AetheriaRuntimeHangarCommands.SelectVerse, StringComparison.Ordinal))
        return false;
    if (node.Cache.Get<AetheriaProgressionCommandRouteDocument>(
            AetheriaRuntimeVerseRecordKeys.ProgressionCommandRoute(request.CommandId)) != null)
        return true;
    var source = node.Cache.Get<AetheriaProgressionSourceDocument>(AetheriaStateNode.ProgressionSourceKey);
    return source != null && !source.UsesLocalProgression;
}

static void StartProgressionForwarding(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    CultRecordKey requestRecordKey,
    EveSurfaceCommandRequest request,
    ConcurrentDictionary<string, Task> tasks,
    CancellationToken cancellationToken)
{
    var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    if (!tasks.TryAdd(request.CommandId, completion.Task))
        return;
    _ = RunAsync();

    async Task RunAsync()
    {
        try
        {
            await ForwardProgressionCommandAsync(
                node,
                options,
                requestRecordKey,
                request,
                cancellationToken).ConfigureAwait(false);
            completion.TrySetResult(null);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Task>>)tasks).Remove(
                new KeyValuePair<string, Task>(request.CommandId, completion.Task));
        }
    }
}

static void StartHangarProjectionRefresh(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    string commandId,
    ConcurrentDictionary<string, Task> tasks,
    CancellationToken cancellationToken)
{
    var taskKey = "hangar-projection:" + commandId;
    var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    if (!tasks.TryAdd(taskKey, completion.Task))
        return;
    _ = RunAsync();

    async Task RunAsync()
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PublishStateSurfacesAsync(
                node,
                options,
                DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);
            completion.TrySetResult(null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"Aetheria Hangar projection refresh failed after command '{commandId}' committed: {error}");
            completion.TrySetResult(null);
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Task>>)tasks).Remove(
                new KeyValuePair<string, Task>(taskKey, completion.Task));
        }
    }
}

static async Task ForwardProgressionCommandAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    CultRecordKey requestRecordKey,
    EveSurfaceCommandRequest request,
    CancellationToken cancellationToken)
{
    try
    {
        var payloadHash = await node.CommitAsync(() => AetheriaHangarCommandJournal.ValidateAsync(
            node,
            request,
            DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);
        using var progressionVerses = CreateProgressionVerseCoordinator(node, options);
        var route = await progressionVerses.ResolveOrPinForwardingRouteAsync(
            request,
            payloadHash,
            DateTimeOffset.UtcNow.ToString("O"),
            cancellationToken).ConfigureAwait(false);
        var remoteReceipt = await progressionVerses.ForwardHangarInvocationAsync(
            request,
            route,
            cancellationToken).ConfigureAwait(false);

        await node.CommitAsync(async () =>
        {
            var pending = await node.MutableDocument<EveSurfaceCommandRequest>(requestRecordKey)
                .ReadAsync().ConfigureAwait(false);
            if (pending == null)
                return;
            if (!string.Equals(AetheriaHangarCommandJournal.PayloadHash(pending), payloadHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Hangar command id '{request.CommandId}' changed after its progression route was pinned.");
            var pinnedRoute = await node.MutableDocument<AetheriaProgressionCommandRouteDocument>(
                    AetheriaRuntimeVerseRecordKeys.ProgressionCommandRoute(request.CommandId))
                .ReadAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Progression forwarding lost its durable pinned route.");
            if (!string.Equals(pinnedRoute.PayloadHash, payloadHash, StringComparison.Ordinal) ||
                !string.Equals(pinnedRoute.VerseId, route.VerseId, StringComparison.Ordinal) ||
                !string.Equals(pinnedRoute.AuthorityRuntimeId, route.AuthorityRuntimeId, StringComparison.Ordinal))
                throw new InvalidOperationException("Progression forwarding route changed before receipt finality.");
            await node.Database.PutAsync(
                AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(request.CommandId),
                remoteReceipt).ConfigureAwait(false);
            await node.Database.DeleteAsync<EveSurfaceCommandRequest>(requestRecordKey).ConfigureAwait(false);
        }).ConfigureAwait(false);

        try
        {
            await PublishStateSurfacesAsync(
                node,
                options,
                DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"Aetheria Hangar projection refresh failed after forwarded command '{request.CommandId}' committed: {error}");
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (TimeoutException error)
    {
        Console.Error.WriteLine(
            $"Hangar command '{request.CommandId}' has no final remote receipt yet and remains pending: {error.Message}");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(
            $"Forwarded Hangar command '{request.CommandId}' failed and remains pending: {error}");
    }
}

static async Task<bool> AcceptCoreEveInvocationAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    AetheriaRuntimeDaemonFrameDocument? currentFrame,
    CultRecordKey requestRecordKey,
    EveSurfaceCommandRequest request)
{
        string payloadHash;
        try
        {
            payloadHash = await AetheriaHangarCommandJournal.ValidateAsync(
                node,
                request,
                DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine($"Rejected Hangar command envelope '{request.CommandId}': {error.Message}");
            await node.Database.DeleteAsync<EveSurfaceCommandRequest>(requestRecordKey).ConfigureAwait(false);
            return false;
        }
        var alreadyReceipted = node.Cache.Get<EveCommandReceiptDocument>(
            AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(request.CommandId)) != null;
        var alreadySubmitted = node.Cache.Get<AetheriaRuntimeDaemonCommandDocument>(
            AetheriaRuntimeVerseRecordKeys.DaemonCommand(request.CommandId)) != null;
        if (alreadyReceipted || alreadySubmitted)
        {
            await node.Database.DeleteAsync<EveSurfaceCommandRequest>(requestRecordKey).ConfigureAwait(false);
            return false;
        }

        if (string.Equals(request.SurfaceId, AetheriaRuntimeHangarCommands.SurfaceId, StringComparison.Ordinal))
        {
            try
            {
                var activatedSession = await AcceptHangarInvocationAsync(node, options, request).ConfigureAwait(false);
                await node.Database.DeleteAsync<EveSurfaceCommandRequest>(requestRecordKey).ConfigureAwait(false);
                return activatedSession;
            }
            catch (TimeoutException error)
            {
                Console.Error.WriteLine(
                    $"Hangar command '{request.CommandId}' has no final remote receipt yet and remains pending: {error.Message}");
                return false;
            }
        }

        if (AetheriaRuntimeDaemonOperationsClient.TryCreateSurfaceCommandDocument(
                request,
                currentFrame,
                request.ClientId,
                currentFrame?.SessionId ?? options.SessionId,
                out var command) && command != null)
        {
            command.CommandId = request.CommandId;
            command.ClientId = request.ClientId;
            command.AuthorRuntimeId = request.ClientId;
            await node.SubmitDaemonCommandAsync(command).ConfigureAwait(false);
            await node.Database.DeleteAsync<EveSurfaceCommandRequest>(requestRecordKey).ConfigureAwait(false);
            return false;
        }

        var denied = new EveCommandReceiptDocument(
            $"receipt:{request.CommandId}:denied",
            request.CommandId,
            request.Command,
            "denied",
            "Aetheria",
            options.DaemonId,
            request.ProviderId,
            request.SurfaceId,
            "Command is not advertised by the Aetheria daemon surface.",
            DateTimeOffset.UtcNow.ToString("O"),
            Math.Max(currentFrame?.FrameId ?? 0, 0));
        await node.Database.PutAsync(AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(denied.CommandId), denied)
            .ConfigureAwait(false);
        await node.Database.DeleteAsync<EveSurfaceCommandRequest>(requestRecordKey).ConfigureAwait(false);
        return false;
}

static Task<bool> AcceptHangarInvocationAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    EveSurfaceCommandRequest request)
    => node.CommitAsync(() => AcceptHangarInvocationCoreAsync(node, options, request));

static async Task<bool> AcceptHangarInvocationCoreAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    EveSurfaceCommandRequest request)
{
    var now = DateTimeOffset.UtcNow.ToString("O");
    var command = request.Command ?? "";
    var accepted = false;
    var diagnostic = "";
    var activatesSession = false;
    EveSurfaceNavigationTarget? navigation = null;
    if (string.Equals(
            Environment.GetEnvironmentVariable("AETHERIA_DEV_INJECT_HANGAR_FAILURE_COMMAND_ID"),
            request.CommandId,
            StringComparison.Ordinal))
        throw new InvalidOperationException("Injected Hangar command finality failure.");
    using var progressionVerses = CreateProgressionVerseCoordinator(node, options);
    var pinnedRoute = await node.MutableDocument<AetheriaProgressionCommandRouteDocument>(
            AetheriaRuntimeVerseRecordKeys.ProgressionCommandRoute(request.CommandId))
        .ReadAsync().ConfigureAwait(false);
    var progressionSource = await node.MutableDocument<AetheriaProgressionSourceDocument>(AetheriaStateNode.ProgressionSourceKey)
        .ReadAsync().ConfigureAwait(false)
        ?? await progressionVerses.EnsureAndRefreshAsync(now).ConfigureAwait(false);
    if (string.Equals(command, AetheriaRuntimeHangarCommands.SelectVerse, StringComparison.Ordinal))
    {
        await progressionVerses.SelectAsync(Payload(request, "value"), now).ConfigureAwait(false);
        accepted = true;
    }
    else if (pinnedRoute != null || !progressionSource.UsesLocalProgression)
    {
        throw new InvalidOperationException(
            "Remote Hangar commands must enter through the progression forwarding worker, outside the state transaction.");
    }

    var shipId = Payload(request, "shipId");
    var expectedRevision = PayloadLong(request, "expectedHangarRevision", -1);
    if (!accepted)
    {
        switch (command)
        {
            case AetheriaRuntimeHangarCommands.SelectShip:
                await AetheriaDaemonHangarCoordinator.SelectShipAsync(node, shipId, now).ConfigureAwait(false);
                accepted = true;
                break;
            case AetheriaRuntimeHangarCommands.SelectTerminus:
                await AetheriaDaemonHangarCoordinator.SelectModeAsync(node, AetheriaGameModes.Terminus, now).ConfigureAwait(false);
                accepted = true;
                break;
            case AetheriaRuntimeHangarCommands.SelectStarbridge:
                await AetheriaDaemonHangarCoordinator.SelectModeAsync(node, AetheriaGameModes.Starbridge, now).ConfigureAwait(false);
                accepted = true;
                break;
            case AetheriaRuntimeHangarCommands.SelectArena:
                await AetheriaDaemonHangarCoordinator.SelectModeAsync(node, AetheriaGameModes.Arena, now).ConfigureAwait(false);
                accepted = true;
                break;
            case AetheriaRuntimeHangarCommands.EditLoadout:
                await AetheriaDaemonHangarCoordinator.SelectViewAsync(node, AetheriaHangarViews.Loadout, now).ConfigureAwait(false);
                accepted = true;
                navigation = new EveSurfaceNavigationTarget(
                    options.VerseId,
                    request.ProviderId,
                    AetheriaRuntimeHangarCommands.SurfaceId,
                    "interactive-world");
                break;
            case AetheriaRuntimeHangarCommands.ShowOverview:
                await AetheriaDaemonHangarCoordinator.SelectViewAsync(node, AetheriaHangarViews.Overview, now).ConfigureAwait(false);
                accepted = true;
                navigation = new EveSurfaceNavigationTarget(
                    options.VerseId,
                    request.ProviderId,
                    AetheriaRuntimeHangarCommands.SurfaceId,
                    "interactive-world");
                break;
            case AetheriaRuntimeHangarCommands.EquipItem:
            {
                var rotation = Payload(request, "destinationRotation");
                if (string.IsNullOrWhiteSpace(rotation))
                    rotation = Payload(request, "sourceRotation");
                var result = await AetheriaHangar.EquipAsync(
                    node,
                    shipId,
                    Payload(request, "itemKey"),
                    expectedRevision,
                    node.RuntimeCatalog().Latest(),
                    now,
                    (int)PayloadLong(request, "destinationX", 0),
                    (int)PayloadLong(request, "destinationY", 0),
                    string.Equals(Payload(request, "hasDestinationPosition"), "true", StringComparison.OrdinalIgnoreCase),
                    string.IsNullOrWhiteSpace(rotation) ? "None" : rotation).ConfigureAwait(false);
                accepted = result.Accepted;
                diagnostic = result.Diagnostic;
                break;
            }
            case AetheriaRuntimeHangarCommands.RemoveItem:
            {
                var result = await AetheriaHangar.RemoveAsync(
                    node,
                    shipId,
                    (int)PayloadLong(request, "equipmentIndex", PayloadLong(request, "originIndex", -1)),
                    expectedRevision,
                    now).ConfigureAwait(false);
                accepted = result.Accepted;
                diagnostic = result.Diagnostic;
                break;
            }
            case AetheriaRuntimeHangarCommands.Launch:
            {
                var receipt = await AetheriaDaemonHangarCoordinator.LaunchAsync(
                    node,
                    await node.RuntimeCatalogForGenerationAsync().ConfigureAwait(false),
                    request.CommandId,
                    options.SessionId,
                    expectedRevision,
                    now).ConfigureAwait(false);
                accepted = receipt.Accepted;
                diagnostic = receipt.Diagnostic;
                if (accepted)
                {
                    activatesSession = true;
                    navigation = new EveSurfaceNavigationTarget(
                        options.VerseId,
                        request.ProviderId,
                        SurfaceForMode(receipt.Mode),
                        "interactive-world");
                }
                break;
            }
            case AetheriaRuntimeHangarCommands.Continue:
            {
                var deployment = await AetheriaDaemonHangarCoordinator.ContinueAsync(
                    node,
                    Payload(request, "deploymentId")).ConfigureAwait(false);
                accepted = deployment != null;
                diagnostic = accepted ? "" : "No resumable deployment exists for the selected ship and mode.";
                if (deployment != null)
                {
                    await ActivateSessionAsync(node, options, deployment.Mode, deployment.RunRecordKey, request.CommandId, now).ConfigureAwait(false);
                    activatesSession = true;
                    navigation = new EveSurfaceNavigationTarget(
                        options.VerseId,
                        request.ProviderId,
                        SurfaceForMode(deployment.Mode),
                        "interactive-world");
                }
                break;
            }
            default:
                diagnostic = "Command is not advertised by the Hangar surface.";
                break;
        }
    }

    var receiptDocument = new EveCommandReceiptDocument(
        $"receipt:{request.CommandId}:{(accepted ? "accepted" : "denied")}",
        request.CommandId,
        command,
        accepted ? "accepted" : "denied",
        "Aetheria Hangar",
        options.DaemonId,
        request.ProviderId,
        request.SurfaceId,
        diagnostic,
        now,
        0,
        navigation);
    await node.Database.PutAsync(AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(request.CommandId), receiptDocument)
        .ConfigureAwait(false);
    return activatesSession;
}

static async Task ActivateSessionAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    string mode,
    string runRecordKey,
    string commandId,
    string now)
{
    var run = await ReadRuntimeRunCheckpointAsync(node, options.RenderSettings, runRecordKey).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Mode activation requires a canonical run checkpoint.");
    await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
        .ReplaceAsync(new AetheriaGameSessionState
        {
            Mode = mode,
            SessionId = options.SessionId,
            RunId = run.RunId,
            RunRecordKey = runRecordKey,
            ControlledEntityKey = run.CurrentEntityKey,
            EntrySurfaceId = AetheriaRuntimeHangarCommands.SurfaceId,
            SimulationRate = 1,
            EffectiveSimulationRate = 1,
            LastStartCommandId = commandId,
            UpdatedAtUtc = now
        }).ConfigureAwait(false);

    if (string.Equals(mode, AetheriaGameModes.Starbridge, StringComparison.Ordinal))
    {
        await node.MutableDocument<AetheriaRuntimeStarbridgeSessionDocument>(AetheriaRuntimeVerseRecordKeys.StarbridgeSessionLatest)
            .ReplaceAsync(new AetheriaRuntimeStarbridgeSessionDocument
            {
                SessionId = options.SessionId,
                ScenarioId = "hangar-deployment",
                RunId = run.RunId,
                BaseEntityKey = run.CurrentEntityKey,
                StationEntityKey = run.CurrentEntityKey,
                Phase = "active"
            }).ConfigureAwait(false);
    }
}

static string SurfaceForMode(string mode) =>
    string.Equals(mode, AetheriaGameModes.Starbridge, StringComparison.Ordinal)
        ? AetheriaRuntimeDaemonGameSurfaceBuilder.CommanderSurfaceId
        : AetheriaRuntimeDaemonGameSurfaceBuilder.PilotSurfaceId;

static string Payload(EveSurfaceCommandRequest request, string key) =>
    request.PayloadFields.TryGetValue(key, out var value) ? value ?? "" : "";

static long PayloadLong(EveSurfaceCommandRequest request, string key, long fallback) =>
    long.TryParse(Payload(request, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value
        : fallback;

static async Task AcceptEveCommandsAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options)
{
    var commandCountBefore = node.Documents<AetheriaRuntimeEveCommandDocument>().Count;
    if (commandCountBefore == 0)
        return;
    var now = DateTimeOffset.UtcNow.ToString("O");
    try
    {
        var report = await AetheriaEveCommandBridge.AcceptObservedAsync(node).ConfigureAwait(false);
        await node.MutableDocument<AetheriaEveCommandAcceptanceStatus>(AetheriaStateNode.EveCommandAcceptanceStatusKey)
            .ReplaceAsync(new AetheriaEveCommandAcceptanceStatus
            {
                RuntimeId = options.DaemonId,
                StatePath = node.StatePath,
                LastPollAtUtc = now,
                LastAcceptedAtUtc = report.AcceptedCommandIds.Length > 0 ? now : "",
                ObservedBeforeAccept = commandCountBefore,
                CommandsAccepted = report.AcceptedCommandIds.Length,
                CommandsRejected = report.RejectedCommands,
                AppliedCatalogRefreshes = report.AcceptedCatalogRefreshes,
                AppliedOperationsRefreshes = report.AcceptedOperationsRefreshes,
                AppliedPlayerSettingsCommands = report.AcceptedPlayerSettingsCommands,
                AppliedInputSettingsCommands = report.AcceptedInputSettingsCommands,
                AppliedLoadoutTemplateCommands = report.AcceptedLoadoutTemplateCommands,
                AppliedVerseHostCommands = report.AcceptedVerseHostCommands,
                AppliedMainMenuCommands = report.AcceptedMainMenuCommands,
                AccountedCommandIds = report.AccountedCommandIds,
                LastRejectedCommand = report.LastRejectedCommand,
                LastRejectedReason = report.LastRejectedReason,
                ConsecutiveFailures = 0,
                Status = report.RejectedCommands > 0 ? "rejected" : "ok"
            })
            .ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        var existing = await node.MutableDocument<AetheriaEveCommandAcceptanceStatus>(AetheriaStateNode.EveCommandAcceptanceStatusKey).ReadAsync().ConfigureAwait(false);
        await node.MutableDocument<AetheriaEveCommandAcceptanceStatus>(AetheriaStateNode.EveCommandAcceptanceStatusKey)
            .ReplaceAsync(new AetheriaEveCommandAcceptanceStatus
            {
                RuntimeId = options.DaemonId,
                StatePath = node.StatePath,
                LastPollAtUtc = now,
                LastAcceptedAtUtc = existing?.LastAcceptedAtUtc ?? "",
                ObservedBeforeAccept = commandCountBefore,
                AccountedCommandIds = [],
                ConsecutiveFailures = (existing?.ConsecutiveFailures ?? 0) + 1,
                LastError = ex.ToString(),
                Status = "error"
            })
            .ConfigureAwait(false);
        throw;
    }
}

static async Task PublishStateSurfacesAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    string updatedAtUtc,
    bool publishHangar = true)
{
    AetheriaProgressionVerseView? hangarView = null;
    if (publishHangar)
    {
        using var progressionVerses = CreateProgressionVerseCoordinator(node, options);
        hangarView = await progressionVerses.ReadViewAsync(updatedAtUtc).ConfigureAwait(false);
    }
    await node.CommitAsync(
        () => PublishStateSurfacesCoreAsync(node, options, updatedAtUtc, hangarView)).ConfigureAwait(false);
}

static async Task PublishStateSurfacesCoreAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    string updatedAtUtc,
    AetheriaProgressionVerseView? hangarView)
{
    var verseHost = await EnsureVerseHostSettingsAsync(node, options, updatedAtUtc).ConfigureAwait(false);
    var eveStatus = await node.MutableDocument<AetheriaEveCommandAcceptanceStatus>(AetheriaStateNode.EveCommandAcceptanceStatusKey).ReadAsync().ConfigureAwait(false);
    var runtimeSession = await node.MutableDocument<AetheriaRuntimeSession>(AetheriaStateNode.RuntimeSessionKey(options.DaemonId)).ReadAsync().ConfigureAwait(false);
    var playerSettings = await node.MutableDocument<AetheriaPlayerSettings>(AetheriaStateNode.PlayerSettingsKey).ReadAsync().ConfigureAwait(false) ?? new AetheriaPlayerSettings();
    var playerSettingsUpdatedAt = string.IsNullOrWhiteSpace(playerSettings.LastUpdatedAtUtc)
        ? updatedAtUtc
        : playerSettings.LastUpdatedAtUtc;
    await node.MutableDocument<EveSurfaceDocument>(AetheriaStateNode.OperationsSurfaceKey)
        .ReplaceAsync(AetheriaEveSurfaceDocuments.BuildOperationsSurface(eveStatus, verseHost, runtimeSession))
        .ConfigureAwait(false);
    await node.MutableDocument<EveSurfaceDocument>(AetheriaStateNode.PlayerSettingsSurfaceKey)
        .ReplaceAsync(AetheriaEveSurfaceDocuments.BuildPlayerSettingsSurface(playerSettings, playerSettingsUpdatedAt))
        .ConfigureAwait(false);
    await node.MutableDocument<EveProviderAdvertisementDocument>(AetheriaStateNode.ProviderAdvertisementSurfaceKey)
        .ReplaceAsync(AetheriaEveSurfaceDocuments.BuildProviderAdvertisement(verseHost, node.StatePath, updatedAtUtc))
        .ConfigureAwait(false);
    if (hangarView != null)
    {
        await node.MutableDocument<EveSurfaceDocument>(AetheriaRuntimeVerseRecordKeys.HangarSurface)
            .ReplaceAsync(AetheriaRuntimeHangarSurfaceBuilder.Build(
                hangarView.Hangar,
                hangarView.Draft.SelectedShipId,
                hangarView.Draft.SelectedMode,
                updatedAtUtc,
                Math.Max(1, hangarView.Hangar.Revision + hangarView.Draft.Revision + hangarView.Source.Revision),
                hangarView.Loadout == null ? null : AetheriaRuntimeStateMapper.ToRuntimeLoadoutTemplate(hangarView.Loadout),
                hangarView.Catalog,
                hangarView.Source,
                hangarView.Draft.ActiveView))
            .ConfigureAwait(false);
    }
}

static AetheriaProgressionVerseCoordinator CreateProgressionVerseCoordinator(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options) =>
    new(node, options.DaemonId, options.VerseId, options.OdinDiscoveryEndpoints, options.ProgressionTrust);

static async Task PublishOdinSurfaceAnnouncementsAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    string updatedAtUtc)
{
    if (!options.EnableOdinAnnouncements || string.IsNullOrWhiteSpace(options.OdinCultMeshUri))
        return;

    var surfaces = new[]
    {
        ("aetheria.operations", "Aetheria Operations", AetheriaStateNode.OperationsSurfaceKey),
        ("aetheria.player_settings", "Aetheria Player Settings", AetheriaStateNode.PlayerSettingsSurfaceKey),
        ("aetheria.daemon.game", "Aetheria Daemon Game", AetheriaRuntimeVerseRecordKeys.DaemonGameSurface),
        ("aetheria.daemon.game.tui", "Aetheria Daemon Game TUI", AetheriaRuntimeVerseRecordKeys.DaemonGameTuiSurface),
        ("aetheria.daemon.editor", "Aetheria Daemon Editor", AetheriaRuntimeVerseRecordKeys.DaemonEditorSurface),
        ("aetheria.daemon.editor.tui", "Aetheria Daemon Editor TUI", AetheriaRuntimeVerseRecordKeys.DaemonEditorTuiSurface),
        ("aetheria.main_menu.root", "Main Menu", AetheriaRuntimeVerseRecordKeys.MainMenuSurface),
        ("aetheria.main_menu.settings", "Main Menu Settings", AetheriaRuntimeVerseRecordKeys.MainMenuSettingsSurface),
        ("aetheria.main_menu.input_settings", "Main Menu Input Settings", AetheriaRuntimeVerseRecordKeys.MainMenuInputSettingsSurface),
        ("aetheria.main_menu.player_settings", "Main Menu Player Settings", AetheriaRuntimeVerseRecordKeys.MainMenuPlayerSettingsSurface),
        ("aetheria.inventory.panel", "Inventory Panel", AetheriaRuntimeVerseRecordKeys.InventoryPanelSurface),
        ("aetheria.inventory.panel.dropdown", "Inventory Dropdown", AetheriaRuntimeVerseRecordKeys.InventoryDropdownSurface),
        (AetheriaRuntimeSectorMapSurfaceBuilder.SurfaceId, "Sector Map", AetheriaRuntimeVerseRecordKeys.MapMenuSurface),
        ("aetheria.trade.menu", "Trade Menu", AetheriaRuntimeVerseRecordKeys.TradeMenuSurface),
        (AetheriaRuntimeHangarCommands.SurfaceId, "Aetheria Hangar", AetheriaRuntimeVerseRecordKeys.HangarSurface)
    };

    var documents = new List<CultNetDocumentPutRawMessage>();
    var providerAdvertisement = await node.MutableDocument<EveProviderAdvertisementDocument>(AetheriaStateNode.ProviderAdvertisementSurfaceKey)
        .ReadAsync()
        .ConfigureAwait(false);
    var assetManifest = await node.MutableDocument<AetheriaRuntimeAssetManifestDocument>(AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest)
        .ReadAsync()
        .ConfigureAwait(false);
    if (assetManifest != null)
    {
        documents.Add(CreateOdinRawPut(
            "gamecult.aetheria.asset_manifest.v1",
            AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest.ToString(),
            assetManifest));
    }
    foreach (var (providerId, title, recordKey) in surfaces)
    {
        var surface = await node.MutableDocument<EveSurfaceDocument>(recordKey)
            .ReadAsync()
            .ConfigureAwait(false);
        if (surface?.Surface?.Root == null)
            continue;

        if (providerAdvertisement != null)
        {
            documents.Add(CreateOdinRawPut(
                EveProviderAdvertisementDocument.SchemaId,
                providerId,
                BuildOdinProviderAdvertisement(providerId, title, providerAdvertisement, updatedAtUtc)));
        }
        documents.Add(CreateOdinRawPut(
            "gamecult.eve.surface_state.v1",
            providerId,
            BuildOdinSurfaceState(providerId, title, surface, updatedAtUtc, assetManifest)));
    }

    if (documents.Count == 0)
        return;

    try
    {
        await PublishOdinRawPutsAsync(options, options.OdinCultMeshUri, documents).ConfigureAwait(false);
    }
    catch (Exception ex) when (
        ex is IOException ||
        ex is SocketException ||
        ex is TimeoutException ||
        ex is InvalidOperationException)
    {
        Console.WriteLine($"Aetheria Odin announcement skipped for {options.OdinCultMeshUri}: {ex.GetType().Name}: {ex.Message}");
    }
}

static EveAssetCatalogDocument BuildCoreAssetCatalog(
    AetheriaUnityBundleArtifactSet unityBundles,
    AetheriaRuntimeAssetManifestDocument source)
{
    var variants = unityBundles.Bundles;

    var assets = (source.Assets ?? Array.Empty<AetheriaRuntimeAssetManifestEntry>())
        .Where(entry => entry?.Ref != null &&
            (entry.Ref.Metadata.TryGetValue("unityAssetPath", out _) ||
             entry.Ref.Metadata.TryGetValue("resourcesPath", out _)))
        .Select(entry =>
        {
            var bundleName = AetheriaRuntimeAssets.ResolveUnityBundleName(entry);
            var entryVariants = variants
                .Where(bundle => string.Equals(bundle.BundleName, bundleName, StringComparison.Ordinal))
                .Select(bundle =>
                {
                    var dependencyUris = bundle.Dependencies.Select(dependency => variants.Single(candidate =>
                            string.Equals(candidate.Platform, bundle.Platform, StringComparison.Ordinal) &&
                            string.Equals(candidate.BundleName, dependency, StringComparison.Ordinal)).Artifact.ManifestKey.Value)
                        .ToArray();
                    var unityAssetPath = ResolveUnityBundleAssetKey(entry, bundle.Assets);
                    return new EveAssetVariant(
                        "unity-scene",
                        bundle.Platform,
                        "unity-assetbundle",
                        bundle.Artifact.ManifestKey.Value,
                        $"sha256:{bundle.Artifact.Manifest.ContentHash}",
                        bundle.Artifact.Manifest.SizeBytes,
                        unityAssetPath,
                        new Dictionary<string, string>(
                            entry.Ref.Metadata
                                .Where(pair => pair.Key.StartsWith("unity.", StringComparison.Ordinal))
                                .Append(new KeyValuePair<string, string>("unity.bundleName", bundle.BundleName))
                                .Append(new KeyValuePair<string, string>("unity.bundleDependencyUris", string.Join(";", dependencyUris)))
                                .Append(new KeyValuePair<string, string>("renderChannel.map.unityLayer", "14")),
                            StringComparer.Ordinal));
                })
                .ToArray();
            if (entryVariants.Length == 0)
                throw new InvalidOperationException(
                    $"No built Unity bundle named '{bundleName}' contains advertised asset '{entry.Ref.AssetKey}'.");
            return new EveAssetCatalogEntry(
                entry.Ref.AssetKey,
                entry.Ref.Kind,
                entryVariants,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mimeType"] = entry.Ref.MimeType ?? "",
                    ["presentationRole"] = entry.Ref.Metadata.TryGetValue("presentationRole", out var role) ? role : ""
                });
        })
        .OrderBy(entry => entry.AssetRef, StringComparer.Ordinal)
        .ToArray();
    return new EveAssetCatalogDocument(
        AetheriaRuntimeProviderIdentity.ProviderId,
        AetheriaRuntimeVerseRecordKeys.EveAssetCatalog.ToString(),
        AssetCatalogVersion(source.PublishedAtUtc),
        source.PublishedAtUtc,
        assets);
}

static long AssetCatalogVersion(string publishedAtUtc)
{
    return DateTimeOffset.TryParse(
            publishedAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var publishedAt)
        ? publishedAt.UtcDateTime.Ticks
        : DateTimeOffset.UtcNow.UtcDateTime.Ticks;
}

static string ResolveUnityBundleAssetKey(
    AetheriaRuntimeAssetManifestEntry entry,
    IReadOnlyList<string> bundleAssets)
{
    if (entry.Ref.Metadata.TryGetValue("bundleAssetPath", out var bundleAssetPath))
        return bundleAssetPath;
    if (entry.Ref.Metadata.TryGetValue("unityAssetPath", out var explicitPath))
        return explicitPath;
    if (!entry.Ref.Metadata.TryGetValue("resourcesPath", out var resourcesPath))
        throw new InvalidOperationException($"Asset '{entry.Ref.AssetKey}' has no Unity asset path.");

    var expected = $"Assets/Resources/{resourcesPath}".Replace('\\', '/');
    var resolved = bundleAssets.FirstOrDefault(path => string.Equals(
        Path.ChangeExtension(path, null)?.Replace('\\', '/'),
        expected,
        StringComparison.OrdinalIgnoreCase));
    return resolved ?? throw new InvalidOperationException(
        $"Bundle asset for '{entry.Ref.AssetKey}' was not found below '{expected}'.");
}

static (IReadOnlyList<string> Assets, IReadOnlyList<string> Dependencies) ReadUnityBundleSidecar(string bundlePath)
{
    var manifestPath = bundlePath + ".manifest";
    if (!File.Exists(manifestPath))
        throw new FileNotFoundException("Unity bundle sidecar manifest is missing.", manifestPath);
    var assets = new List<string>();
    var dependencies = new List<string>();
    var section = "";
    foreach (var line in File.ReadLines(manifestPath))
    {
        var trimmed = line.Trim();
        if (trimmed == "Assets:") { section = "assets"; continue; }
        if (trimmed.StartsWith("Dependencies:", StringComparison.Ordinal))
        {
            section = "dependencies";
            if (trimmed == "Dependencies: []") section = "";
            continue;
        }
        if (!trimmed.StartsWith("- ", StringComparison.Ordinal)) continue;
        if (section == "assets") assets.Add(trimmed.Substring(2));
        else if (section == "dependencies")
            dependencies.Add(Path.GetFileName(trimmed.Substring(2).Replace('\\', '/')));
    }
    return (assets, dependencies);
}

static IReadOnlyList<(string Path, string Platform, string BundleName)> FindAssetBundles(AetheriaDaemonHostOptions options)
{
    return Directory.Exists(options.AssetBundleRoot)
        ? Directory.GetFiles(options.AssetBundleRoot, "aetheria-*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (
                path,
                new DirectoryInfo(Path.GetDirectoryName(path)!).Name,
                Path.GetFileName(path)))
            .ToArray()
        : Array.Empty<(string, string, string)>();
}

static AetheriaUnityBundleArtifactSet BuildUnityBundleArtifactSet(AetheriaDaemonHostOptions options)
{
    var bundles = FindAssetBundles(options)
        .Select(bundle =>
        {
            var sidecar = ReadUnityBundleSidecar(bundle.Path);
            return new AetheriaUnityBundleArtifact(
                bundle.Path,
                bundle.Platform,
                bundle.BundleName,
                sidecar.Assets,
                sidecar.Dependencies,
                PackAssetBundle(bundle.Path, bundle.Platform, bundle.BundleName));
        })
        .ToArray();
    foreach (var bundle in bundles)
    foreach (var dependency in bundle.Dependencies)
    {
        if (!bundles.Any(candidate =>
                string.Equals(candidate.Platform, bundle.Platform, StringComparison.Ordinal) &&
                string.Equals(candidate.BundleName, dependency, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Unity bundle '{bundle.BundleName}' depends on missing bundle '{dependency}' for {bundle.Platform}.");
    }
    return new AetheriaUnityBundleArtifactSet(bundles);
}

static CultMeshCdnArtifact PackAssetBundle(string path, string platform, string bundleName)
{
    return CultMeshCdn.PackArtifact(
        $"aetheria/bundles/{platform}/{bundleName}",
        File.ReadAllBytes(path),
        new CultMeshCdnPackOptions
        {
            Kind = CultMeshCdnArtifactKinds.Asset,
            Version = "1",
            MimeType = "application/vnd.unity.assetbundle",
            Tags = ["aetheria", "unity-scene", platform, bundleName]
        });
}

static CultNetDocumentPutRawMessage CreateOdinRawPut<T>(
    string schemaId,
    string recordKey,
    T payload)
{
    var now = DateTimeOffset.UtcNow.ToString("O");
    return new CultNetDocumentPutRawMessage
    {
        MessageId = $"aetheria-odin-put:{recordKey}:{Guid.NewGuid():N}",
        Document = new CultNetRawDocumentRecord
        {
            SchemaId = schemaId,
            RecordKey = recordKey,
            StoredAt = now,
            PayloadEncoding = "messagepack",
            Payload = MessagePackSerializer.Serialize(payload),
            SourceRuntimeId = "aetheria-daemon",
            SourceRole = "aetheria-daemon",
            Tags = ["aetheria", "eve", "odin"]
        }
    };
}

static EveProviderAdvertisementDocument BuildOdinProviderAdvertisement(
    string providerId,
    string title,
    EveProviderAdvertisementDocument canonical,
    string updatedAtUtc)
{
    var surfaces = canonical.Surfaces
        .Where(surface => string.Equals(surface.SurfaceId, providerId, StringComparison.Ordinal))
        .ToArray();
    var commands = canonical.Commands
        .Where(command => string.IsNullOrWhiteSpace(command.SurfaceId) ||
            string.Equals(command.SurfaceId, providerId, StringComparison.Ordinal))
        .ToArray();
    return new EveProviderAdvertisementDocument(
        providerId,
        canonical.ServiceId,
        canonical.VerseId,
        title,
        canonical.Kind,
        canonical.CultMeshAddress,
        string.IsNullOrWhiteSpace(updatedAtUtc) ? canonical.UpdatedAtUtc : updatedAtUtc,
        canonical.Freshness,
        canonical.Schemas,
        canonical.Witnesses,
        surfaces,
        commands);
}

static object[] BuildOdinSurfaceState(
    string providerId,
    string title,
    EveSurfaceDocument document,
    string updatedAtUtc,
    AetheriaRuntimeAssetManifestDocument? assetManifest)
{
    var surface = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["schema"] = "gamecult.eve.surface.v1",
        ["id"] = document.Surface.Id,
        ["title"] = string.IsNullOrWhiteSpace(document.Title) ? title : document.Title,
        ["root"] = ToOdinSurfaceComponent(document.Surface.Root),
        ["assetManifestDocumentId"] = AetheriaRuntimeVerseRecordKeys.DaemonAssetManifest.ToString(),
        ["assetManifestSchemaId"] = AetheriaRuntimeDaemonSchemas.AssetManifest,
        ["assets"] = ToOdinAssetRefs(assetManifest)
    };
    return
    [
        providerId,
        title,
        document.Version,
        string.IsNullOrWhiteSpace(document.UpdatedAtUtc)
            ? (string.IsNullOrWhiteSpace(updatedAtUtc) ? DateTimeOffset.UtcNow.ToString("O") : updatedAtUtc)
            : document.UpdatedAtUtc,
        surface
    ];
}

static object[] ToOdinAssetRefs(AetheriaRuntimeAssetManifestDocument? assetManifest)
{
    return assetManifest?.Assets?
        .Where(entry => !string.IsNullOrWhiteSpace(entry?.Ref?.AssetKey))
        .Select(entry => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["assetKey"] = entry.Ref.AssetKey,
            ["kind"] = entry.Ref.Kind,
            ["uri"] = entry.Ref.Uri,
            ["transport"] = entry.Ref.Transport,
            ["mimeType"] = entry.Ref.MimeType,
            ["contentHash"] = entry.Ref.ContentHash,
            ["tags"] = entry.Tags?.ToArray() ?? Array.Empty<string>()
        })
        .Cast<object>()
        .ToArray()
        ?? Array.Empty<object>();
}

static Dictionary<string, object?> ToOdinSurfaceComponent(EveSurfaceComponent component)
{
    var result = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["id"] = component.Id,
        ["kind"] = component.Kind,
        ["props"] = component.Props.ToDictionary(prop => prop.Key, prop => (object?)prop.Value, StringComparer.Ordinal),
        ["children"] = component.Children.Select(ToOdinSurfaceComponent).ToArray()
    };
    if (component.Layout.Count > 0)
    {
        result["layout"] = component.Layout.ToDictionary(prop => prop.Key, prop => (object?)prop.Value, StringComparer.Ordinal);
    }

    if (component.Style.Count > 0)
    {
        result["style"] = component.Style.ToDictionary(prop => prop.Key, prop => (object?)prop.Value, StringComparer.Ordinal);
    }

    if (component.EmbeddedDocuments.Count > 0)
    {
        result["embeddedDocuments"] = component.EmbeddedDocuments
            .Select(document => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slotId"] = document.SlotId,
                ["documentId"] = document.DocumentId,
                ["schemaId"] = document.SchemaId,
                ["presentationKind"] = document.PresentationKind
            })
            .ToArray();
    }

    return result;
}

static async Task PublishOdinRawPutsAsync(
    AetheriaDaemonHostOptions options,
    string cultMeshUri,
    IReadOnlyList<CultNetDocumentPutRawMessage> documents)
{
    var endpoint = CultMesh.ResolveRudpEndpoint(cultMeshUri);
    using var client = CultMesh.ConnectRudpClient(
        $"{options.DaemonId}-odin-publisher",
        0x0d1d0002,
        endpoint,
        new CultMeshRudpClientOptions
        {
            ConnectPayload = System.Text.Encoding.UTF8.GetBytes("aetheria-odin-surface-announcer"),
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            PollInterval = TimeSpan.FromMilliseconds(5),
            SocketOptions = new CultMeshRudpSocketOptions
            {
                BindHost = "0.0.0.0",
                BindPort = 0,
                TransportId = "aetheria-odin-surface-announcer",
                MaxFragmentBytes = 1200,
                MaxPendingReliablePackets = 512,
                ResendDelayMs = 25
            }
        });

    foreach (var document in documents)
    {
        client.SendSchemaMessage(document);
        var drainUntil = DateTimeOffset.UtcNow.AddMilliseconds(40);
        while (DateTimeOffset.UtcNow < drainUntil)
        {
            _ = client.ReceiveOnce();
            client.PollResends();
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    var deadline = DateTimeOffset.UtcNow.AddMilliseconds(500);
    while (DateTimeOffset.UtcNow < deadline)
    {
        _ = client.ReceiveOnce();
        client.PollResends();
        await Task.Delay(10).ConfigureAwait(false);
    }
}

static async Task PublishRuntimeSessionAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    string startedAtUtc,
    string status)
{
    var now = DateTimeOffset.UtcNow.ToString("O");
    await node.MutableDocument<AetheriaRuntimeSession>(AetheriaStateNode.RuntimeSessionKey(options.DaemonId))
        .ReplaceAsync(new AetheriaRuntimeSession
        {
            RuntimeId = options.DaemonId,
            Role = "verse-daemon",
            StartedAtUtc = startedAtUtc,
            LastSeenAtUtc = now,
            Status = status
        })
        .ConfigureAwait(false);
    await PublishStateSurfacesAsync(
        node,
        options,
        now,
        publishHangar: !string.Equals(status, "running", StringComparison.Ordinal) &&
            !string.Equals(status, "completed", StringComparison.Ordinal)).ConfigureAwait(false);
}

static async Task EnsureWorldDocumentAsync(AetheriaStateNode node)
{
    var now = DateTimeOffset.UtcNow.ToString("O");
    var world = node.MutableDocument<AetheriaWorldState>(AetheriaStateNode.WorldKey);
    var existing = await world.ReadAsync().ConfigureAwait(false);
    if (existing != null)
    {
        return;
    }

    await world.ReplaceAsync(new AetheriaWorldState
    {
        Name = "Aetheria",
        WorldId = "aetheria",
        SchemaEpoch = 1,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    }).ConfigureAwait(false);
}

static async Task<bool> EnsureTradeValuePolicyAsync(AetheriaStateNode node, string now)
{
    var tradeValuePolicy = node.MutableDocument<AetheriaTradeValuePolicy>(AetheriaStateNode.TradeValuePolicyKey);
    var existing = await tradeValuePolicy.ReadAsync().ConfigureAwait(false);
    if (existing != null)
        return false;

    await tradeValuePolicy.ReplaceAsync(
        AetheriaRuntimeStateMapper.ToTradeValuePolicy(
            AetheriaRuntimeTradeValueSettings.Default,
            now)).ConfigureAwait(false);
    return true;
}

static async Task EnsurePlayableRunDocumentsAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    string now,
    AetheriaRuntimeDaemonFrameDocument? latestFrame)
{
    if (options.UseTerminusFixture)
    {
        await AetheriaDaemonZoneGenerator.WritePlayableRunAsync(
            node, node.RuntimeCatalog().Latest(), now, options.TerminusScenario).ConfigureAwait(false);
        return;
    }

    if (HasPlayableRun(latestFrame?.Run))
        return;

    var settings = await node.MutableDocument<AetheriaPlayerSettings>(AetheriaStateNode.PlayerSettingsKey).ReadAsync().ConfigureAwait(false);
    if (!string.IsNullOrWhiteSpace(settings?.ActiveRunKey))
    {
        var existingRun = await ReadRuntimeRunCheckpointAsync(node, options.RenderSettings).ConfigureAwait(false);
        if (HasPlayableRun(existingRun))
            return;
    }

    await AetheriaDaemonRunFactory.WriteAsync(
        node,
        await node.RuntimeCatalogForGenerationAsync().ConfigureAwait(false),
        now,
        now).ConfigureAwait(false);
}

static Task EnsureGameSessionAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    string now,
    AetheriaRuntimeDaemonFrameDocument? latestFrame)
    => node.CommitAsync(() => EnsureGameSessionCoreAsync(node, options, now, latestFrame));

static async Task EnsureGameSessionCoreAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    string now,
    AetheriaRuntimeDaemonFrameDocument? latestFrame)
{
    var existing = await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
        .ReadAsync().ConfigureAwait(false);
    var runRecordKey = existing?.RunRecordKey ?? "";
    var run = !string.IsNullOrWhiteSpace(runRecordKey)
        ? await ReadRuntimeRunCheckpointAsync(node, options.RenderSettings, runRecordKey).ConfigureAwait(false)
        : null;
    if (!HasPlayableRun(run) && FrameBelongsToSession(latestFrame, existing))
        run = latestFrame!.Run;
    if (!HasPlayableRun(run))
    {
        if (existing != null &&
            (!string.IsNullOrWhiteSpace(existing.RunId) || !string.IsNullOrWhiteSpace(existing.RunRecordKey)))
            throw new InvalidDataException(
                $"Active game session '{existing.RunId}' has no canonical run at '{existing.RunRecordKey}'.");
        var settings = await node.MutableDocument<AetheriaPlayerSettings>(AetheriaStateNode.PlayerSettingsKey)
            .ReadAsync().ConfigureAwait(false);
        runRecordKey = settings?.ActiveRunKey ?? "";
        run = await ReadRuntimeRunCheckpointAsync(node, options.RenderSettings, runRecordKey).ConfigureAwait(false);
    }
    if (run == null)
        throw new InvalidDataException("Aetheria game session requires a canonical active run.");
    var expectedMode = string.IsNullOrWhiteSpace(run.GameMode)
        ? run.IsTutorial ? AetheriaGameSessionState.AetheriaMode : AetheriaGameSessionState.TerminusMode
        : run.GameMode;
    var initialSimulationRate = options.UseTerminusFixture ? 0 : 1;
    if (existing != null &&
        string.Equals(existing.Mode, expectedMode, StringComparison.Ordinal) &&
        string.Equals(existing.RunId, run.RunId, StringComparison.Ordinal) &&
        string.Equals(existing.RunRecordKey, runRecordKey, StringComparison.Ordinal) &&
        string.Equals(existing.ControlledEntityKey, run.CurrentEntityKey, StringComparison.Ordinal) &&
        Math.Abs(existing.SimulationRate - initialSimulationRate) < 0.000001 &&
        Math.Abs((existing.EffectiveSimulationRate ?? existing.SimulationRate) - initialSimulationRate) < 0.000001)
        return;

    await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
        .ReplaceAsync(new AetheriaGameSessionState
        {
            Mode = expectedMode,
            SessionId = options.SessionId,
            RunId = run.RunId,
            RunRecordKey = runRecordKey,
            ControlledEntityKey = run.CurrentEntityKey,
            EntrySurfaceId = AetheriaRuntimeDaemonGameSurfaceBuilder.PilotSurfaceId,
            SimulationRate = initialSimulationRate,
            EffectiveSimulationRate = initialSimulationRate,
            UpdatedAtUtc = now
        }).ConfigureAwait(false);
}

static bool FrameBelongsToSession(
    AetheriaRuntimeDaemonFrameDocument? frame,
    AetheriaGameSessionState? session) =>
    HasPlayableRun(frame?.Run) &&
    session != null &&
    !string.IsNullOrWhiteSpace(session.RunId) &&
    !string.IsNullOrWhiteSpace(session.RunRecordKey) &&
    string.Equals(frame!.RunRecordKey, session.RunRecordKey, StringComparison.Ordinal) &&
    string.Equals(frame.Run.RunId, session.RunId, StringComparison.Ordinal) &&
    string.Equals(frame.GameMode, session.Mode, StringComparison.Ordinal);

static async Task EnsureVerseAuthorityPolicyAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options)
{
    var existing = await node.MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy).ReadAsync().ConfigureAwait(false);
    if (existing != null && string.Equals(existing.Schema, AetheriaRuntimeVerseAuthoritySchemas.Policy, StringComparison.Ordinal))
        return;

    var policy = AetheriaRuntimeVerseAuthorityPolicyDocument.TrustedCoop(options.VerseId, options.DaemonId);
    await node.MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy)
        .ReplaceAsync(policy)
        .ConfigureAwait(false);
}

static async Task<AetheriaRuntimeRunCheckpointCommit?> ReadRuntimeRunCheckpointAsync(
    AetheriaStateNode node,
    AetheriaRuntimeDaemonRenderSettings renderSettings,
    string? runRecordKey = null)
{
    if (string.IsNullOrWhiteSpace(runRecordKey))
    {
        var settings = await node.MutableDocument<AetheriaPlayerSettings>(AetheriaStateNode.PlayerSettingsKey).ReadAsync().ConfigureAwait(false);
        runRecordKey = settings?.ActiveRunKey;
    }
    if (string.IsNullOrWhiteSpace(runRecordKey)) return null;

    var run = await ReadDurableBootstrapDocumentAsync<AetheriaRunState>(
        node,
        new CultRecordKey(runRecordKey)).ConfigureAwait(false);
    if (run == null)
        return null;

    var catalog = node.RuntimeCatalog().Latest() ?? new AetheriaRuntimeCatalogSnapshot();
    var zones = new List<AetheriaRuntimeZoneSnapshotCommit>();
    var zoneKeys = run.ZoneKeys ?? Array.Empty<string>();
    for (var zoneIndex = 0; zoneIndex < zoneKeys.Length; zoneIndex++)
    {
        var zone = await ReadDurableBootstrapDocumentAsync<AetheriaZoneState>(
            node,
            new CultRecordKey(zoneKeys[zoneIndex])).ConfigureAwait(false);
        if (zone == null)
            continue;

        zones.Add(await ToRuntimeZoneAsync(node, zone, zoneIndex, renderSettings, catalog).ConfigureAwait(false));
    }

    return new AetheriaRuntimeRunCheckpointCommit
    {
        RunId = run.RunId ?? "",
        IsTutorial = run.IsTutorial,
        GameMode = string.IsNullOrWhiteSpace(run.GameMode)
            ? run.IsTutorial ? AetheriaGameSessionState.AetheriaMode : AetheriaGameSessionState.TerminusMode
            : run.GameMode,
        EntranceZoneIndex = run.EntranceZoneIndex,
        ExitZoneIndex = run.ExitZoneIndex,
        CurrentZoneIndex = run.CurrentZoneIndex,
        DiscoveredZoneIndices = (run.DiscoveredZoneIndices ?? Array.Empty<int>()).ToArray(),
        Zones = zones,
        FactionRelationships = (run.FactionRelationships ?? Array.Empty<AetheriaFactionRelationshipState>())
            .Select(relationship => new AetheriaRuntimeFactionRelationshipCommit
            {
                Relationship = relationship.Relationship ?? "",
                Standing = relationship.Standing,
                FactionKey = relationship.FactionKey ?? ""
            })
            .ToArray(),
        HomeZones = (run.HomeZones ?? Array.Empty<AetheriaFactionZoneState>())
            .Select(entry => new AetheriaRuntimeFactionZoneCommit
            {
                FactionIndex = entry.FactionIndex,
                ZoneIndex = entry.ZoneIndex
            })
            .ToArray(),
        BossZones = (run.BossZones ?? Array.Empty<AetheriaFactionZoneState>())
            .Select(entry => new AetheriaRuntimeFactionZoneCommit
            {
                FactionIndex = entry.FactionIndex,
                ZoneIndex = entry.ZoneIndex
            })
            .ToArray(),
        GenerationSeed = run.GenerationSeed,
        CurrentEntityKey = run.CurrentEntityKey ?? "",
        LifecyclePhase = string.IsNullOrWhiteSpace(run.LifecyclePhase)
            ? AetheriaRuntimeRunLifecycle.Active
            : run.LifecyclePhase,
        TerminalReason = run.TerminalReason ?? "",
        TerminalFrameId = run.TerminalFrameId,
        AgentTasks = (run.AgentTasks ?? Array.Empty<AetheriaAgentTaskState>())
            .Select(task => new AetheriaRuntimeAgentTaskCommit
            {
                TaskId = task.TaskId ?? "",
                CorporationKey = task.CorporationKey ?? "",
                TaskType = task.TaskType ?? "",
                Priority = task.Priority,
                ZoneIndex = task.ZoneIndex,
                Status = task.Status ?? AetheriaRuntimeAgentTaskStatuses.Queued,
                AssignedEntityIndex = task.AssignedEntityIndex,
                CompletionRadius = task.CompletionRadius,
                TargetOrbitKeys = (task.TargetOrbitKeys ?? Array.Empty<string>()).ToArray(),
                CircuitIndex = task.CircuitIndex
            })
            .ToArray(),
        Credits = 1000000
    };
}

static async Task<AetheriaRuntimeZoneSnapshotCommit> ToRuntimeZoneAsync(
    AetheriaStateNode node,
    AetheriaZoneState zone,
    int zoneIndex,
    AetheriaRuntimeDaemonRenderSettings renderSettings,
    AetheriaRuntimeCatalogSnapshot catalog)
{
    var entityKeys = zone.EntityKeys ?? Array.Empty<string>();
    var entityIndices = entityKeys
        .Select((key, index) => new { key, index })
        .Where(pair => !string.IsNullOrWhiteSpace(pair.key))
        .ToDictionary(pair => pair.key, pair => pair.index, StringComparer.Ordinal);
    var entities = new List<AetheriaRuntimeEntitySnapshotCommit>();
    for (var entityIndex = 0; entityIndex < entityKeys.Length; entityIndex++)
    {
        var entity = await ReadDurableBootstrapDocumentAsync<AetheriaEntitySnapshot>(
            node,
            new CultRecordKey(entityKeys[entityIndex])).ConfigureAwait(false);
        if (entity != null)
            entities.Add(ToRuntimeEntity(entityKeys[entityIndex], entity, entityIndex, entityIndices, catalog));
    }

    return new AetheriaRuntimeZoneSnapshotCommit
    {
        ZoneIndex = zoneIndex,
        Name = zone.Name ?? "",
        PositionX = zone.Position?.X ?? 0,
        PositionY = zone.Position?.Y ?? 0,
        AdjacentZoneIndices = (zone.AdjacentZoneIndices ?? Array.Empty<int>()).ToArray(),
        FactionIndices = (zone.FactionIndices ?? Array.Empty<int>()).ToArray(),
        OwnerFactionIndex = zone.OwnerFactionIndex,
        Entities = entities,
        Orbits = (zone.Orbits ?? Array.Empty<AetheriaOrbitSnapshot>()).Select(ToRuntimeOrbit).ToArray(),
        Bodies = (zone.Bodies ?? Array.Empty<AetheriaBodySnapshot>()).Select(body => ToRuntimeBody(body, entityIndices, renderSettings)).ToArray(),
        DroppedPickups = (zone.DroppedPickups ?? Array.Empty<AetheriaDroppedPickupSnapshot>()).Select(ToRuntimePickup).ToArray(),
        GravityTerrainRadius = zone.GravityTerrainRadius,
        GravityTerrainDepth = zone.GravityTerrainDepth,
        GravityTerrainDepthExponent = zone.GravityTerrainDepthExponent,
        GravityTerrainBoundaryFog = zone.GravityTerrainBoundaryFog,
        GravityTerrainWaveFrequency = zone.GravityTerrainWaveFrequency,
        SimulationTimeSeconds = zone.SimulationTimeSeconds,
        NextPickupIndex = Math.Max(
            zone.NextPickupIndex,
            (zone.DroppedPickups ?? Array.Empty<AetheriaDroppedPickupSnapshot>())
                .Select(pickup => pickup?.PickupIndex ?? -1)
                .DefaultIfEmpty(-1)
                .Max() + 1)
    };
}

static async Task<TDocument?> ReadDurableBootstrapDocumentAsync<TDocument>(
    AetheriaStateNode node,
    CultRecordKey key)
    where TDocument : class
{
    if (node.Cache.Get<TDocument>(key) == null)
    {
        await node.HydrateRecordsAsync(metadata =>
            string.Equals(metadata.Key, key.ToString(), StringComparison.Ordinal)).ConfigureAwait(false);
    }

    return await node.MutableDocument<TDocument>(key).ReadAsync().ConfigureAwait(false);
}

static AetheriaRuntimeEntitySnapshotCommit ToRuntimeEntity(
    string entityKey,
    AetheriaEntitySnapshot entity,
    int entityIndex,
    IReadOnlyDictionary<string, int> entityIndices,
    AetheriaRuntimeCatalogSnapshot catalog)
{
    var equipment = ToEntitySlotCommits(entity.Equipment);
    var runtimeEntity = new AetheriaRuntimeEntitySnapshotCommit
    {
        EntityId = entityKey ?? "",
        HomeEntityId = entity.HomeEntityKey ?? "",
        AgentTaskCapabilities = (entity.AgentTaskCapabilities ?? Array.Empty<string>()).ToArray(),
        AssignedAgentTaskId = entity.AssignedAgentTaskId ?? "",
        EntityIndex = entityIndex,
        Name = entity.Name ?? "",
        Kind = entity.Kind ?? "",
        PositionX = entity.Position?.X ?? 0,
        PositionY = entity.Position?.Y ?? 0,
        PositionZ = entity.Position?.Z ?? 0,
        DirectionX = entity.Direction?.X ?? 0,
        DirectionY = entity.Direction?.Y ?? 1,
        LookDirectionX = entity.LookDirection?.X ?? entity.Direction?.X ?? 0,
        LookDirectionY = entity.LookDirection?.Y ?? entity.Direction?.Y ?? 1,
        HelmStrafe = entity.HelmInput?.X ?? 0,
        HelmForward = entity.HelmInput?.Y ?? 0,
        OrbitKey = entity.OrbitKey ?? "",
        SecurityLevel = entity.SecurityLevel,
        SecurityRadius = entity.SecurityRadius,
        FactionKey = entity.FactionKey ?? "",
        HullItemKey = entity.HullItemKey ?? "",
        Equipment = equipment,
        CargoBays = ToEntitySlotCommits(entity.CargoBays),
        DockingBays = ToEntitySlotCommits(entity.DockingBays),
        ChildEntityIndices = (entity.ChildEntityKeys ?? Array.Empty<string>())
            .Select(key => entityIndices.TryGetValue(key, out var index) ? index : -1)
            .Where(index => index >= 0)
            .ToArray(),
        WeaponGroups = (entity.WeaponGroups ?? Array.Empty<AetheriaWeaponGroupSnapshot>())
            .Select(group => (IReadOnlyList<int>)(group.EquipmentIndices ?? Array.Empty<int>()).ToArray())
            .ToArray(),
        StatGrids = (entity.StatGrids ?? Array.Empty<AetheriaEntityStatGrid>())
            .Select(grid => new AetheriaRuntimeEntityStatGridCommit
            {
                Name = grid.Name ?? "",
                Width = grid.Width,
                Height = grid.Height,
                Values = (grid.Values ?? Array.Empty<double>()).ToArray()
            })
            .ToArray(),
        VelocityX = entity.Velocity?.X ?? 0,
        VelocityY = entity.Velocity?.Y ?? 0,
        TargetEntityIndex = entityIndices.TryGetValue(entity.TargetEntityKey ?? "", out var targetIndex) ? targetIndex : -1,
        IsActive = entity.IsActive,
        HeatsinksEnabled = entity.HeatsinksEnabled,
        OverrideShutdown = entity.OverrideShutdown,
        TractorPower = entity.TractorPower,
        Heatstroke = entity.Heatstroke,
        Hypothermia = entity.Hypothermia,
        ActiveConsumables = (entity.ActiveConsumables ?? Array.Empty<AetheriaActiveConsumableSnapshot>())
            .Select(consumable => new AetheriaRuntimeActiveConsumableCommit
            {
                EffectId = consumable.EffectId ?? "",
                ItemKey = consumable.ItemKey ?? "",
                Quality = consumable.Quality,
                RemainingDuration = consumable.RemainingDuration,
                Duration = consumable.Duration,
                BehaviorStates = (consumable.BehaviorStates ?? Array.Empty<AetheriaConsumableBehaviorStateSnapshot>())
                    .Select(state => new AetheriaRuntimeConsumableBehaviorStateCommit
                    {
                        BehaviorIndex = state.BehaviorIndex,
                        BehaviorKind = state.BehaviorKind ?? "",
                        ScalarState = state.ScalarState,
                        BehaviorId = state.BehaviorId ?? ""
                    })
                    .ToArray()
            })
            .ToArray(),
        BehaviorProgress = (entity.BehaviorProgress ?? Array.Empty<AetheriaBehaviorProgressSnapshot>())
            .Select(progress => new AetheriaRuntimeBehaviorProgressCommit
            {
                OwnerKind = progress.OwnerKind ?? "",
                OwnerIndex = progress.OwnerIndex,
                BehaviorIndex = progress.BehaviorIndex,
                BehaviorKind = progress.BehaviorKind ?? "",
                Progress = progress.Progress
            })
            .ToArray(),
        WeaponStates = Array.Empty<AetheriaRuntimeWeaponStateCommit>(),
        BehaviorStates = AetheriaRuntimeBehaviorStateProjector.CreateEquipmentBehaviorStates(equipment, catalog),
        CargoContents = ToCargoBayCommits(entity.CargoContents),
        DockingBayContents = ToCargoBayCommits(entity.DockingBayContents),
        DockingBayAssignments = (entity.DockingBayAssignments ?? Array.Empty<int>()).ToArray(),
        Visibility = entity.Visibility,
        VisibilitySourceCount = entity.VisibilitySourceCount,
        FogFieldEmitters = (entity.FogFieldEmitters ?? Array.Empty<AetheriaFogFieldEmitterSnapshot>())
            .Select(emitter => new AetheriaRuntimeFogFieldEmitterCommit
            {
                Enabled = emitter.Enabled,
                Radius = emitter.Radius,
                Density = emitter.Density,
                OffsetX = emitter.OffsetX,
                OffsetZ = emitter.OffsetZ,
                FalloffExponent = emitter.FalloffExponent
            })
            .ToArray(),
        Contacts = (entity.Contacts ?? Array.Empty<AetheriaEntityContactSnapshot>())
            .Select(contact => ToRuntimeContact(contact, entityIndices))
            .Where(contact => contact.TargetEntityIndex >= 0)
            .ToArray(),
        LoadoutGeneration = entity.LoadoutGeneration == null ? null : new AetheriaRuntimeLoadoutGenerationReceiptCommit
        {
            Seed = entity.LoadoutGeneration.Seed,
            SourceZoneIndex = entity.LoadoutGeneration.SourceZoneIndex,
            AvailabilityFactionKey = entity.LoadoutGeneration.AvailabilityFactionKey ?? "",
            PriceExponent = entity.LoadoutGeneration.PriceExponent,
            Selections = (entity.LoadoutGeneration.Selections ?? Array.Empty<AetheriaLoadoutGenerationSelection>())
                .Select(value => new AetheriaRuntimeLoadoutGenerationSelectionCommit
                {
                    Role = value.Role ?? "",
                    ItemKey = value.ItemKey ?? "",
                    ManufacturerKey = value.ManufacturerKey ?? "",
                    Price = value.Price,
                    ManufacturerDistance = value.ManufacturerDistance,
                    Allegiance = value.Allegiance
                }).ToArray()
        }
    };
    AetheriaRuntimeBehaviorStateProjector.EnsureEquipmentBehaviorStates(runtimeEntity, catalog);
    return runtimeEntity;
}

static AetheriaRuntimeEntityContactCommit ToRuntimeContact(
    AetheriaEntityContactSnapshot contact,
    IReadOnlyDictionary<string, int> entityIndices)
{
    return new AetheriaRuntimeEntityContactCommit
    {
        TargetEntityIndex = entityIndices.TryGetValue(contact.TargetEntityKey ?? "", out var index) ? index : -1,
        InfoGathered = contact.InfoGathered,
        Hostile = contact.Hostile,
        Visible = contact.Visible
    };
}

static AetheriaRuntimeOrbitSnapshotCommit ToRuntimeOrbit(AetheriaOrbitSnapshot orbit)
{
    return new AetheriaRuntimeOrbitSnapshotCommit
    {
        OrbitKey = orbit.OrbitKey ?? "",
        ParentOrbitKey = orbit.ParentOrbitKey ?? "",
        Distance = orbit.Distance,
        Phase = orbit.Phase,
        Period = orbit.Period,
        FixedPositionX = orbit.FixedPosition?.X ?? 0,
        FixedPositionY = orbit.FixedPosition?.Y ?? 0
    };
}

static AetheriaRuntimeBodySnapshotCommit ToRuntimeBody(
    AetheriaBodySnapshot body,
    IReadOnlyDictionary<string, int> entityIndices,
    AetheriaRuntimeDaemonRenderSettings renderSettings)
{
    return new AetheriaRuntimeBodySnapshotCommit
    {
        BodyKey = body.BodyKey ?? "",
        Kind = body.Kind ?? "",
        Name = body.Name ?? "",
        OrbitKey = body.OrbitKey ?? "",
        Mass = body.Mass,
        Resources = (body.Resources ?? Array.Empty<AetheriaBodyResource>())
            .Select(resource => new AetheriaRuntimeBodyResourceCommit
            {
                ItemKey = resource.ItemKey ?? "",
                Amount = resource.Amount
            })
            .ToArray(),
        BodyRadiusMultiplier = body.BodyRadiusMultiplier,
        GravityRadiusMultiplier = body.GravityRadiusMultiplier,
        GravityDepthMultiplier = body.GravityDepthMultiplier,
        GravityDepthExponent = body.GravityDepthExponent,
        Asteroids = (body.Asteroids ?? Array.Empty<AetheriaAsteroidSnapshot>())
            .Select(asteroid => new AetheriaRuntimeAsteroidCommit
            {
                Distance = asteroid.Distance,
                Phase = asteroid.Phase,
                Size = asteroid.Size,
                RotationSpeed = asteroid.RotationSpeed,
                Damage = asteroid.Damage,
                RespawnTimer = asteroid.RespawnTimer,
                MiningAccumulators = (asteroid.MiningAccumulators ?? Array.Empty<AetheriaAsteroidMiningAccumulatorSnapshot>())
                    .Select(accumulator => new AetheriaRuntimeAsteroidMiningAccumulatorCommit
                    {
                        MinerEntityIndex = entityIndices.TryGetValue(accumulator.MinerEntityKey ?? "", out var index) ? index : -1,
                        Amount = accumulator.Amount
                    })
                    .ToArray()
            })
            .ToArray(),
        GasGiantVisual = new AetheriaRuntimeGasGiantVisualCommit
        {
            FirstOffsetDomainRotationSpeed = body.GasGiantVisual?.FirstOffsetDomainRotationSpeed ?? 1,
            FirstOffsetRotationSpeed = body.GasGiantVisual?.FirstOffsetRotationSpeed ?? 1,
            SecondOffsetDomainRotationSpeed = body.GasGiantVisual?.SecondOffsetDomainRotationSpeed ?? 1,
            SecondOffsetRotationSpeed = body.GasGiantVisual?.SecondOffsetRotationSpeed ?? 1,
            AlbedoRotationSpeed = body.GasGiantVisual?.AlbedoRotationSpeed ?? 1,
            WaveRadiusMultiplier = body.GasGiantVisual?.WaveRadiusMultiplier ?? 1,
            WaveDepthMultiplier = body.GasGiantVisual?.WaveDepthMultiplier ?? 1,
            WaveDepthExponent = body.GasGiantVisual?.WaveDepthExponent ?? 8,
            WaveSpeedMultiplier = body.GasGiantVisual?.WaveSpeedMultiplier ?? 8,
            MaterialOverrides = (body.GasGiantVisual?.MaterialOverrides ?? Array.Empty<string>()).ToArray(),
            Colors = (body.GasGiantVisual?.Colors ?? Array.Empty<AetheriaColor>())
                .Select(color => new AetheriaRuntimeColorCommit { X = color.X, Y = color.Y, Z = color.Z, W = color.W })
                .ToArray()
        },
        SunVisual = new AetheriaRuntimeSunVisualCommit
        {
            LightColorX = body.SunVisual?.LightColor?.X ?? 0,
            LightColorY = body.SunVisual?.LightColor?.Y ?? 0,
            LightColorZ = body.SunVisual?.LightColor?.Z ?? 0,
            FogTintColorX = body.SunVisual?.FogTintColor?.X ?? 0,
            FogTintColorY = body.SunVisual?.FogTintColor?.Y ?? 0,
            FogTintColorZ = body.SunVisual?.FogTintColor?.Z ?? 0,
            LightRadiusMultiplier = body.SunVisual?.LightRadiusMultiplier ?? 1
        },
        GravityInfluenceCenterX = body.GravityInfluenceCenterX,
        GravityInfluenceCenterZ = body.GravityInfluenceCenterZ,
        GravityInfluenceRadius = body.GravityInfluenceRadius,
        GravityWellDepth = body.GravityWellDepth,
        GravityWaveRadius = body.GravityWaveRadius,
        GravityWaveDepth = body.GravityWaveDepth,
        GravityWaveSpeed = body.GravityWaveSpeed,
        GravityWaveFrequency = renderSettings.ResolveGravityWaveFrequency(body.Mass),
        IconSize = renderSettings.ResolveBodyIconSize(body.Mass)
    };
}

static void ApplyDaemonRenderSettings(
    AetheriaRuntimeRunCheckpointCommit run,
    AetheriaRuntimeDaemonRenderSettings renderSettings)
{
    foreach (var zone in run.Zones ?? Array.Empty<AetheriaRuntimeZoneSnapshotCommit>())
    {
        foreach (var body in zone.Bodies ?? Array.Empty<AetheriaRuntimeBodySnapshotCommit>())
        {
            body.IconSize = renderSettings.ResolveBodyIconSize(body.Mass);
            body.GravityWaveFrequency = renderSettings.ResolveGravityWaveFrequency(body.Mass);
        }
    }
}

static AetheriaRuntimeDroppedPickupCommit ToRuntimePickup(AetheriaDroppedPickupSnapshot pickup)
{
    return new AetheriaRuntimeDroppedPickupCommit
    {
        PickupIndex = pickup.PickupIndex,
        PositionX = pickup.Position?.X ?? 0,
        PositionY = pickup.Position?.Y ?? 0,
        PositionZ = pickup.Position?.Z ?? 0,
        VelocityX = pickup.Velocity?.X ?? 0,
        VelocityY = pickup.Velocity?.Y ?? 0,
        VelocityZ = pickup.Velocity?.Z ?? 0,
        Item = ToLoadoutItemCommit(pickup.Item)
    };
}

static bool HasPlayableRun(AetheriaRuntimeRunCheckpointCommit? run)
{
    return (run?.Zones ?? Array.Empty<AetheriaRuntimeZoneSnapshotCommit>())
        .Any(zone => (zone.Entities ?? Array.Empty<AetheriaRuntimeEntitySnapshotCommit>()).Count > 0);
}

static AetheriaRuntimeLoadoutItemSlotCommit[] ToEntitySlotCommits(
    IReadOnlyList<AetheriaEntityItemSlot>? slots)
{
    return (slots ?? Array.Empty<AetheriaEntityItemSlot>())
        .Select(slot => new AetheriaRuntimeLoadoutItemSlotCommit
        {
            X = slot.Position?.X ?? 0,
            Y = slot.Position?.Y ?? 0,
            Rotation = slot.Rotation ?? "None",
            Item = new AetheriaRuntimeLoadoutItemCommit
            {
                ItemKey = slot.ItemKey ?? "",
                Quality = slot.Quality,
                Durability = slot.Durability,
                Quantity = slot.Quantity,
                Enabled = slot.Enabled,
                OverrideShutdown = slot.OverrideShutdown,
                Temperature = slot.Temperature
            }
        })
        .ToArray();
}

static async Task<AetheriaVerseHostSettings> EnsureVerseHostSettingsAsync(
    AetheriaStateNode node,
    AetheriaDaemonHostOptions options,
    string now)
{
    var existing = await node.MutableDocument<AetheriaVerseHostSettings>(AetheriaStateNode.VerseHostSettingsKey).ReadAsync().ConfigureAwait(false);
    var normalized = AetheriaVerseHostSettingsNormalizer.Normalize(existing);
    normalized.ServiceId = options.DaemonId;
    normalized.VerseId = options.VerseId;
    normalized.CultMeshAddress = options.CultMeshAddress;

    if (existing == null ||
        string.IsNullOrWhiteSpace(existing.LastUpdatedAtUtc) ||
        !AetheriaVerseHostSettingsNormalizer.Equivalent(existing, normalized))
    {
        normalized.LastUpdatedAtUtc = now;
        await node.MutableDocument<AetheriaVerseHostSettings>(AetheriaStateNode.VerseHostSettingsKey)
            .ReplaceAsync(normalized)
            .ConfigureAwait(false);
    }

    return normalized;
}

static AetheriaRuntimeLoadoutTemplateCommit ToLoadoutTemplateCommit(AetheriaLoadoutTemplate template)
{
    return new AetheriaRuntimeLoadoutTemplateCommit
    {
        Name = template.Name ?? "",
        OwnerPlayerKey = template.OwnerPlayerKey ?? "",
        RootEntity = ToEntityLoadoutCommit(template.RootEntity)
    };
}

static AetheriaRuntimeEntityLoadoutCommit ToEntityLoadoutCommit(AetheriaEntityLoadout? entity)
{
    entity ??= new AetheriaEntityLoadout();
    return new AetheriaRuntimeEntityLoadoutCommit
    {
        Name = entity.Name ?? "",
        Kind = entity.Kind ?? "",
        FactionKey = entity.FactionKey ?? "",
        Hull = ToLoadoutItemCommit(entity.Hull),
        Equipment = ToSlotCommits(entity.Equipment),
        CargoBays = ToSlotCommits(entity.CargoBays),
        DockingBays = ToSlotCommits(entity.DockingBays),
        CargoContents = ToCargoBayCommits(entity.CargoContents),
        DockingBayContents = ToCargoBayCommits(entity.DockingBayContents),
        DockingBayAssignments = (entity.DockingBayAssignments ?? Array.Empty<int>()).ToArray(),
        WeaponGroups = (entity.WeaponGroups ?? Array.Empty<IReadOnlyList<int>>())
            .Select(group => (group ?? Array.Empty<int>()).ToArray())
            .ToArray(),
        Children = (entity.Children ?? Array.Empty<AetheriaEntityLoadout>())
            .Select(ToEntityLoadoutCommit)
            .ToArray()
    };
}

static AetheriaRuntimeLoadoutItemSlotCommit[] ToSlotCommits(
    IReadOnlyList<AetheriaLoadoutItemSlot>? slots)
{
    return (slots ?? Array.Empty<AetheriaLoadoutItemSlot>())
        .Select(slot => new AetheriaRuntimeLoadoutItemSlotCommit
        {
            X = slot.Position?.X ?? 0,
            Y = slot.Position?.Y ?? 0,
            Rotation = slot.Rotation ?? "None",
            Item = ToLoadoutItemCommit(slot.Item)
        })
        .ToArray();
}

static AetheriaRuntimeCargoBayLoadoutCommit[] ToCargoBayCommits(
    IReadOnlyList<AetheriaCargoBayLoadout>? cargoBays)
{
    return (cargoBays ?? Array.Empty<AetheriaCargoBayLoadout>())
        .Select(cargoBay => new AetheriaRuntimeCargoBayLoadoutCommit
        {
            Items = ToSlotCommits(cargoBay.Items)
        })
        .ToArray();
}

static AetheriaRuntimeLoadoutItemCommit ToLoadoutItemCommit(AetheriaLoadoutItem? item)
{
    return new AetheriaRuntimeLoadoutItemCommit
    {
        ItemKey = item?.ItemKey ?? "",
        Quality = item?.Quality ?? 0,
        Durability = item?.Durability ?? 0,
        Quantity = item?.Quantity ?? 0,
        Enabled = item?.Enabled ?? false,
        OverrideShutdown = item?.OverrideShutdown ?? false
    };
}

static AetheriaRuntimeLoadoutTemplateSnapshot ToLoadoutTemplateSnapshot(AetheriaLoadoutTemplate template)
{
    return new AetheriaRuntimeLoadoutTemplateSnapshot(
        template.Name ?? "",
        template.OwnerPlayerKey ?? "",
        ToEntityLoadoutSnapshot(template.RootEntity),
        template.CreatedAtUtc ?? "",
        template.UpdatedAtUtc ?? "");
}

static AetheriaRuntimeEntityLoadoutSnapshot ToEntityLoadoutSnapshot(AetheriaEntityLoadout? entity)
{
    entity ??= new AetheriaEntityLoadout();
    return new AetheriaRuntimeEntityLoadoutSnapshot(
        entity.Name ?? "",
        entity.Kind ?? "",
        entity.FactionKey ?? "",
        ToLoadoutItemSnapshot(entity.Hull),
        ToSlotSnapshots(entity.Equipment),
        ToSlotSnapshots(entity.CargoBays),
        ToSlotSnapshots(entity.DockingBays),
        ToCargoSnapshots(entity.CargoContents),
        ToCargoSnapshots(entity.DockingBayContents),
        (entity.DockingBayAssignments ?? []).ToArray(),
        (entity.WeaponGroups ?? []).Select(group => (IReadOnlyList<int>)group.ToArray()).ToArray(),
        (entity.Children ?? []).Select(ToEntityLoadoutSnapshot).ToArray());
}

static AetheriaRuntimeLoadoutItemSnapshot ToLoadoutItemSnapshot(AetheriaLoadoutItem? item)
{
    item ??= new AetheriaLoadoutItem();
    return new AetheriaRuntimeLoadoutItemSnapshot(
        item.ItemKey ?? "",
        item.Quality,
        item.Durability,
        item.Quantity,
        item.Enabled,
        item.OverrideShutdown,
        item.Temperature);
}

static AetheriaRuntimeLoadoutItemSlotSnapshot[] ToSlotSnapshots(IEnumerable<AetheriaLoadoutItemSlot>? slots)
{
    return (slots ?? [])
        .Select(slot => new AetheriaRuntimeLoadoutItemSlotSnapshot(
            slot.Position?.X ?? 0,
            slot.Position?.Y ?? 0,
            ToLoadoutItemSnapshot(slot.Item),
            slot.Rotation ?? "None"))
        .ToArray();
}

static AetheriaRuntimeCargoBayLoadoutSnapshot[] ToCargoSnapshots(IEnumerable<AetheriaCargoBayLoadout>? bays)
{
    return (bays ?? [])
        .Select(bay => new AetheriaRuntimeCargoBayLoadoutSnapshot(ToSlotSnapshots(bay.Items)))
        .ToArray();
}

internal sealed record AetheriaPreparedPublication(
    AetheriaRuntimeDaemonTickResult Publication,
    IReadOnlyList<AetheriaYmirZonePersistenceCapture> Physics,
    AetheriaRuntimeCatalogSnapshot Catalog);

internal sealed class AetheriaPlayableWorldDemandState
{
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

    public bool IsActive => !_active.IsEmpty;

    public void Observe(CultNetDatabaseSubscriptionDemand demand)
    {
        var key = demand.ConsumerRuntimeId + "\u001f" + demand.SubscriptionId;
        if (!RequestsPlayableWorld(demand))
        {
            _active.TryRemove(key, out _);
            return;
        }

        _active[key] = 0;
    }

    private static bool RequestsPlayableWorld(CultNetDatabaseSubscriptionDemand demand) =>
        demand.Active &&
        (demand.RecordKeys.Any(key =>
             string.Equals(key, AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest.ToString(), StringComparison.Ordinal) ||
             string.Equals(key, AetheriaRuntimeVerseRecordKeys.DaemonGameSurface.ToString(), StringComparison.Ordinal) ||
             string.Equals(key, AetheriaRuntimeVerseRecordKeys.DaemonGameReactiveSurface.ToString(), StringComparison.Ordinal) ||
             string.Equals(key, AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(), StringComparison.Ordinal)) ||
         demand.SchemaIds.Contains(AetheriaRuntimeDaemonSchemas.Frame, StringComparer.Ordinal) ||
         demand.BodyIds.Contains(AetheriaRuntimeDaemonSoaFramePublisher.BodyId, StringComparer.Ordinal));
}

internal sealed class AetheriaManagedViewportDemandState
{
    private readonly ConcurrentDictionary<string, AetheriaManagedViewportDemand> _active =
        new(StringComparer.Ordinal);

    public void Observe(CultNetDatabaseSubscriptionDemand demand)
    {
        var key = demand.ConsumerRuntimeId + "\u001f" + demand.SubscriptionId;
        if (!demand.Active)
        {
            _active.TryRemove(key, out _);
            return;
        }

        if (demand.RecordKeys.Count == 0)
            return;
        _active[key] = new AetheriaManagedViewportDemand(
            demand.RecordKeys.ToArray(),
            demand.SchemaIds.ToArray());
    }

    public IReadOnlyList<AetheriaManagedViewportDemand> Snapshot() =>
        _active.Values
            .GroupBy(
                demand => string.Join("\u001f", demand.RecordKeys) + "\u001e" + string.Join("\u001f", demand.SchemaIds),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
}

internal sealed class AetheriaManagedViewportDemand
{
    public AetheriaManagedViewportDemand(
        IReadOnlyList<string> recordKeys,
        IReadOnlyList<string> schemaIds)
    {
        RecordKeys = recordKeys;
        SchemaIds = schemaIds;
    }

    public IReadOnlyList<string> RecordKeys { get; }
    public IReadOnlyList<string> SchemaIds { get; }
}

internal sealed class AetheriaReactiveSurfacePublicationState
{
    private long? _version;

    public long LastPublishedFrame => _version ?? -1;

    public bool Matches(long version) => _version == version;

    public void Set(long version) => _version = version;
}

internal sealed class AetheriaHotEntityPublicationState
{
    private EveEntitySoaViewDocument? _layout;

    public void Set(EveEntitySoaViewDocument layout) => _layout = layout;

    public bool Matches(EveEntitySoaViewDocument next)
    {
        var current = _layout;
        if (current == null || current.ProviderId != next.ProviderId ||
            current.BodySchemaId != next.BodySchemaId || current.LayoutVersion != next.LayoutVersion ||
            current.ProducerEpoch != next.ProducerEpoch || current.Capacity != next.Capacity ||
            current.Buffers.Length != next.Buffers.Length ||
            current.Columns.Length != next.Columns.Length || current.DirtyRanges.Length != next.DirtyRanges.Length ||
            current.RenderGroups.Length != next.RenderGroups.Length || current.Identities.Length != next.Identities.Length)
            return false;

        for (var i = 0; i < current.Buffers.Length; i++)
        {
            var a = current.Buffers[i];
            var b = next.Buffers[i];
            if (a.BufferId != b.BufferId || a.ByteOffset != b.ByteOffset || a.ByteLength != b.ByteLength)
                return false;
        }
        for (var i = 0; i < current.Columns.Length; i++)
        {
            var a = current.Columns[i];
            var b = next.Columns[i];
            if (a.ColumnId != b.ColumnId || a.Semantic != b.Semantic || a.BufferId != b.BufferId ||
                a.ScalarType != b.ScalarType || a.ByteOffset != b.ByteOffset ||
                a.ElementStride != b.ElementStride || a.ElementCount != b.ElementCount ||
                a.Unit != b.Unit || a.CoordinateSpace != b.CoordinateSpace)
                return false;
        }
        for (var i = 0; i < current.DirtyRanges.Length; i++)
        {
            var a = current.DirtyRanges[i];
            var b = next.DirtyRanges[i];
            if (a.ColumnId != b.ColumnId || a.StartIndex != b.StartIndex || a.Count != b.Count)
                return false;
        }
        for (var i = 0; i < current.RenderGroups.Length; i++)
        {
            var a = current.RenderGroups[i];
            var b = next.RenderGroups[i];
            if (a.GroupId != b.GroupId || a.MeshAssetRef != b.MeshAssetRef ||
                a.MaterialAssetRef != b.MaterialAssetRef || a.SubMeshIndex != b.SubMeshIndex ||
                a.Layer != b.Layer || a.InstanceCount != b.InstanceCount || a.DefaultScale != b.DefaultScale ||
                a.BoundsCenterX != b.BoundsCenterX || a.BoundsCenterY != b.BoundsCenterY ||
                a.BoundsCenterZ != b.BoundsCenterZ || a.BoundsSizeX != b.BoundsSizeX ||
                a.BoundsSizeY != b.BoundsSizeY || a.BoundsSizeZ != b.BoundsSizeZ ||
                a.ShadowMode != b.ShadowMode || a.ReceiveShadows != b.ReceiveShadows || a.Lod != b.Lod)
                return false;
        }
        for (var i = 0; i < current.Identities.Length; i++)
        {
            var a = current.Identities[i];
            var b = next.Identities[i];
            if (a.Index != b.Index || a.EntityId != b.EntityId || a.EntityKind != b.EntityKind ||
                a.Label != b.Label || a.Faction != b.Faction || a.Selectable != b.Selectable ||
                a.Controllable != b.Controllable || a.AssetRef != b.AssetRef)
                return false;
        }
        return true;
    }
}

internal sealed class AetheriaDaemonIngressState
{
    public bool ControlPlaneInitialized { get; set; }
    public string GameMode { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string RunRecordKey { get; set; } = "";
    public double RequestedSimulationRate { get; set; }
    public double SimulationRate { get; set; }
    public double SimulationStepAccumulator { get; set; }
    public AetheriaRuntimeLoadoutTemplateCommit[] LoadoutTemplates { get; set; } = [];
    public AetheriaRuntimeCatalogSnapshot? Catalog { get; set; }
    public AetheriaRuntimeVerseAuthorityPolicyDocument? AuthorityPolicy { get; set; }
    public AetheriaRuntimeStarbridgeScenarioDocument? StarbridgeScenario { get; set; }
    public AetheriaRuntimeStarbridgeSessionDocument? StarbridgeSession { get; set; }
    public AetheriaRuntimeAuthorityLeaseDocument[] AuthorityLeases { get; set; } = [];

    public int TakeTerminusSimulationSteps()
    {
        SimulationStepAccumulator += Math.Max(0, SimulationRate);
        var steps = (int)Math.Floor(SimulationStepAccumulator);
        SimulationStepAccumulator -= steps;
        return steps;
    }
}

internal sealed class AetheriaUnityBundleArtifactSet
{
    private readonly IReadOnlyDictionary<string, CultMeshCdnArtifactChunk> _chunks;

    public AetheriaUnityBundleArtifactSet(IReadOnlyList<AetheriaUnityBundleArtifact> bundles)
    {
        Bundles = bundles ?? throw new ArgumentNullException(nameof(bundles));
        var chunks = new Dictionary<string, CultMeshCdnArtifactChunk>(StringComparer.Ordinal);
        foreach (var chunk in bundles.SelectMany(bundle => bundle.Artifact.Chunks))
            chunks.TryAdd(CultMeshCdnArtifactChunk.CreateRecordKey(chunk).ToString(), chunk);
        _chunks = chunks;
    }

    public IReadOnlyList<AetheriaUnityBundleArtifact> Bundles { get; }

    public CultMeshCdnArtifactChunk? ResolveChunk(string hash)
    {
        var key = CultMeshCdnArtifactChunk.CreateRecordKey(hash).ToString();
        return _chunks.TryGetValue(key, out var chunk) ? chunk : null;
    }
}

internal sealed class AetheriaUnityBundleArtifact
{
    public AetheriaUnityBundleArtifact(
        string path,
        string platform,
        string bundleName,
        IReadOnlyList<string> assets,
        IReadOnlyList<string> dependencies,
        CultMeshCdnArtifact artifact)
    {
        Path = path;
        Platform = platform;
        BundleName = bundleName;
        Assets = assets;
        Dependencies = dependencies;
        Artifact = artifact;
    }

    public string Path { get; }
    public string Platform { get; }
    public string BundleName { get; }
    public IReadOnlyList<string> Assets { get; }
    public IReadOnlyList<string> Dependencies { get; }
    public CultMeshCdnArtifact Artifact { get; }
}

internal sealed class AetheriaClientCultMeshHost : IAsyncDisposable
{
    public AetheriaClientCultMeshHost(
        ICultNetSchemaServer protocol,
        CultNetSchemaServerGroup? protocolGroup,
        CultMeshSessionIdentityServer sessionIdentity,
        TcpFramedCultNetSchemaServer control,
        CultNetWebSocketSchemaServer? browser,
        WebApplication? browserApp,
        CultMeshTcpContentServer content,
        CultMeshQuicRealtimeServer realtime,
        X509Certificate2 realtimeCertificate,
        ECDsa? providerSigningKey,
        string controlEndpoint,
        string browserEndpoint,
        string contentEndpoint,
        string realtimeEndpoint)
    {
        Protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        ProtocolGroup = protocolGroup;
        SessionIdentity = sessionIdentity ?? throw new ArgumentNullException(nameof(sessionIdentity));
        Control = control ?? throw new ArgumentNullException(nameof(control));
        Browser = browser;
        BrowserApp = browserApp;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Realtime = realtime ?? throw new ArgumentNullException(nameof(realtime));
        RealtimeCertificate = realtimeCertificate ?? throw new ArgumentNullException(nameof(realtimeCertificate));
        ProviderSigningKey = providerSigningKey;
        ControlEndpoint = controlEndpoint ?? throw new ArgumentNullException(nameof(controlEndpoint));
        BrowserEndpoint = browserEndpoint ?? "";
        ContentEndpoint = contentEndpoint ?? throw new ArgumentNullException(nameof(contentEndpoint));
        RealtimeEndpoint = realtimeEndpoint ?? throw new ArgumentNullException(nameof(realtimeEndpoint));
    }

    public ICultNetSchemaServer Protocol { get; }
    private CultNetSchemaServerGroup? ProtocolGroup { get; }
    private CultMeshSessionIdentityServer SessionIdentity { get; }
    public TcpFramedCultNetSchemaServer Control { get; }
    public CultNetWebSocketSchemaServer? Browser { get; }
    private WebApplication? BrowserApp { get; }
    public CultMeshTcpContentServer Content { get; }
    public CultMeshQuicRealtimeServer Realtime { get; }
    private X509Certificate2 RealtimeCertificate { get; }
    private ECDsa? ProviderSigningKey { get; }
    public string ControlEndpoint { get; }
    public string BrowserEndpoint { get; }
    public string ContentEndpoint { get; }
    public string RealtimeEndpoint { get; }

    public int ControlPeerCount => Control.PeerCount + (Browser?.PeerCount ?? 0);

    public async ValueTask DisposeAsync()
    {
        SessionIdentity.Dispose();
        ProtocolGroup?.Dispose();
        if (BrowserApp != null)
        {
            await BrowserApp.StopAsync().ConfigureAwait(false);
            await BrowserApp.DisposeAsync().ConfigureAwait(false);
        }
        if (Browser != null) await Browser.DisposeAsync().ConfigureAwait(false);
        await Realtime.DisposeAsync().ConfigureAwait(false);
        RealtimeCertificate.Dispose();
        ProviderSigningKey?.Dispose();
        Content.Dispose();
        Control.Dispose();
    }
}

internal sealed class AetheriaDaemonHostOptions
{
    public const string OdinDiscoveryEndpointsEnvironmentVariable = "AETHERIA_ODIN_DISCOVERY_ENDPOINTS";
    public string StatePath { get; init; } = "";
    public string DaemonId { get; init; } = "aetheria-daemon";
    public string SessionId { get; init; } = "local";
    public string VerseId { get; init; } = "aetheria.local";
    public string CultMeshAddress { get; init; } = "cultmesh://aetheria.local/eve/providers/aetheria.daemon";
    public string ClientCultMeshHost { get; init; } = "127.0.0.1";
    public string ClientCultMeshAdvertiseHost { get; init; } = "127.0.0.1";
    public int ClientCultMeshPort { get; init; } = 3076;
    public int ClientCultMeshWebSocketPort { get; init; } = 0;
    public int ClientCultMeshContentPort { get; init; }
    public int ClientCultMeshQuicPort { get; init; }
    public string ClientCultMeshCertificatePath { get; init; } = "";
    public string ClientCultMeshCertificatePassword { get; init; } = "";
    public bool ClientCultMeshCertificateWasExplicit { get; init; }
    public string ProviderSigningKeyPath { get; init; } = "";
    public string ProviderKeyId { get; init; } = "";
    public string AuthorityRouteGrantPath { get; init; } = "";
    public string AetheriaResourcesRoot { get; init; } = "";
    public string AssetBundleRoot { get; init; } = "";
    public string OdinCultMeshUri { get; init; } = "";
    public bool EnableOdinAnnouncements { get; init; }
    public IReadOnlyList<string> OdinDiscoveryEndpoints { get; init; } = Array.Empty<string>();
    public CultMeshAuthorityTrustPolicy ProgressionTrust { get; init; } = new(
        CultMeshAuthorityTrustMode.AuthenticatedRemote);
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(20);
    public TimeSpan ApiPublicationInterval { get; init; } = TimeSpan.FromSeconds(1);
    public double FixedDeltaSeconds { get; init; } = 0.02;
    public AetheriaRuntimeDaemonRenderSettings RenderSettings { get; init; } =
        AetheriaRuntimeDaemonRenderSettings.AetheriaDefault;
    public AetheriaRuntimeDaemonSimulationSettings SimulationSettings { get; init; } =
        AetheriaRuntimeDaemonSimulationSettings.AetheriaDefault;
    public string TerminusScenario { get; init; } = AetheriaDaemonTerminusScenarios.Standard;
    public bool UseTerminusFixture { get; init; }
    public bool Once { get; init; }

    public static AetheriaDaemonHostOptions Parse(IReadOnlyList<string> args)
    {
        var root = ReadOption(args, "--root");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        var state = ReadOption(args, "--state");
        var intervalMs = ReadPositiveInt(args, "--tick-interval-ms") ?? 20;
        var fixedDeltaMs = ReadPositiveInt(args, "--fixed-delta-ms") ?? 20;
        var daemonId = ReadOption(args, "--daemon-id");
        var verseId = ReadOption(args, "--verse-id");
        var cultMeshAddress = ReadOption(args, "--cultmesh-address");
        var clientCultMeshHost = ReadOption(args, "--client-cultmesh-host");
        var resolvedClientCultMeshHost =
            string.IsNullOrWhiteSpace(clientCultMeshHost) ? "127.0.0.1" : clientCultMeshHost;
        var clientCultMeshAdvertiseHost = ReadOption(args, "--client-cultmesh-advertise-host");
        var clientCultMeshPort = ReadNonNegativeInt(args, "--client-cultmesh-port") ?? 3076;
        var clientCultMeshWebSocketPortOption = ReadOption(args, "--client-cultmesh-websocket-port");
        var clientCultMeshWebSocketPort = string.IsNullOrWhiteSpace(clientCultMeshWebSocketPortOption)
            ? (IsLoopbackHost(resolvedClientCultMeshHost) ? 0 : -1)
            : int.TryParse(clientCultMeshWebSocketPortOption, out var parsedWebSocketPort) && parsedWebSocketPort >= 0
                ? parsedWebSocketPort
                : throw new InvalidOperationException("--client-cultmesh-websocket-port must be zero or a positive port.");
        var clientCultMeshContentPort = ReadNonNegativeInt(args, "--client-cultmesh-content-port") ?? 0;
        var clientCultMeshQuicPort = ReadNonNegativeInt(args, "--client-cultmesh-quic-port") ?? 0;
        var clientCultMeshCertificatePath = ReadOption(args, "--client-cultmesh-certificate-path");
        var clientCultMeshCertificatePassword = Environment.GetEnvironmentVariable("AETHERIA_CLIENT_TLS_CERTIFICATE_PASSWORD") ?? "";
        var providerSigningKeyPath = ReadOption(args, "--provider-signing-key-pem");
        var providerKeyId = ReadOption(args, "--provider-key-id");
        var authorityRouteGrantPath = ReadOption(args, "--authority-route-grant");
        var aetheriaResourcesRoot = ReadOption(args, "--aetheria-resources-root");
        var assetBundleRoot = ReadOption(args, "--asset-bundle-root");
        RejectRemovedOption(args, "--rts-cultmesh-port", "--client-cultmesh-port");
        RejectRemovedOption(args, "--peer-cultmesh-endpoint", "Pilot candidate transport through Commander selection");
        RejectRemovedOption(args, "--peer-sync-timeout-ms", "Pilot candidate transport through Commander selection");
        RejectRemovedOption(args, "--odin-cultmesh-rudp", "--odin-cultmesh-uri");
        RejectRemovedOption(args, "--odin-cultnet-rudp", "--odin-cultmesh-uri");
        var odinCultMeshUri = ReadOption(args, "--odin-cultmesh-uri");
        var odinDiscoveryEndpoints = ReadOptions(args, "--odin-discovery-endpoint")
            .Concat((Environment.GetEnvironmentVariable(OdinDiscoveryEndpointsEnvironmentVariable) ?? "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Select(endpoint => endpoint.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var odinRoots = ReadOptions(args, "--odin-root-p256")
            .Concat((Environment.GetEnvironmentVariable("AETHERIA_ODIN_ROOT_P256") ?? "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(ParseOdinRoot)
            .GroupBy(root => root.KeyId, StringComparer.Ordinal)
            .Select(group => group.Single())
            .ToArray();
        var progressionTrust = odinDiscoveryEndpoints.Length > 0 &&
            odinDiscoveryEndpoints.All(IsLoopbackEndpoint) && odinRoots.Length == 0
                ? new CultMeshAuthorityTrustPolicy(CultMeshAuthorityTrustMode.LocalDevelopment)
                : new CultMeshAuthorityTrustPolicy(CultMeshAuthorityTrustMode.AuthenticatedRemote, odinRoots);
        var noOdinAnnouncements = HasFlag(args, "--no-odin-announcements");
        var apiPublicationIntervalMs = ReadPositiveInt(args, "--api-publication-interval-ms") ?? 1000;
        var sessionId = ReadOption(args, "--session-id");
        var requestedTerminusScenario = ReadOption(args, "--terminus-scenario");
        var terminusScenario = AetheriaDaemonTerminusScenarios.Parse(requestedTerminusScenario);

        var resolvedStatePath = string.IsNullOrWhiteSpace(state)
            ? AetheriaStatePaths.ResolveDefaultStatePath(root)
            : Path.GetFullPath(state);
        var certificateHost = string.IsNullOrWhiteSpace(clientCultMeshAdvertiseHost)
            ? (string.IsNullOrWhiteSpace(clientCultMeshHost) || clientCultMeshHost == "0.0.0.0" || clientCultMeshHost == "*" ? "127.0.0.1" : clientCultMeshHost)
            : clientCultMeshAdvertiseHost;
        var certificateHostToken = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(certificateHost))).Substring(0, 12).ToLowerInvariant();

        return new AetheriaDaemonHostOptions
        {
            StatePath = resolvedStatePath,
            DaemonId = string.IsNullOrWhiteSpace(daemonId) ? "aetheria-daemon" : daemonId,
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? "local" : sessionId,
            VerseId = string.IsNullOrWhiteSpace(verseId) ? "aetheria.local" : verseId,
            CultMeshAddress = string.IsNullOrWhiteSpace(cultMeshAddress)
                ? "cultmesh://aetheria.local/eve/providers/aetheria.daemon"
                : cultMeshAddress,
            ClientCultMeshHost = resolvedClientCultMeshHost,
            ClientCultMeshAdvertiseHost = string.IsNullOrWhiteSpace(clientCultMeshAdvertiseHost)
                ? (string.IsNullOrWhiteSpace(clientCultMeshHost) || clientCultMeshHost == "0.0.0.0" || clientCultMeshHost == "*" ? "127.0.0.1" : clientCultMeshHost)
                : clientCultMeshAdvertiseHost,
            ClientCultMeshPort = clientCultMeshPort,
            ClientCultMeshWebSocketPort = clientCultMeshWebSocketPort,
            ClientCultMeshContentPort = clientCultMeshContentPort,
            ClientCultMeshQuicPort = clientCultMeshQuicPort,
            ClientCultMeshCertificatePath = string.IsNullOrWhiteSpace(clientCultMeshCertificatePath)
                ? resolvedStatePath + $".client-quic-{certificateHostToken}.pfx"
                : Path.GetFullPath(clientCultMeshCertificatePath),
            ClientCultMeshCertificatePassword = clientCultMeshCertificatePassword,
            ClientCultMeshCertificateWasExplicit = !string.IsNullOrWhiteSpace(clientCultMeshCertificatePath),
            ProviderSigningKeyPath = string.IsNullOrWhiteSpace(providerSigningKeyPath) ? "" : Path.GetFullPath(providerSigningKeyPath),
            ProviderKeyId = providerKeyId,
            AuthorityRouteGrantPath = string.IsNullOrWhiteSpace(authorityRouteGrantPath)
                ? ""
                : Path.GetFullPath(authorityRouteGrantPath),
            AetheriaResourcesRoot = string.IsNullOrWhiteSpace(aetheriaResourcesRoot)
                ? Path.GetFullPath(Path.Combine(root, "Aetheria.Assets.Unity", "Assets", "Resources"))
                : Path.GetFullPath(aetheriaResourcesRoot),
            AssetBundleRoot = string.IsNullOrWhiteSpace(assetBundleRoot)
                ? Path.GetFullPath(Path.Combine(root, "Aetheria.Assets.Unity", "Build", "EveAssets"))
                : Path.GetFullPath(assetBundleRoot),
            OdinCultMeshUri = noOdinAnnouncements ? "" : odinCultMeshUri,
            EnableOdinAnnouncements = !noOdinAnnouncements && !string.IsNullOrWhiteSpace(odinCultMeshUri),
            OdinDiscoveryEndpoints = odinDiscoveryEndpoints,
            ProgressionTrust = progressionTrust,
            TickInterval = TimeSpan.FromMilliseconds(intervalMs),
            ApiPublicationInterval = TimeSpan.FromMilliseconds(apiPublicationIntervalMs),
            FixedDeltaSeconds = fixedDeltaMs / 1000.0,
            TerminusScenario = terminusScenario,
            UseTerminusFixture = !string.IsNullOrWhiteSpace(requestedTerminusScenario),
            Once = HasFlag(args, "--once")
        };
    }

    private static string ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return "";
    }

    private static IReadOnlyList<string> ReadOptions(IReadOnlyList<string> args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                values.Add(args[i + 1].Trim());
            }
        }

        return values
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void RejectRemovedOption(IReadOnlyList<string> args, string removed, string replacement)
    {
        if (args.Any(arg => string.Equals(arg, removed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{removed} was removed. Use {replacement} with a cultmesh:// Odin route.");
        }
    }

    private static int? ReadPositiveInt(IReadOnlyList<string> args, string name)
    {
        return int.TryParse(ReadOption(args, name), out var value) && value > 0
            ? value
            : null;
    }

    private static int? ReadNonNegativeInt(IReadOnlyList<string> args, string name)
    {
        return int.TryParse(ReadOption(args, name), out var value) && value >= 0
            ? value
            : null;
    }

    private static bool HasFlag(IReadOnlyList<string> args, string name)
    {
        return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsLoopbackEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || IsLoopbackHost(uri.Host));

    private static CultMeshEcdsaP256PublicKey ParseOdinRoot(string value)
    {
        var parts = (value ?? "").Trim().Split(':');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                "--odin-root-p256 must be '<key-id>:<base64-x>:<base64-y>' using standard padded Base64 coordinates.");
        return new CultMeshEcdsaP256PublicKey(parts[0], parts[1], parts[2]);
    }
}

public static class AetheriaHangarCommandJournal
{
    public static async Task<string> AdmitAsync(
        AetheriaStateNode node,
        CultRecordKey requestRecordKey,
        EveSurfaceCommandRequest request,
        string now)
    {
        return await node.CommitAsync(async () =>
        {
            var payloadHash = PayloadHash(request);
            var envelopeKey = AetheriaRuntimeVerseRecordKeys.HangarCommandEnvelope(request.CommandId);
            var envelope = await node.MutableDocument<AetheriaHangarCommandEnvelopeDocument>(envelopeKey)
                .ReadAsync().ConfigureAwait(false);
            ValidateEnvelope(envelope, request, payloadHash);

            var pending = await node.MutableDocument<EveSurfaceCommandRequest>(requestRecordKey)
                .ReadAsync().ConfigureAwait(false);
            if (pending != null && !string.Equals(PayloadHash(pending), payloadHash, StringComparison.Ordinal))
                throw Collision(request.CommandId);

            if (envelope == null)
            {
                await node.MutableDocument<AetheriaHangarCommandEnvelopeDocument>(envelopeKey)
                    .ReplaceAsync(NewEnvelope(request, payloadHash, now)).ConfigureAwait(false);
            }
            if (pending == null)
                await node.Database.PutAsync(requestRecordKey, request).ConfigureAwait(false);
            return payloadHash;
        }).ConfigureAwait(false);
    }

    public static async Task<string> ValidateAsync(
        AetheriaStateNode node,
        EveSurfaceCommandRequest request,
        string now)
    {
        return await node.CommitAsync(async () =>
        {
            var payloadHash = PayloadHash(request);
            var envelopeKey = AetheriaRuntimeVerseRecordKeys.HangarCommandEnvelope(request.CommandId);
            var envelope = await node.MutableDocument<AetheriaHangarCommandEnvelopeDocument>(envelopeKey)
                .ReadAsync().ConfigureAwait(false);
            ValidateEnvelope(envelope, request, payloadHash);
            if (envelope == null)
            {
                await node.MutableDocument<AetheriaHangarCommandEnvelopeDocument>(envelopeKey)
                    .ReplaceAsync(NewEnvelope(request, payloadHash, now)).ConfigureAwait(false);
            }
            return payloadHash;
        }).ConfigureAwait(false);
    }

    public static string PayloadHash(EveSurfaceCommandRequest request)
    {
        var canonical = new StringBuilder();
        Append(request.Schema);
        Append(request.ProviderId);
        Append(request.SurfaceId);
        Append(request.Command);
        Append(request.ClientId);
        Append(request.CommandBoundary);
        Append(request.ReceiptSchema);
        foreach (var field in request.PayloadFields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(field.Key);
            Append(field.Value);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();

        void Append(string? value)
        {
            value ??= "";
            canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            canonical.Append(':');
            canonical.Append(value);
        }
    }

    private static AetheriaHangarCommandEnvelopeDocument NewEnvelope(
        EveSurfaceCommandRequest request,
        string payloadHash,
        string now) =>
        new()
        {
            CommandId = request.CommandId,
            PayloadHash = payloadHash,
            ClientId = request.ClientId,
            CreatedAtUtc = now ?? ""
        };

    private static void ValidateEnvelope(
        AetheriaHangarCommandEnvelopeDocument? envelope,
        EveSurfaceCommandRequest request,
        string payloadHash)
    {
        if (envelope != null &&
            (!string.Equals(envelope.PayloadHash, payloadHash, StringComparison.Ordinal) ||
             !string.Equals(envelope.ClientId, request.ClientId, StringComparison.Ordinal)))
            throw Collision(request.CommandId);
    }

    private static InvalidOperationException Collision(string commandId) =>
        new($"Hangar command id '{commandId}' was reused with a different immutable envelope.");
}
