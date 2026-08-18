using Aetheria.State;
using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

try
{
var root = Directory.GetCurrentDirectory();
var seed = Path.Combine(root, "Aetheria.Unity", "Build", "aetheria-unity.cc");
if (!File.Exists(seed) || !Directory.Exists(seed + ".records"))
    throw new InvalidOperationException("Build/import Aetheria.Unity/Build/aetheria-unity.cc before running the progression Verse smoke.");

var smokeRoot = Path.Combine(Path.GetTempPath(), "aetheria-progression-verse-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(smokeRoot);
Console.WriteLine($"Progression smoke state: {smokeRoot}");
var localState = Path.Combine(smokeRoot, "local.cc");
var remoteState = Path.Combine(smokeRoot, "remote.cc");
CopyState(seed, localState);
CopyState(seed, remoteState);

const string localVerse = "aetheria.progression-smoke.local";
const string remoteVerse = "aetheria.progression-smoke.remote";
var localTarget = new CultMeshSessionTarget(localVerse, "progression-local");
var remoteTarget = new CultMeshSessionTarget(remoteVerse, "progression-remote");
var localPort = FreeTcpPort();
var remotePort = FreeTcpPort();
var localEndpoint = $"cultnet+tcp://127.0.0.1:{localPort}";
var remoteEndpoint = $"cultnet+tcp://127.0.0.1:{remotePort}";
Process? local = null;
Process? remote = null;
try
{
    remote = StartDaemon(root, remoteState, "progression-remote", remoteVerse, remotePort);
    await WaitForEndpointAsync(remote, remotePort, TimeSpan.FromSeconds(30));
    await WaitForVerseAdvertisementAsync(
        remoteEndpoint,
        remoteVerse,
        "progression-remote",
        TimeSpan.FromSeconds(45));
    local = StartDaemon(root, localState, "progression-local", localVerse, localPort,
        "--odin-discovery-endpoint", remoteEndpoint);
    await WaitForEndpointAsync(local, localPort, TimeSpan.FromSeconds(30));

    using (var client = Client(localEndpoint))
    using (var remoteClient = Client(remoteEndpoint))
    {
        var initialSource = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaStateNode.ProgressionSourceKey.ToString(),
            (AetheriaProgressionSourceDocument source) =>
                source.UsesLocalProgression &&
                source.AvailableVerses.Any(option => option.VerseId == remoteVerse),
            TimeSpan.FromSeconds(45));
        Require(initialSource.AvailableVerses.First().VerseId == AetheriaProgressionSources.Local,
            "Local must remain the first Hangar Verse option.");

        var initialSurface = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(),
            (EveSurfaceDocument surface) =>
                Find(surface.Surface.Root, "aetheria.hangar.verse").Children
                    .Any(option => option.Props["value"] == remoteVerse),
            TimeSpan.FromSeconds(10));
        var verseSelect = Find(initialSurface.Surface.Root, "aetheria.hangar.verse");
        Require(verseSelect.Kind == "control.select" &&
                verseSelect.Props["value"] == AetheriaProgressionSources.Local &&
                verseSelect.Children.Any(option => option.Props["value"] == remoteVerse),
            "The daemon-published Hangar surface must expose Local plus Odin-discovered Verses.");

        await SubmitAsync(
            client,
            localTarget,
            initialSurface,
            AetheriaRuntimeHangarCommands.SelectVerse,
            new Dictionary<string, string> { ["value"] = remoteVerse });
        await ReadUntilAsync(
            client,
            localTarget,
            AetheriaStateNode.ProgressionSourceKey.ToString(),
            (AetheriaProgressionSourceDocument source) =>
                source.SelectedVerseId == remoteVerse && source.Status == AetheriaProgressionSourceStatuses.Ready,
            TimeSpan.FromSeconds(10));

        var remoteHangar = await remoteClient.ReadAsync<AetheriaHangarState>(
            remoteTarget,
            AetheriaStateNode.HangarKey.ToString());
        var remoteRevision = remoteHangar.Revision;
        await RequireRawProgressionPutRejectedAsync(
            remoteClient,
            remoteTarget,
            AetheriaStateNode.HangarKey.ToString(),
            remoteHangar);
        var hangarAfterRejectedPut = await remoteClient.ReadAsync<AetheriaHangarState>(
            remoteTarget,
            AetheriaStateNode.HangarKey.ToString());
        Require(hangarAfterRejectedPut.Revision == remoteRevision,
            "A public CultMesh peer must not mutate Hangar state through a generic raw document put.");
        var remoteSurface = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(),
            (EveSurfaceDocument surface) =>
                Find(surface.Surface.Root, "aetheria.hangar.verse").Props["value"] == remoteVerse &&
                Find(surface.Surface.Root, "aetheria.hangar.loadout.grid").Children.Count > 0,
            TimeSpan.FromSeconds(10));
        var loadoutGrid = Find(remoteSurface.Surface.Root, "aetheria.hangar.loadout.grid");
        var inventoryGrid = Find(remoteSurface.Surface.Root, "aetheria.hangar.inventory.grid");
        var remove = loadoutGrid.Children.First();
        var removedItemKey = remove.Props["itemKey"];
        var originalX = int.Parse(remove.Props["x"]);
        var originalY = int.Parse(remove.Props["y"]);
        Require(EveInventoryInteraction.TryCreateDropRequest(
                remoteSurface,
                remove,
                inventoryGrid,
                0,
                0,
                "progression-verse-smoke",
                out var removeRequest),
            "The Hangar Eve surface must translate loadout-to-storage drag into a typed remove operation.");
        var removeReceipt = await SubmitRequestAsync(client, localTarget, removeRequest!);
        Require(removeReceipt.State == "accepted",
            "The remote progression Verse must accept the Eve loadout-to-storage drop.");
        var updatedRemoteHangar = await ReadUntilAsync(
            remoteClient,
            remoteTarget,
            AetheriaStateNode.HangarKey.ToString(),
            (AetheriaHangarState hangar) => hangar.Revision > remoteRevision,
            TimeSpan.FromSeconds(10));

        var refitSurface = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(),
            (EveSurfaceDocument surface) =>
                Find(surface.Surface.Root, "aetheria.hangar.inventory.grid").Children
                    .Any(item => item.Props["itemKey"] == removedItemKey) &&
                Find(surface.Surface.Root, "aetheria.hangar.loadout.grid").Props["payload.expectedHangarRevision"] == updatedRemoteHangar.Revision.ToString(),
            TimeSpan.FromSeconds(10));
        var refitInventory = Find(refitSurface.Surface.Root, "aetheria.hangar.inventory.grid");
        var refitLoadout = Find(refitSurface.Surface.Root, "aetheria.hangar.loadout.grid");
        var equip = refitInventory.Children.First(item => item.Props["itemKey"] == removedItemKey);
        Require(EveInventoryInteraction.TryCreateDropRequest(
                refitSurface,
                equip,
                refitLoadout,
                originalX,
                originalY,
                "progression-verse-smoke",
                out var equipRequest),
            "The Hangar Eve surface must translate storage-to-loadout drag into a typed positioned equip operation.");
        var equipReceipt = await SubmitRequestAsync(client, localTarget, equipRequest!);
        Require(equipReceipt.State == "accepted",
            "The remote progression Verse must accept the Eve storage-to-loadout drop at the requested cells.");
        updatedRemoteHangar = await ReadUntilAsync(
            remoteClient,
            remoteTarget,
            AetheriaStateNode.HangarKey.ToString(),
            (AetheriaHangarState hangar) => hangar.Revision > updatedRemoteHangar.Revision,
            TimeSpan.FromSeconds(10));

        var launchSurface = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(),
            (EveSurfaceDocument surface) =>
                Find(surface.Surface.Root, "aetheria.hangar.launch").Props["disabled"] == "false" &&
                Find(surface.Surface.Root, "aetheria.hangar.launch").Props["expectedHangarRevision"] == updatedRemoteHangar.Revision.ToString(),
            TimeSpan.FromSeconds(10));
        var launch = Find(launchSurface.Surface.Root, "aetheria.hangar.launch");
        var launchReceipt = await SubmitAsync(
            client,
            localTarget,
            launchSurface,
            AetheriaRuntimeHangarCommands.Launch,
            new Dictionary<string, string>(launch.Props, StringComparer.Ordinal));
        var launchNavigation = launchReceipt.Navigation;
        Require(launchReceipt.State == "accepted" && launchNavigation?.VerseId == remoteVerse,
            $"Remote Terminus launch must return an accepted Eve navigation target for the selected Verse; state='{launchReceipt.State}', message='{launchReceipt.Message}', navigation='{launchNavigation?.VerseId ?? "<none>"}'.");
        Require(launchNavigation!.SurfaceId == AetheriaRuntimeDaemonGameSurfaceBuilder.PilotSurfaceId,
            "Remote Terminus launch must navigate the generic client to the Pilot surface.");
        Require(launchNavigation.RendezvousEndpoints.SequenceEqual(new[] { remoteEndpoint }, StringComparer.Ordinal),
            "Remote Terminus navigation must carry only the configured Odin rendezvous route, not flattened content or realtime provider routes.");
        using (var navigatedClient = Client(launchNavigation.RendezvousEndpoints[0]))
        {
            using var gameSurfaceDemand = await navigatedClient.LeaseDocumentAsync<EveSurfaceDocument>(
                remoteTarget,
                AetheriaRuntimeVerseRecordKeys.DaemonGameSurface.ToString());
            var gameSurface = await ReadLiveUntilAsync(
                gameSurfaceDemand.Handle,
                surface => string.Equals(surface.Surface.Id, AetheriaRuntimeDaemonGameSurfaceBuilder.PilotSurfaceId, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));
            Require(
                string.Equals(gameSurface.Surface.Id, AetheriaRuntimeDaemonGameSurfaceBuilder.PilotSurfaceId, StringComparison.Ordinal),
                "Navigating into Terminus must publish the Pilot Eve surface through the live demand subscription.");
            using var gameplayStateClient = Client(remoteEndpoint);
            var deployedHangar = await ReadUntilAsync(
                gameplayStateClient,
                remoteTarget,
                AetheriaStateNode.HangarKey.ToString(),
                (AetheriaHangarState hangar) =>
                    (hangar.Deployments ?? []).Any(deployment => deployment.RequestId == launchReceipt.CommandId),
                TimeSpan.FromSeconds(10));
            var deployment = deployedHangar.Deployments.Single(value => value.RequestId == launchReceipt.CommandId);
            var gameSession = await ReadUntilAsync(
                gameplayStateClient,
                remoteTarget,
                AetheriaStateNode.GameSessionStateKey.ToString(),
                (AetheriaGameSessionState session) =>
                    session.Mode == AetheriaGameSessionState.TerminusMode &&
                    session.RunId == deployment.RunId &&
                    session.RunRecordKey == deployment.RunRecordKey &&
                    session.SimulationRate > 0,
                TimeSpan.FromSeconds(10));
            var liveFrame = await ReadUntilAsync(
                gameplayStateClient,
                remoteTarget,
                AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest.ToString(),
                (AetheriaRuntimeDaemonFrameDocument frame) =>
                    frame.Run?.RunId == deployment.RunId && frame.Run.GameMode == AetheriaGameModes.Terminus,
                TimeSpan.FromSeconds(10));
            var livePlayerLoadout = liveFrame.Run.CreateLoadoutTemplate(liveFrame.Run.CurrentEntityKey).RootEntity;
            Require(gameSession.RunId == liveFrame.Run.RunId &&
                    livePlayerLoadout.Equipment.Select(item => item.Item.ItemKey).SequenceEqual(
                        deployment.Loadout.Equipment.Select(item => item.Item.ItemKey),
                        StringComparer.Ordinal),
                "Accepted launch must make the receipt-owned run and configured loadout the first live daemon frame, not preserve a stale published run.");
        }

        var continueSurface = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(),
            (EveSurfaceDocument surface) =>
                Find(surface.Surface.Root, "aetheria.hangar.continue").Props["disabled"] == "false",
            TimeSpan.FromSeconds(10));
        var resume = Find(continueSurface.Surface.Root, "aetheria.hangar.continue");
        var continueReceipt = await SubmitAsync(
            client,
            localTarget,
            continueSurface,
            AetheriaRuntimeHangarCommands.Continue,
            new Dictionary<string, string>(resume.Props, StringComparer.Ordinal));
        Require(continueReceipt.State == "accepted" && continueReceipt.Navigation?.VerseId == remoteVerse,
            "Remote Terminus continue must return the selected Verse Pilot navigation target.");

        var pinnedLaunchRoute = await client.ReadAsync<AetheriaProgressionCommandRouteDocument>(
            localTarget,
            AetheriaRuntimeVerseRecordKeys.ProgressionCommandRoute(launchReceipt.CommandId).ToString());
        Require(pinnedLaunchRoute != null &&
                pinnedLaunchRoute.VerseId == remoteVerse &&
                pinnedLaunchRoute.AuthorityRuntimeId == remoteTarget.AuthorityRuntimeId,
            "The first remote launch attempt must durably pin its Verse and authority target.");

        await SubmitAsync(
            client,
            localTarget,
            continueSurface,
            AetheriaRuntimeHangarCommands.SelectVerse,
            new Dictionary<string, string> { ["value"] = AetheriaProgressionSources.Local });
        await ReadUntilAsync(
            client,
            localTarget,
            AetheriaStateNode.ProgressionSourceKey.ToString(),
            (AetheriaProgressionSourceDocument source) => source.UsesLocalProgression,
            TimeSpan.FromSeconds(10));
        var routeAfterSelectionChange = await client.ReadAsync<AetheriaProgressionCommandRouteDocument>(
            localTarget,
            AetheriaRuntimeVerseRecordKeys.ProgressionCommandRoute(launchReceipt.CommandId).ToString());
        Require(routeAfterSelectionChange != null &&
                routeAfterSelectionChange.VerseId == pinnedLaunchRoute!.VerseId &&
                routeAfterSelectionChange.AuthorityRuntimeId == pinnedLaunchRoute.AuthorityRuntimeId &&
                routeAfterSelectionChange.PayloadHash == pinnedLaunchRoute.PayloadHash,
            "Changing the Verse dropdown must not retarget an existing command envelope.");

        var localSurface = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(),
            (EveSurfaceDocument surface) =>
                Find(surface.Surface.Root, "aetheria.hangar.verse").Props["value"] == AetheriaProgressionSources.Local,
            TimeSpan.FromSeconds(10));
        await SubmitAsync(
            client,
            localTarget,
            localSurface,
            AetheriaRuntimeHangarCommands.SelectVerse,
            new Dictionary<string, string> { ["value"] = remoteVerse });
        await ReadUntilAsync(
            client,
            localTarget,
            AetheriaStateNode.ProgressionSourceKey.ToString(),
            (AetheriaProgressionSourceDocument source) =>
                source.SelectedVerseId == remoteVerse && source.Status == AetheriaProgressionSourceStatuses.Ready,
            TimeSpan.FromSeconds(10));
    }

    Stop(local);
    local = StartDaemon(root, localState, "progression-local", localVerse, localPort,
        "--odin-discovery-endpoint", remoteEndpoint);
    await WaitForEndpointAsync(local, localPort, TimeSpan.FromSeconds(30));
    await WaitForVerseAdvertisementAsync(
        localEndpoint,
        localVerse,
        "progression-local",
        TimeSpan.FromSeconds(45));
    using (var restartedClient = Client(localEndpoint))
    {
        var restored = await ReadUntilAsync(
            restartedClient,
            localTarget,
            AetheriaStateNode.ProgressionSourceKey.ToString(),
            (AetheriaProgressionSourceDocument source) =>
                source.SelectedVerseId == remoteVerse && source.Status == AetheriaProgressionSourceStatuses.Ready,
            TimeSpan.FromSeconds(10));
        Require(restored.SelectedVerseId == remoteVerse,
            "Daemon restart must preserve the selected progression Verse by stable identity.");
    }

    Console.WriteLine("Aetheria Hangar Verse discovery, raw-write rejection, pinned forwarding, remote spatial refit, Terminus launch/continue navigation, and restart smoke passed.");
}
catch
{
    Console.Error.WriteLine($"Local daemon: {ProcessState(local)}; remote daemon: {ProcessState(remote)}.");
    throw;
}
finally
{
    Stop(local);
    Stop(remote);
    if (!string.Equals(Environment.GetEnvironmentVariable("AETHERIA_SMOKE_KEEP_STATE"), "1", StringComparison.Ordinal))
    {
        try { Directory.Delete(smokeRoot, recursive: true); } catch { }
    }
}
}
catch (Exception error)
{
    Console.Error.WriteLine($"Aetheria progression smoke failed cleanly: {error}");
    Environment.ExitCode = 1;
}

