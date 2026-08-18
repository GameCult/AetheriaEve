using Aetheria.State;
using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

var root = Directory.GetCurrentDirectory();
var seed = Path.Combine(root, "Aetheria.Unity", "Build", "aetheria-unity.cc");
if (!File.Exists(seed) || !Directory.Exists(seed + ".records"))
    throw new InvalidOperationException("Build/import Aetheria.Unity/Build/aetheria-unity.cc before running the progression Verse smoke.");

var smokeRoot = Path.Combine(Path.GetTempPath(), "aetheria-progression-verse-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(smokeRoot);
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
                Find(surface.Surface.Root, "aetheria.hangar.launch").Props["enabled"] == "true" &&
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
        Require(launchNavigation.RendezvousEndpoints.Contains(remoteEndpoint, StringComparer.Ordinal),
            "Remote Terminus navigation must carry the Odin-discovered rendezvous route, not strand the client on the local daemon.");
        await ReadUntilAsync(
            remoteClient,
            remoteTarget,
            AetheriaStateNode.GameSessionStateKey.ToString(),
            (AetheriaGameSessionState session) =>
                session.Mode == AetheriaGameSessionState.TerminusMode && session.SimulationRate > 0,
            TimeSpan.FromSeconds(10));

        var continueSurface = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(),
            (EveSurfaceDocument surface) =>
                Find(surface.Surface.Root, "aetheria.hangar.continue").Props["enabled"] == "true",
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
    }

    Stop(local);
    local = StartDaemon(root, localState, "progression-local", localVerse, localPort,
        "--odin-discovery-endpoint", remoteEndpoint);
    await WaitForEndpointAsync(local, localPort, TimeSpan.FromSeconds(30));
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

    Console.WriteLine("Aetheria Hangar Verse discovery, remote spatial refit, Terminus launch/continue navigation, and restart smoke passed.");
}
finally
{
    Stop(local);
    Stop(remote);
    try { Directory.Delete(smokeRoot, recursive: true); } catch { }
}

static CultMeshClient Client(string rendezvousEndpoint) => new(new CultMeshClientOptions
{
    RendezvousEndpoints = new[] { rendezvousEndpoint },
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
    var requestRecordKey = AetheriaRuntimeVerseRecordKeys.EveCommandRecordPrefix + ":" + request.CommandId;
    await client.SubmitDocumentAsync(
        target,
        requestRecordKey,
        request,
        "progression-verse-smoke",
        "headless-smoke");
    var receipt = await ReadUntilAsync(
        client,
        target,
        AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(request.CommandId).ToString(),
        (EveCommandReceiptDocument receipt) => receipt.CommandId == request.CommandId,
        TimeSpan.FromSeconds(10));
    await RequireDeletedAsync<EveSurfaceCommandRequest>(
        client,
        target,
        requestRecordKey,
        TimeSpan.FromSeconds(10));
    return receipt;
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
    var daemon = Path.Combine(root, "Aetheria.State.Daemon", "bin", "Debug", "net10.0", "Aetheria.State.Daemon.exe");
    if (!File.Exists(daemon)) throw new FileNotFoundException("Build the Aetheria daemon before the progression smoke.", daemon);
    var arguments = new List<string>
    {
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
    arguments.AddRange(extra.Select((value, index) => index % 2 == 1 ? Quote(value) : value));
    var start = new ProcessStartInfo(daemon, string.Join(" ", arguments))
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start Aetheria daemon.");
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
