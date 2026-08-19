import { app, BrowserWindow, ipcMain, protocol, shell } from "electron";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createWriteStream, existsSync, mkdirSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { startEveElectronProviderHost } from "@gamecult/eve-electron/live-provider-host";
import { CultMesh } from "cultmesh-ts";
import { encode } from "@msgpack/msgpack";

const __dirname = dirname(fileURLToPath(import.meta.url));
const projectRoot = resolve(__dirname, "..");
const repoRoot = resolve(projectRoot, "..");
const logsRoot = resolve(projectRoot, "logs");
const runtimeRoot = resolve(process.env.AETHERIA_RUNTIME_ROOT ?? resolve(projectRoot, "runtime"));
const runtimeStatePath = resolve(runtimeRoot, "aetheria.cc");
const daemonDll = resolve(repoRoot, "Aetheria.State.Daemon", "bin", "Debug", "net10.0", "Aetheria.State.Daemon.dll");
const rendererIndex = resolve(projectRoot, "wwwroot", "index.html");
const clientCultMeshPort = Number.parseInt(process.env.AETHERIA_CLIENT_CULTMESH_PORT ?? "3076", 10);
const verseId = process.env.AETHERIA_VERSE_ID?.trim() || "aetheria.local";
const daemonId = process.env.AETHERIA_DAEMON_ID?.trim() || "aetheria-daemon";
const configuredDaemonUri = process.env.AETHERIA_CULTMESH_URI?.trim() ?? "";
const launchLocalDaemon = configuredDaemonUri.length === 0;
const daemonCultMeshUri = configuredDaemonUri || `cultmesh://aetheria/daemon/${encodeURIComponent(daemonId)}`;
const clientCultMeshAdvertiseHost = process.env.AETHERIA_CLIENT_CULTMESH_ADVERTISE_HOST?.trim() || "127.0.0.1";
const localDaemonRudpEndpoint = launchLocalDaemon ? `rudp://127.0.0.1:${clientCultMeshPort}` : "";
const electronSmoke = process.env.AETHERIA_ELECTRON_SMOKE === "1";
const electronSmokeResultPath = process.env.AETHERIA_ELECTRON_SMOKE_RESULT;

let daemonProcess: ChildProcessWithoutNullStreams | null = null;
let mainWindow: BrowserWindow | null = null;
let eveHost: { close(): Promise<void>; window: BrowserWindow } | null = null;
let isQuitting = false;

app.whenReady().then(async () => {
  writeElectronSmokeResult({ ok: false, stage: "electron-ready" });

  try {
    if (launchLocalDaemon) {
      await ensureDotnetBuild();
      mkdirSync(runtimeRoot, { recursive: true });
      daemonProcess = startDotnet("aetheria-daemon", daemonDll, [
        "--state",
        runtimeStatePath,
        "--verse-id",
        verseId,
        "--daemon-id",
        daemonId,
        "--client-cultmesh-port",
        clientCultMeshPort.toString(),
        "--client-cultmesh-advertise-host",
        clientCultMeshAdvertiseHost,
        "--tick-interval-ms",
        "20",
        "--fixed-delta-ms",
        "20",
        "--api-publication-interval-ms",
        "100000",
        "--no-odin-announcements",
      ]);
    } else {
      mkdirSync(runtimeRoot, { recursive: true });
    }

    const startedEveHost = await startEveElectronProviderHost({
      electron: { app, BrowserWindow, ipcMain, protocol, shell },
      dependencies: { CultMesh, encode },
      providerTarget: {
        providerId: "aetheria.daemon",
        advertisementRecordRef: "eve:provider:aetheria.daemon",
        peerId: daemonId,
        verseId,
        role: "aetheria-daemon",
        endpoints: localDaemonRudpEndpoint ? [localDaemonRudpEndpoint] : [],
      },
      renderer: rendererIndex,
      runtimeId: "eve-electron-aetheria",
      surfaceId: process.env.EVE_SURFACE_ID || "aetheria.starbridge.commander",
      window: {
        show: !electronSmoke,
        backgroundColor: "#0b1016",
        title: "Aetheria Starbridge",
      },
    });
    eveHost = startedEveHost;
    mainWindow = startedEveHost.window;
    writeElectronSmokeResult({ ok: false, stage: "renderer-loaded" });
    if (electronSmoke) {
      await withTimeout(mainWindow.webContents.executeJavaScript("window.eveProvider.providerAdvertisement()"), 10000, "provider advertisement smoke");
      writeElectronSmokeResult({ ok: false, stage: "provider-advertisement-readable" });
      await withTimeout(mainWindow.webContents.executeJavaScript("window.eveProvider.surface({ surfaceId: 'aetheria.starbridge.commander' })"), 10000, "surface smoke");
      writeElectronSmokeResult({ ok: false, stage: "provider-surface-readable" });
      await withTimeout(mainWindow.webContents.executeJavaScript(`window.eveProvider.submitCommand({
        providerId: 'aetheria.daemon', surfaceId: 'aetheria.starbridge.commander', command: 'aetheria.daemon.commands.SensorPing',
        clientId: 'aetheria-electron-smoke-preflight', payload: {}
      })`), 10000, "command receipt smoke");
      writeElectronSmokeResult({ ok: false, stage: "provider-command-receipt-readable" });
      const result = await runElectronSmoke(mainWindow);
      writeElectronSmokeResult({ ok: true, result });
      console.log(JSON.stringify(result, null, 2));
      await exitElectronSmoke(0);
    }
  } catch (error) {
    if (electronSmoke) {
      writeElectronSmokeResult({
        ok: false,
        error: error instanceof Error ? error.stack ?? error.message : String(error),
      });
      console.error(error instanceof Error ? error.stack ?? error.message : String(error));
      await exitElectronSmoke(1);
    } else {
      console.error(error instanceof Error ? error.stack ?? error.message : String(error));
    }
  }
});