static string ProcessState(Process? process)
{
    if (process == null) return "not-started";
    var state = process.HasExited ? $"exited({process.ExitCode})" : $"running(pid={process.Id})";
    if (!process.StartInfo.Environment.TryGetValue("AETHERIA_SMOKE_LOG_PATH", out var logPath) ||
        string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        return state;
    string[] lines;
    try { lines = File.ReadAllLines(logPath); }
    catch { return state; }
    return state + Environment.NewLine + string.Join(Environment.NewLine, lines.TakeLast(20));
}

static CultMeshClient Client(string rendezvousEndpoint) => new(new CultMeshClientOptions
{
    RendezvousEndpoints = new[] { rendezvousEndpoint },
    Sessions = new CultMeshSessionManagerOptions
    {
        RuntimeId = "progression-verse-smoke",
        Trust = new CultMeshAuthorityTrustPolicy(CultMeshAuthorityTrustMode.LocalDevelopment)
    },
    Discovery = new CultMeshVerseDiscoveryClientOptions
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ResponseTimeout = TimeSpan.FromSeconds(2)
    }
});

static async Task<EveCommandReceiptDocument> SubmitAsync(
    CultMeshClient client,
    CultMeshSessionTarget target,
    EveSurfaceDocument surface,
    string command,
    IReadOnlyDictionary<string, string> payload)
{
    var operation = CultMesh.OperationInvocation(
        surface.Commands.Single(template => template.Command == command).Operation,
        idempotencyKey: "progression-verse-smoke-" + Guid.NewGuid().ToString("N"));
    var request = new EveSurfaceCommandRequest(
        surface.ProviderId,
        surface.Surface.Id,
        operation,
        CultMesh.OperationPayload(payload),
        DateTimeOffset.UtcNow,
        "progression-verse-smoke");
    return await SubmitRequestAsync(client, target, request);
}

