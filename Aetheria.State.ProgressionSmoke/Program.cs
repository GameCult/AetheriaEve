using Aetheria.State;
using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

try
{
if (args.Contains("--process-output-lifecycle", StringComparer.Ordinal))
{
    RunProcessOutputLifecycleSmoke();
    Console.WriteLine("Progression smoke child output lifecycle failed in a controlled process.");
    return;
}
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
const string poisonCommandId = "progression-smoke-poison-command";
const string delayedRemoteCommandId = "progression-smoke-delayed-remote-command";
var localTarget = new CultMeshSessionTarget(localVerse, "progression-local");
var remoteTarget = new CultMeshSessionTarget(remoteVerse, "progression-remote");
var localPort = FreeTcpPort();
var remotePort = FreeTcpPort();
var localEndpoint = $"cultnet+tcp://127.0.0.1:{localPort}";
var remoteEndpoint = $"cultnet+tcp://127.0.0.1:{remotePort}";
var remoteLifecyclePipe = "aetheria-progression-smoke-" + Guid.NewGuid().ToString("N");
Process? local = null;
Process? remote = null;
try
{
    remote = StartDaemon(root, remoteState, "progression-remote", remoteVerse, remotePort,
        "--lifecycle-pipe", remoteLifecyclePipe,
        "--api-publication-interval-ms", "100000");
    await WaitForEndpointAsync(remote, remotePort, TimeSpan.FromSeconds(30));
    await WaitForVerseAdvertisementAsync(
        remoteEndpoint,
        remoteVerse,
        "progression-remote",
        TimeSpan.FromSeconds(45));
    var previousRouteDelayCommand = Environment.GetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_ROUTE_COMMAND_ID");
    var previousRouteDelay = Environment.GetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_ROUTE_MS");
    Environment.SetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_ROUTE_COMMAND_ID", delayedRemoteCommandId);
    Environment.SetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_ROUTE_MS", "1500");
    try
    {
        local = StartDaemon(root, localState, "progression-local", localVerse, localPort,
            "--odin-discovery-endpoint", remoteEndpoint);
    }
    finally
    {
        Environment.SetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_ROUTE_COMMAND_ID", previousRouteDelayCommand);
        Environment.SetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_ROUTE_MS", previousRouteDelay);
    }
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

        var poisonRequest = new EveSurfaceCommandRequest(
            initialSurface.ProviderId,
            initialSurface.Surface.Id,
            CultMesh.OperationInvocation(
                "aetheria.hangar.injected-failure",
                idempotencyKey: poisonCommandId),
            TargetedPayload(initialSurface),
            DateTimeOffset.UtcNow,
            "progression-verse-smoke");
        const string validModeCommandId = "progression-smoke-valid-after-poison";
        var validModeRequest = new EveSurfaceCommandRequest(
            initialSurface.ProviderId,
            initialSurface.Surface.Id,
            CultMesh.OperationInvocation(
                initialSurface.Commands.Single(template => template.Command == AetheriaRuntimeHangarCommands.SelectArena).Operation,
                idempotencyKey: validModeCommandId),
            TargetedPayload(initialSurface),
            DateTimeOffset.UtcNow.AddTicks(1),
            "progression-verse-smoke");
        await client.SubmitDocumentAsync(
            localTarget,
            AetheriaRuntimeVerseRecordKeys.EveCommandRecordPrefix + ":" + poisonCommandId,
            poisonRequest,
            "progression-verse-smoke",
            "headless-smoke");
        await client.SubmitDocumentAsync(
            localTarget,
            AetheriaRuntimeVerseRecordKeys.EveCommandRecordPrefix + ":" + validModeCommandId,
            validModeRequest,
            "progression-verse-smoke",
            "headless-smoke");
        var validModeReceipt = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(validModeCommandId).ToString(),
            (EveCommandReceiptDocument receipt) => receipt.State == "accepted",
            TimeSpan.FromSeconds(5));
        var draftAfterPoison = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaStateNode.HangarDraftKey.ToString(),
            (AetheriaHangarDraftState draft) => draft.SelectedMode == AetheriaGameModes.Arena,
            TimeSpan.FromSeconds(5));
        Require(validModeReceipt.CommandId == validModeCommandId &&
                draftAfterPoison.SelectedMode == AetheriaGameModes.Arena,
            "one poison Hangar request must remain isolated while a later valid command reaches independent finality");

        var localHangarBeforeRemote = await client.ReadAsync<AetheriaHangarState>(
            localTarget,
            AetheriaStateNode.HangarKey.ToString());

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
        removeRequest = new EveSurfaceCommandRequest(
            removeRequest!.ProviderId,
            removeRequest.SurfaceId,
            new CultMeshOperationInvocationDescriptor(
                removeRequest.Command,
                removeRequest.Operation.SchemaId,
                removeRequest.Operation.RouteHint,
                delayedRemoteCommandId),
            removeRequest.Payload,
            removeRequest.IssuedAt,
            removeRequest.ClientId,
            removeRequest.CommandBoundary,
            removeRequest.ReceiptSchema);
        var removeReceiptTask = SubmitRequestAsync(client, localTarget, removeRequest!);
        var localFinality = Stopwatch.StartNew();
        var selectLocalReceipt = await SubmitAsync(
            client,
            localTarget,
            remoteSurface,
            AetheriaRuntimeHangarCommands.SelectVerse,
            new Dictionary<string, string> { ["value"] = AetheriaProgressionSources.Local });
        localFinality.Stop();
        Require(selectLocalReceipt.State == "accepted" &&
                localFinality.Elapsed < TimeSpan.FromMilliseconds(1500) &&
                !removeReceiptTask.IsCompleted,
            "a pre-route remote command must not hold the state gate or block a later Verse selection");
        var pinnedRemoveRoute = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaRuntimeVerseRecordKeys.ProgressionCommandRoute(removeRequest!.CommandId).ToString(),
            (AetheriaProgressionCommandRouteDocument route) =>
                route.CommandId == removeRequest.CommandId &&
                route.VerseId == remoteVerse &&
                route.ProgressionSourceRevision == long.Parse(
                    removeRequest.PayloadFields[AetheriaRuntimeHangarCommands.ExpectedProgressionSourceRevision]),
            TimeSpan.FromSeconds(5));
        Require(pinnedRemoveRoute.VerseId == remoteVerse,
            "changing the Verse dropdown before route creation must not retarget the admitted command to Local");
        var localDuringRemoteWait = await ReadUntilAsync(
            client,
            localTarget,
            AetheriaRuntimeVerseRecordKeys.HangarSurface.ToString(),
            (EveSurfaceDocument surface) =>
                Find(surface.Surface.Root, "aetheria.hangar.verse").Props["value"] == AetheriaProgressionSources.Local,
            TimeSpan.FromSeconds(5));
        await SubmitAsync(
            client,
            localTarget,
            localDuringRemoteWait,
            AetheriaRuntimeHangarCommands.SelectVerse,
            new Dictionary<string, string> { ["value"] = remoteVerse });
        var removeReceipt = await removeReceiptTask;
        Require(removeReceipt.State == "accepted",
            "The remote progression Verse must accept the Eve loadout-to-storage drop.");
        var updatedRemoteHangar = await ReadUntilAsync(
            remoteClient,
            remoteTarget,
            AetheriaStateNode.HangarKey.ToString(),
            (AetheriaHangarState hangar) => hangar.Revision > remoteRevision,
            TimeSpan.FromSeconds(10));
        var localHangarAfterRemote = await client.ReadAsync<AetheriaHangarState>(
            localTarget,
            AetheriaStateNode.HangarKey.ToString());
        Require(localHangarAfterRemote.Revision == localHangarBeforeRemote.Revision,
            "a remote command delayed before route creation must never fall through into Local progression after a dropdown switch");

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
        Require(launchReceipt.Authority == localTarget.AuthorityRuntimeId,
            "The local progression router must own the client-facing receipt authority.");
        Require(!string.IsNullOrWhiteSpace(launchReceipt.InvocationHash),
            "The client-facing launch receipt must identify the immutable invocation it finalizes.");
        Require(launchNavigation!.SurfaceId == AetheriaRuntimeDaemonGameSurfaceBuilder.PilotSurfaceId,
            "Remote Terminus launch must navigate the generic client to the Pilot surface.");
        Require(launchNavigation.AuthorityRuntimeId == remoteTarget.AuthorityRuntimeId,
            "Remote Terminus navigation must preserve the exact authority runtime that owns the accepted deployment.");
        Require(launchNavigation.RendezvousEndpoints.SequenceEqual(new[] { remoteEndpoint }, StringComparer.Ordinal),
            "Remote Terminus navigation must carry only the configured Odin rendezvous route, not flattened content or realtime provider routes.");
        using (var navigatedClient = Client(launchNavigation.RendezvousEndpoints[0]))
        {
            var navigationTarget = new CultMeshSessionTarget(
                launchNavigation.VerseId,
                launchNavigation.AuthorityRuntimeId);
            using var gameSurfaceDemand = await navigatedClient.LeaseDocumentAsync<EveSurfaceDocument>(
                navigationTarget,
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
                navigationTarget,
                AetheriaStateNode.HangarKey.ToString(),
                (AetheriaHangarState hangar) =>
                    (hangar.Deployments ?? []).Any(deployment => deployment.RequestId == launchReceipt.CommandId),
                TimeSpan.FromSeconds(10));
            var deployment = deployedHangar.Deployments.Single(value => value.RequestId == launchReceipt.CommandId);
            var gameSession = await ReadUntilAsync(
                gameplayStateClient,
                navigationTarget,
                AetheriaStateNode.GameSessionStateKey.ToString(),
                (AetheriaGameSessionState session) =>
                    session.Mode == AetheriaGameSessionState.TerminusMode &&
                    session.RunId == deployment.RunId &&
                    session.RunRecordKey == deployment.RunRecordKey &&
                    session.SimulationRate > 0,
                TimeSpan.FromSeconds(10));
            var liveFrame = await ReadUntilAsync(
                gameplayStateClient,
                navigationTarget,
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
        Require(continueReceipt.State == "accepted" &&
                continueReceipt.Navigation?.VerseId == remoteVerse &&
                continueReceipt.Navigation.AuthorityRuntimeId == remoteTarget.AuthorityRuntimeId &&
                continueReceipt.Authority == localTarget.AuthorityRuntimeId,
            "Remote Terminus continue must return the selected Verse Pilot navigation target.");

        var pinnedLaunchRoute = await client.ReadAsync<AetheriaProgressionCommandRouteDocument>(
            localTarget,
            AetheriaRuntimeVerseRecordKeys.ProgressionCommandRoute(launchReceipt.CommandId).ToString());
        Require(pinnedLaunchRoute != null &&
                pinnedLaunchRoute.VerseId == remoteVerse &&
                pinnedLaunchRoute.AuthorityRuntimeId == remoteTarget.AuthorityRuntimeId &&
                pinnedLaunchRoute.PayloadHash == launchReceipt.InvocationHash,
            "The first remote launch attempt must durably pin its Verse and authority target.");
        var canonicalRemoteLaunchReceipt = await remoteClient.ReadAsync<EveCommandReceiptDocument>(
            remoteTarget,
            AetheriaRuntimeVerseRecordKeys.EveReceiptForCommand(launchReceipt.CommandId).ToString());
        Require(canonicalRemoteLaunchReceipt.Authority == remoteTarget.AuthorityRuntimeId &&
                canonicalRemoteLaunchReceipt.InvocationHash == pinnedLaunchRoute.ForwardedInvocationHash,
            "Remote finality must bind the remote authority to the same immutable invocation digest pinned by the local router.");

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

    if (!Stop(local))
        throw new InvalidOperationException("The local daemon could not be stopped for the restart witness.");
    var previousFailureInjection = Environment.GetEnvironmentVariable("AETHERIA_DEV_INJECT_HANGAR_FAILURE_COMMAND_ID");
    var previousReceiptDelayCommand = Environment.GetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_RECEIPT_COMMAND_ID");
    var previousReceiptDelay = Environment.GetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_RECEIPT_MS");
    Environment.SetEnvironmentVariable("AETHERIA_DEV_INJECT_HANGAR_FAILURE_COMMAND_ID", poisonCommandId);
    Environment.SetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_RECEIPT_COMMAND_ID", delayedRemoteCommandId);
    Environment.SetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_RECEIPT_MS", "3000");
    try
    {
        local = StartDaemon(root, localState, "progression-local", localVerse, localPort,
            "--odin-discovery-endpoint", remoteEndpoint);
    }
    finally
    {
        Environment.SetEnvironmentVariable("AETHERIA_DEV_INJECT_HANGAR_FAILURE_COMMAND_ID", previousFailureInjection);
        Environment.SetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_RECEIPT_COMMAND_ID", previousReceiptDelayCommand);
        Environment.SetEnvironmentVariable("AETHERIA_DEV_DELAY_PROGRESSION_RECEIPT_MS", previousReceiptDelay);
    }
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

    long durableFrameBeforeShutdown;
    using (var shutdownClient = Client(remoteEndpoint))
    {
        using var activeDemand = await shutdownClient.LeaseDocumentAsync<EveSurfaceDocument>(
            remoteTarget,
            AetheriaRuntimeVerseRecordKeys.DaemonGameSurface.ToString());
        await ReadLiveUntilAsync(
            activeDemand.Handle,
            surface => string.Equals(surface.Surface.Id, AetheriaRuntimeDaemonGameSurfaceBuilder.PilotSurfaceId, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        durableFrameBeforeShutdown = (await shutdownClient.ReadAsync<AetheriaRuntimeDaemonFrameDocument>(
            remoteTarget,
            AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest.ToString())).FrameId;
        await Task.Delay(500);
        if (!Stop(remote, lifecyclePipeName: remoteLifecyclePipe))
            throw new InvalidOperationException("The remote daemon could not complete the graceful checkpoint witness.");
        remote = null;
    }
    remote = StartDaemon(root, remoteState, "progression-remote", remoteVerse, remotePort,
        "--lifecycle-pipe", remoteLifecyclePipe,
        "--api-publication-interval-ms", "100000");
    await WaitForEndpointAsync(remote, remotePort, TimeSpan.FromSeconds(30));
    await WaitForVerseAdvertisementAsync(
        remoteEndpoint,
        remoteVerse,
        "progression-remote",
        TimeSpan.FromSeconds(45));
    using (var checkpointClient = Client(remoteEndpoint))
    {
        var restoredFrame = await checkpointClient.ReadAsync<AetheriaRuntimeDaemonFrameDocument>(
            remoteTarget,
            AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest.ToString());
        Require(restoredFrame.FrameId > durableFrameBeforeShutdown,
            "Graceful shutdown must checkpoint the last authoritative frame even when periodic publication has not run.");
    }

    Console.WriteLine("Aetheria Hangar Verse discovery, poison-command isolation, nonblocking pinned forwarding, remote spatial refit, Terminus launch/continue navigation, and graceful restart smoke passed.");
}
catch
{
    Console.Error.WriteLine($"Local daemon: {ProcessState(local)}; remote daemon: {ProcessState(remote)}.");
    throw;
}
finally
{
    var localStopped = Stop(local);
    var remoteStopped = Stop(remote, lifecyclePipeName: remoteLifecyclePipe);
    if (localStopped && remoteStopped &&
        !string.Equals(Environment.GetEnvironmentVariable("AETHERIA_SMOKE_KEEP_STATE"), "1", StringComparison.Ordinal))
    {
        try { Directory.Delete(smokeRoot, recursive: true); } catch { }
    }
    else if (!localStopped || !remoteStopped)
    {
        Console.Error.WriteLine($"Aetheria progression smoke preserved '{smokeRoot}' because child termination was not confirmed.");
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
    var commandPayload = new Dictionary<string, string>(payload, StringComparer.Ordinal);
    if (!string.Equals(command, AetheriaRuntimeHangarCommands.SelectVerse, StringComparison.Ordinal) &&
        string.Equals(surface.Surface.Id, AetheriaRuntimeHangarCommands.SurfaceId, StringComparison.Ordinal))
    {
        foreach (var field in TargetedPayload(surface))
            commandPayload[field.Key] = field.Value;
    }
    var operation = CultMesh.OperationInvocation(
        surface.Commands.Single(template => template.Command == command).Operation,
        idempotencyKey: "progression-verse-smoke-" + Guid.NewGuid().ToString("N"));
    var request = new EveSurfaceCommandRequest(
        surface.ProviderId,
        surface.Surface.Id,
        operation,
        CultMesh.OperationPayload(commandPayload),
        DateTimeOffset.UtcNow,
        "progression-verse-smoke");
    return await SubmitRequestAsync(client, target, request);
}

static CultMeshOperationPayload TargetedPayload(EveSurfaceDocument surface)
{
    var root = surface?.Surface?.Root ?? throw new ArgumentNullException(nameof(surface));
    return CultMesh.OperationPayload(
        (AetheriaRuntimeHangarCommands.ExpectedProgressionVerseId,
            root.Props.TryGetValue("progressionVerseId", out var verseId) ? verseId : AetheriaProgressionSources.Local),
        (AetheriaRuntimeHangarCommands.ExpectedProgressionSourceRevision,
            root.Props.TryGetValue("progressionSourceRevision", out var revision) ? revision : "0"));
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
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    var logPath = state + ".daemon.log";
    start.Environment["AETHERIA_SMOKE_LOG_PATH"] = logPath;
    var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start Aetheria daemon.");
    AetheriaProgressionSmokeProcessOutput.Attach(process, logPath);
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

static bool Stop(Process? process, bool forceEscalationForTest = false, string lifecyclePipeName = "")
{
    if (process == null) return true;
    try
    {
        if (!process.HasExited && !forceEscalationForTest && !string.IsNullOrWhiteSpace(lifecyclePipeName))
        {
            using var pipe = new NamedPipeClientStream(".", lifecyclePipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("shutdown");
        }
        else if (!process.HasExited && !forceEscalationForTest && process.StartInfo.RedirectStandardInput)
        {
            process.StandardInput.WriteLine("shutdown");
            process.StandardInput.Flush();
        }
        var exitedAfterFirstWait = !forceEscalationForTest && process.WaitForExit(5000);
        if (!exitedAfterFirstWait && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(10000) && !process.HasExited)
            {
                Console.Error.WriteLine(
                    $"Aetheria progression smoke child {process.Id} survived termination escalation; state is being preserved.");
                Environment.ExitCode = 1;
                return false;
            }
        }

        process.WaitForExit();
        AetheriaProgressionSmokeProcessOutput.Detach(process);
        process.Dispose();
        return true;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Aetheria progression smoke child shutdown failed cleanly: {error}");
        Environment.ExitCode = 1;
        try
        {
            if (process.HasExited)
            {
                process.WaitForExit();
                AetheriaProgressionSmokeProcessOutput.Detach(process);
                process.Dispose();
                return true;
            }
        }
        catch (Exception cleanupError)
        {
            Console.Error.WriteLine($"Aetheria progression smoke child cleanup also failed cleanly: {cleanupError}");
        }
        try { Console.Error.WriteLine($"Aetheria progression smoke preserved child pid {process.Id} for operator cleanup."); } catch { }
        return false;
    }
}

static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

static void RunProcessOutputLifecycleSmoke()
{
    var priorExitCode = Environment.ExitCode;
    Environment.ExitCode = 0;
    var unhandled = false;
    UnhandledExceptionEventHandler observer = (_, _) => unhandled = true;
    AppDomain.CurrentDomain.UnhandledException += observer;
    Process? child = null;
    try
    {
        var missingLog = Path.Combine(
            Path.GetTempPath(),
            "aetheria-missing-output-" + Guid.NewGuid().ToString("N"),
            "child.log");
        child = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            "/d /c \"for /L %i in (1,1,5000) do @echo child-output\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the output lifecycle probe child.");
        AetheriaProgressionSmokeProcessOutput.Attach(child, missingLog);
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        Thread.Sleep(20);
        if (Stop(child, forceEscalationForTest: true))
            child = null;
        Require(Environment.ExitCode == 1,
            "an unavailable child log must become a controlled smoke failure");
        Require(!unhandled,
            "child output capture must never escape through AppDomain.UnhandledException");
    }
    finally
    {
        if (child != null) Stop(child);
        AppDomain.CurrentDomain.UnhandledException -= observer;
        if (priorExitCode != 0)
            Environment.ExitCode = priorExitCode;
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal static class AetheriaProgressionSmokeProcessOutput
{
    private static readonly ConcurrentDictionary<int, Capture> Captures = new();

    public static void Attach(Process process, string logPath)
    {
        var capture = new Capture(logPath);
        if (!Captures.TryAdd(process.Id, capture))
            throw new InvalidOperationException($"Output capture already exists for child process {process.Id}.");
        process.OutputDataReceived += capture.OutputHandler;
        process.ErrorDataReceived += capture.ErrorHandler;
    }

    public static void Detach(Process process)
    {
        if (!Captures.TryRemove(process.Id, out var capture)) return;
        process.OutputDataReceived -= capture.OutputHandler;
        process.ErrorDataReceived -= capture.ErrorHandler;
        if (capture.ErrorCount > 0)
        {
            Console.Error.WriteLine(
                $"Aetheria progression smoke child output capture failed cleanly " +
                $"({capture.ErrorCount} write attempt(s)): {capture.FirstError}");
            Environment.ExitCode = 1;
        }
    }

    private sealed class Capture
    {
        private readonly string _logPath;
        private readonly object _gate = new();
        private Exception? _firstError;
        private int _errorCount;

        public Capture(string logPath)
        {
            _logPath = logPath;
            OutputHandler = (_, eventArgs) => Record(eventArgs.Data);
            ErrorHandler = (_, eventArgs) => Record(eventArgs.Data);
        }

        public DataReceivedEventHandler OutputHandler { get; }
        public DataReceivedEventHandler ErrorHandler { get; }
        public Exception? FirstError => _firstError;
        public int ErrorCount => Volatile.Read(ref _errorCount);

        private void Record(string? line)
        {
            if (line == null) return;
            try
            {
                lock (_gate) File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _firstError, error, null);
                Interlocked.Increment(ref _errorCount);
            }
        }
    }
}