app.on("before-quit", event => {
  if (isQuitting)
    return;

  event.preventDefault();
  isQuitting = true;
  void shutdownOwnedRuntime()
    .then(() => app.quit())
    .catch(error => {
      isQuitting = false;
      console.error(`Aetheria shutdown remains incomplete: ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
      void showFailure(error);
    });
});

async function runElectronSmoke(window: BrowserWindow): Promise<Record<string, unknown>> {
  const started = Date.now();
  let lastResult: Record<string, unknown> | null = null;
  while (Date.now() - started < 15000) {
    lastResult = await window.webContents.executeJavaScript(`
      (async () => {
        await new Promise(resolve => setTimeout(resolve, 100));
        const eveProvider = window.eveProvider;
        const status = document.querySelector("#status")?.textContent ?? "";
        const bodyMode = document.body.className;
        const eveHostText = document.querySelector("#eve-surface-host")?.textContent ?? "";
        const worldScene = document.querySelector(".cultui-world-scene");
        const worldEntityCount = worldScene?.querySelectorAll(".cultui-world-entity").length ?? 0;
        const controlledWorldEntityCount = worldScene?.querySelectorAll('.cultui-world-entity[data-controlled="true"]').length ?? 0;
        const workerRosterCount = document.querySelectorAll('.cultui-list-item[data-component-kind="agent.item"]').length;
        const providerAdvertisement = eveProvider ? await eveProvider.providerAdvertisement() : null;
        const eveSurface = eveProvider ? await eveProvider.surface({ surfaceId: "aetheria.starbridge.commander" }) : null;
        const pilotSurface = eveProvider ? await eveProvider.surface({ surfaceId: "aetheria.pilot" }) : null;
        const findComponent = (component, predicate) => {
          if (!component || typeof component !== "object") return null;
          if (predicate(component)) return component;
          for (const child of component.children ?? []) {
            const match = findComponent(child, predicate);
            if (match) return match;
          }
          return null;
        };
        const eveFieldSurface = findComponent(pilotSurface?.surface?.root, component =>
          component.kind === "field.surface2d" || component.kind === "gravity.surface");
        const embedded = slotId => eveFieldSurface?.embeddedDocuments?.find(slot => slot.slotId === slotId) ?? null;
        const resolveEmbedded = async slot => slot ? eveProvider.document({ documentId: slot.documentId, schemaId: slot.schemaId }) : null;
        const [renderSplatsResolved, gravityResolved, objectsResolved] = await Promise.all([
          resolveEmbedded(embedded("renderSplats")),
          resolveEmbedded(embedded("gravity")),
          resolveEmbedded(embedded("objects")),
        ]);
        const assetProbe = await new Promise(resolve => {
          const image = new Image();
          image.onload = () => resolve({ loaded: true, width: image.naturalWidth, height: image.naturalHeight });
          image.onerror = () => resolve({ loaded: false, width: 0, height: 0 });
          image.src = "eve-asset://asset?uri=" + encodeURIComponent("cultmesh://aetheria/assets/map/entity/player");
        });
        const eveReceipt = eveProvider ? await eveProvider.submitCommand({
          providerId: "aetheria.daemon",
          surfaceId: "aetheria.starbridge.commander",
          command: "aetheria.daemon.commands.SensorPing",
          clientId: "aetheria-electron-smoke",
          payload: {},
        }) : null;
        return {
          hasApi: !!eveProvider &&
            typeof eveProvider.providerAdvertisement === "function" &&
            typeof eveProvider.document === "function" &&
            typeof eveProvider.surface === "function" &&
            typeof eveProvider.submitCommand === "function",
          status,
          bodyMode,
          eveHostText,
          worldSceneReady: !!worldScene,
          worldEntityCount,
          controlledWorldEntityCount,
          workerRosterCount,
          providerAdvertisement,
          eveSurface,
          pilotSurface,
          renderSplatsResolved,
          gravityResolved,
          objectsResolved,
          assetProbe,
          eveFieldSurface,
          eveReceipt
        };
      })()
    `, true) as Record<string, unknown>;

    if (isElectronSmokeReady(lastResult))
      return lastResult;
    await delay(250);
  }

  throw new Error(`Aetheria Starbridge Electron smoke did not reach ready state. Last result: ${JSON.stringify(lastResult)}`);
}

function isElectronSmokeReady(result: Record<string, unknown>): boolean {
  const status = stringValue(result.status);
  const bodyMode = stringValue(result.bodyMode);
  const eveHostText = stringValue(result.eveHostText);
  const eveSurface = objectValue(result.eveSurface);
  const providerAdvertisement = objectValue(result.providerAdvertisement);
  const renderSplatsResolved = objectValue(result.renderSplatsResolved);
  const gravityResolved = objectValue(result.gravityResolved);
  const objectsResolved = objectValue(result.objectsResolved);
  const eveFieldSurface = objectValue(result.eveFieldSurface);
  const eveReceipt = objectValue(result.eveReceipt);
  return result.hasApi === true &&
    status.includes("Starbridge Commander") &&
    bodyMode.includes("eve-game-mode") &&
    result.worldSceneReady === true &&
    Number(result.worldEntityCount) > 0 &&
    Number(result.controlledWorldEntityCount) === 1 &&
    Number(result.workerRosterCount) > 0 &&
    eveHostText.includes("Station Stock") &&
    eveHostText.includes("Wave Forecast") &&
    arrayValue(providerAdvertisement?.surfaces).some(surface => objectValue(surface)?.surfaceId === "aetheria.starbridge.commander") &&
    objectValue(eveSurface?.surface)?.id === "aetheria.starbridge.commander" &&
    stringValue(renderSplatsResolved?.documentId).length > 0 &&
    stringValue(gravityResolved?.documentId).length > 0 &&
    stringValue(objectsResolved?.documentId).length > 0 &&
    renderSplatsResolved?.schemaId === "gamecult.fields.splats.v1" &&
    gravityResolved?.schemaId === "gamecult.fields.gravity.v1" &&
    objectsResolved?.schemaId === "gamecult.fields.objects.v1" &&
    objectValue(result.assetProbe)?.loaded === true &&
    Number(objectValue(result.assetProbe)?.width) > 0 &&
    Number(objectValue(result.assetProbe)?.height) > 0 &&
    stringValue(eveFieldSurface?.id).length > 0 &&
    arrayValue(eveFieldSurface?.embeddedDocuments).some(slot =>
      objectValue(slot)?.slotId === "renderSplats") &&
    arrayValue(eveFieldSurface?.embeddedDocuments).some(slot =>
      objectValue(slot)?.slotId === "gravity") &&
    arrayValue(eveFieldSurface?.embeddedDocuments).some(slot =>
      objectValue(slot)?.slotId === "objects") &&
    stringValue(eveReceipt?.commandId).length > 0 &&
    objectValue(eveReceipt?.route)?.kind === "network" &&
    eveReceipt?.accepted === true;
}

function stringValue(value: unknown): string {
  return typeof value === "string" ? value : "";
}

function objectValue(value: unknown): Record<string, unknown> | null {
  return value != null && typeof value === "object" ? value as Record<string, unknown> : null;
}

function arrayValue(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function writeElectronSmokeResult(result: Record<string, unknown>): void {
  if (!electronSmokeResultPath)
    return;

  writeFileSync(electronSmokeResultPath, JSON.stringify(result, null, 2), "utf8");
}

async function exitElectronSmoke(exitCode: number): Promise<void> {
  isQuitting = true;
  try {
    await shutdownOwnedRuntime();
  } catch (error) {
    isQuitting = false;
    throw error;
  }
  mainWindow?.destroy();
  mainWindow = null;
  app.exit(exitCode);
}

async function shutdownOwnedRuntime(): Promise<void> {
  const ownedDaemon = daemonProcess;
  await stopChildGracefully(ownedDaemon);
  if (daemonProcess === ownedDaemon)
    daemonProcess = null;

  const ownedHost = eveHost;
  if (ownedHost) {
    await withTimeout(ownedHost.close(), 5000, "Eve host shutdown");
    if (eveHost === ownedHost)
      eveHost = null;
  }
}

function withTimeout<T>(work: Promise<T>, timeoutMs: number, label: string): Promise<T> {
  return Promise.race([
    work,
    new Promise<T>((_resolve, reject) => setTimeout(() => reject(new Error(`Timed out during ${label}.`)), timeoutMs)),
  ]);
}

async function ensureDotnetBuild(): Promise<void> {
  if (existsSync(daemonDll) && !daemonBuildInputsAreNewer(daemonDll))
    return;

  await runProcess("dotnet", ["build", resolve(repoRoot, "Aetheria.State.Daemon", "Aetheria.State.Daemon.csproj")], "Aetheria.State.Daemon.build");
}

function daemonBuildInputsAreNewer(outputPath: string): boolean {
  const outputMtime = statSync(outputPath).mtimeMs;
  const sourceRoots = [
    resolve(repoRoot, "Aetheria.State.Daemon"),
    resolve(repoRoot, "Aetheria.State"),
    resolve(repoRoot, "Packages", "org.gamecult.aetheria.state", "Runtime"),
  ];

  return sourceRoots.some(sourceRoot => newestSourceMtime(sourceRoot) > outputMtime);
}

function newestSourceMtime(root: string): number {
  if (!existsSync(root))
    return 0;

  const stat = statSync(root);
  if (stat.isFile())
    return isBuildInput(root) ? stat.mtimeMs : 0;

  let newest = 0;
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const path = resolve(root, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "bin" || entry.name === "obj")
        continue;
      newest = Math.max(newest, newestSourceMtime(path));
    } else if (entry.isFile() && isBuildInput(path)) {
      newest = Math.max(newest, statSync(path).mtimeMs);
    }
  }
  return newest;
}

function isBuildInput(path: string): boolean {
  return path.endsWith(".cs") || path.endsWith(".csproj") || path.endsWith(".props") || path.endsWith(".targets");
}

function startDotnet(name: string, dllPath: string, args: string[]): ChildProcessWithoutNullStreams {
  mkdirSync(logsRoot, { recursive: true });
  const child = spawn("dotnet", [dllPath, ...args], {
    cwd: repoRoot,
    detached: process.platform !== "win32",
    windowsHide: true,
  });
  pipeChildLogs(name, child);
  child.once("exit", (code, signal) => {
    if (!isQuitting) {
      void showFailure(new Error(`${name} exited unexpectedly (${signal ?? code ?? "unknown"}). Check ${logsRoot}.`));
    }
  });
  return child;
}

function runProcess(command: string, args: string[], logName: string): Promise<void> {
  mkdirSync(logsRoot, { recursive: true });
  const child = spawn(command, args, {
    cwd: repoRoot,
    windowsHide: true,
  });
  pipeChildLogs(logName, child);
  return new Promise((resolvePromise, reject) => {
    child.once("error", reject);
    child.once("exit", code => {
      if (code === 0) {
        resolvePromise();
        return;
      }

      reject(new Error(`${command} ${args.join(" ")} failed with exit code ${code}. Check ${logsRoot}.`));
    });
  });
}

function pipeChildLogs(name: string, child: ChildProcessWithoutNullStreams): void {
  const stdout = createWriteStream(resolve(logsRoot, `${name}.log`), { flags: "a" });
  const stderr = createWriteStream(resolve(logsRoot, `${name}.err.log`), { flags: "a" });
  child.stdout.pipe(stdout);
  child.stderr.pipe(stderr);
}

async function stopChildGracefully(child: ChildProcessWithoutNullStreams | null): Promise<void> {
  if (!child || child.exitCode !== null || child.signalCode !== null)
    return;

  const exited = new Promise<void>(resolvePromise => child.once("exit", () => resolvePromise()));
  try {
    if (!child.stdin.destroyed && child.stdin.writable)
      child.stdin.write("shutdown\n");
  } catch (error) {
    console.error(`Failed to request graceful daemon shutdown: ${error instanceof Error ? error.message : String(error)}`);
  }

  if (await waitForExit(exited, 5000))
    return;

  await terminateProcessTree(child);
  if (!await waitForExit(exited, 5000))
    throw new Error(`Aetheria daemon PID ${child.pid ?? "unknown"} survived graceful shutdown and process-tree termination.`);
}

async function terminateProcessTree(child: ChildProcessWithoutNullStreams): Promise<void> {
  if (child.exitCode !== null || child.signalCode !== null)
    return;
  const pid = child.pid;
  if (!pid)
    throw new Error("Cannot terminate the Aetheria daemon tree without its process identity.");

  if (process.platform === "win32") {
    const exitCode = await runExitCode("taskkill", ["/PID", pid.toString(), "/T", "/F"]);
    if (exitCode !== 0 && child.exitCode === null && child.signalCode === null)
      throw new Error(`taskkill could not terminate the Aetheria daemon tree (exit ${exitCode}).`);
    return;
  }

  try {
    process.kill(-pid, "SIGKILL");
  } catch (error) {
    if (child.exitCode === null && child.signalCode === null)
      throw error;
  }
}

function runExitCode(command: string, args: string[]): Promise<number | null> {
  const child = spawn(command, args, {
    cwd: repoRoot,
    windowsHide: true,
    stdio: "ignore",
  });
  return new Promise((resolvePromise, reject) => {
    child.once("error", reject);
    child.once("exit", code => resolvePromise(code));
  });
}

async function waitForExit(exited: Promise<void>, timeoutMs: number): Promise<boolean> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timedOut = new Promise<false>(resolvePromise => {
    timer = setTimeout(() => resolvePromise(false), timeoutMs);
  });
  const result = await Promise.race([exited.then(() => true as const), timedOut]);
  if (timer)
    clearTimeout(timer);
  return result;
}

function delay(milliseconds: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function showFailure(error: unknown): Promise<void> {
  const message = error instanceof Error ? error.message : String(error);
  await mainWindow?.loadURL(`data:text/html;charset=utf-8,${encodeURIComponent(startupHtml("Aetheria Starbridge failed to start", message))}`);
}

function startupHtml(title: string, detail: string): string {
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>${escapeHtml(title)}</title>
  <style>
    body {
      margin: 0;
      min-height: 100vh;
      display: grid;
      place-items: center;
      background: #0b1016;
      color: #dbe7f3;
      font-family: "Segoe UI", sans-serif;
    }
    main {
      width: min(520px, calc(100vw - 48px));
    }
    h1 {
      margin: 0 0 12px;
      font-size: 24px;
      font-weight: 650;
    }
    p {
      margin: 0;
      color: #8fa3b7;
      line-height: 1.5;
    }
  </style>
</head>
<body>
  <main>
    <h1>${escapeHtml(title)}</h1>
    <p>${escapeHtml(detail)}</p>
  </main>
</body>
</html>`;
}

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}