static async Task<EveCommandReceiptDocument> SubmitRequestAsync(
    CultMeshClient client,
    CultMeshSessionTarget target,
    EveSurfaceCommandRequest request)
{
    string? transportError = null;
    var session = await client.ConnectAsync(target, CultMeshProtocols.Documents);
    using var errorSubscription = session.OnCultNet<CultNetErrorMessage>(error => transportError = error.Error);
    var requestRecordKey = AetheriaRuntimeVerseRecordKeys.EveCommandRecordPrefix + ":" + request.CommandId;
    await client.SubmitDocumentAsync(
        target,
        requestRecordKey,
        request,
        "progression-verse-smoke",
        "headless-smoke");
    EveCommandReceiptDocument receipt;
    try
    {
        receipt = await ReadUntilAsync(
            client,
            target,
            AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(request.CommandId).ToString(),
            (EveCommandReceiptDocument receipt) => receipt.CommandId == request.CommandId,
            TimeSpan.FromSeconds(10));
    }
    catch (Exception error) when (!string.IsNullOrWhiteSpace(transportError))
    {
        throw new InvalidOperationException($"Command ingress failed: {transportError}", error);
    }
    await RequireDeletedAsync<EveSurfaceCommandRequest>(
        client,
        target,
        requestRecordKey,
        TimeSpan.FromSeconds(10));
    return receipt;
}

static async Task RequireRawProgressionPutRejectedAsync<TDocument>(
    CultMeshClient client,
    CultMeshSessionTarget target,
    string recordKey,
    TDocument document)
    where TDocument : class
{
    var rejection = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var session = await client.ConnectAsync(target, CultMeshProtocols.Documents);
    using var subscription = session.OnCultNet<CultNetErrorMessage>(error => rejection.TrySetResult(error.Error));
    await client.SubmitDocumentAsync(
        target,
        recordKey,
        document,
        "progression-verse-smoke",
        "headless-smoke");
    var diagnostic = await rejection.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require(diagnostic.Contains("typed Eve command intents only", StringComparison.Ordinal),
        $"Raw progression put was rejected for the wrong reason: {diagnostic}");
}

static async Task RequireDeletedAsync<T>(
    CultMeshClient client,
    CultMeshSessionTarget target,
    string recordKey,
    TimeSpan timeout)
    where T : class
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            await client.ReadAsync<T>(target, recordKey, TimeSpan.FromMilliseconds(500));
        }
        catch (Exception error) when (error is TimeoutException || error is InvalidOperationException ||
            error is SocketException || error is CultMeshSessionException)
        {
            return;
        }
        await Task.Delay(50);
    }
    throw new InvalidOperationException($"Handled Eve command request '{recordKey}' remained in the transient inbox.");
}

static async Task<T> ReadUntilAsync<T>(
    CultMeshClient client,
    CultMeshSessionTarget target,
    string recordKey,
    Func<T, bool> predicate,
    TimeSpan timeout)
    where T : class
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    Exception? last = null;
    T? lastValue = null;
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            var value = await client.ReadAsync<T>(target, recordKey, TimeSpan.FromMilliseconds(750));
            lastValue = value;
            if (predicate(value)) return value;
        }
        catch (Exception error) when (error is TimeoutException || error is InvalidOperationException ||
            error is SocketException || error is CultMeshSessionException)
        {
            last = error;
        }
        await Task.Delay(50);
    }
    var observed = lastValue is AetheriaProgressionSourceDocument source
        ? $" selected={source.SelectedVerseId} status={source.Status} diagnostic='{source.Diagnostic}' odin=[{string.Join(",", source.OdinDiscoveryEndpoints)}] verses=[{string.Join(",", source.AvailableVerses.Select(option => $"{option.VerseId}({string.Join("|", option.AuthorityRuntimeIds)})"))}]"
        : "";
    throw new TimeoutException($"Timed out reading '{recordKey}' from target '{target}'.{observed}", last);
}

static async Task<T> ReadLiveUntilAsync<T>(
    CultMeshDocumentHandle<T> handle,
    Func<T, bool> predicate,
    TimeSpan timeout)
    where T : class
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    Exception? last = null;
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            var value = await handle.LatestAsync();
            if (predicate(value)) return value;
        }
        catch (KeyNotFoundException error)
        {
            last = error;
        }
        await Task.Delay(50);
    }
    throw new TimeoutException($"Timed out awaiting live CultMesh document '{handle.DocumentId}'.", last);
}

static EveSurfaceComponent Find(EveSurfaceComponent component, string id)
{
    if (component.Id == id) return component;
    foreach (var child in component.Children)
    {
        var found = FindOrNull(child, id);
        if (found != null) return found;
    }
    throw new InvalidOperationException($"Eve surface component '{id}' was not found.");
}

static EveSurfaceComponent? FindOrNull(EveSurfaceComponent component, string id)
{
    if (component.Id == id) return component;
    foreach (var child in component.Children)
    {
        var found = FindOrNull(child, id);
        if (found != null) return found;
    }
    return null;
}

static Process StartDaemon(
    string root,
    string state,
    string daemonId,
    string verseId,
    int port,
    params string[] extra)
{
    var daemon = Path.Combine(root, "Aetheria.State.Daemon", "bin", "Debug", "net10.0", "Aetheria.State.Daemon.dll");
    if (!File.Exists(daemon)) throw new FileNotFoundException("Build the Aetheria daemon before the progression smoke.", daemon);
    var arguments = new List<string>
    {
        Quote(daemon),
        "--root", Quote(root),
        "--state", Quote(state),
        "--daemon-id", daemonId,
        "--verse-id", verseId,
        "--session-id", "progression-smoke",
        "--client-cultmesh-host", "127.0.0.1",
        "--client-cultmesh-advertise-host", "127.0.0.1",
        "--client-cultmesh-port", port.ToString(),
        "--client-cultmesh-content-port", "0",
        "--client-cultmesh-quic-port", "0",
        "--no-odin-announcements"
    };
    var assetBundleRoot = Environment.GetEnvironmentVariable("AETHERIA_SMOKE_ASSET_BUNDLE_ROOT")
        ?? Path.Combine(root, "Build", "EveAssets");
    if (Directory.Exists(assetBundleRoot))
        arguments.AddRange(new[] { "--asset-bundle-root", Quote(assetBundleRoot) });
    arguments.AddRange(extra.Select((value, index) => index % 2 == 1 ? Quote(value) : value));
    var start = new ProcessStartInfo("dotnet", string.Join(" ", arguments))
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    var logPath = state + ".daemon.log";
    start.Environment["AETHERIA_SMOKE_LOG_PATH"] = logPath;
    var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start Aetheria daemon.");
    var logGate = new object();
    void Record(string? line)
    {
        if (line == null) return;
        lock (logGate) File.AppendAllText(logPath, line + Environment.NewLine);
    }
    process.OutputDataReceived += (_, eventArgs) => Record(eventArgs.Data);
    process.ErrorDataReceived += (_, eventArgs) => Record(eventArgs.Data);
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
}

static async Task WaitForEndpointAsync(Process process, int port, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (process.HasExited)
            throw new InvalidOperationException($"Aetheria daemon exited with code {process.ExitCode} before opening port {port}.");
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return;
        }
        catch (SocketException)
        {
            await Task.Delay(100);
        }
    }
    throw new TimeoutException($"Aetheria daemon did not open port {port}.");
}

static async Task WaitForVerseAdvertisementAsync(
    string endpoint,
    string verseId,
    string authorityRuntimeId,
    TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    Exception? last = null;
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            var response = await CultMesh.CreateVerseDiscoveryClient().FetchAsync(
                endpoint,
                new CultMeshVerseCatalogRequestMessage
                {
                    VerseIds = new[] { verseId },
                    TransportVersion = "cultmesh.v0"
                }).ConfigureAwait(false);
            if (response.Verses.Any(candidate =>
                    string.Equals(candidate.VerseId, verseId, StringComparison.Ordinal) &&
                    (candidate.AuthorityRuntimeIds ?? Array.Empty<string>()).Contains(authorityRuntimeId, StringComparer.Ordinal)))
                return;
        }
        catch (Exception error) when (error is IOException || error is SocketException || error is TimeoutException ||
            error is InvalidOperationException)
        {
            last = error;
        }
        await Task.Delay(100).ConfigureAwait(false);
    }
    throw new TimeoutException(
        $"Daemon '{authorityRuntimeId}' did not advertise Verse '{verseId}' through '{endpoint}'.",
        last);
}

static void CopyState(string source, string destination)
{
    File.Copy(source, destination);
    CopyDirectory(source + ".records", destination + ".records");
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    foreach (var directory in Directory.GetDirectories(source))
        CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
}

static int FreeTcpPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static void Stop(Process? process)
{
    if (process == null) return;
    try
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }
    catch { }
    process.Dispose();
}

static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
