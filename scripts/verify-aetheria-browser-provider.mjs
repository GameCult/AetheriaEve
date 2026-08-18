import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { createServer as createHttpServer } from "node:http";
import { createServer as createNetServer } from "node:net";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const projectsRoot = resolve(repoRoot, "..");
const cultLibRoot = argumentRoot("--cultlib-root", join(projectsRoot, "CultLib"));
const eveRoot = argumentRoot("--eve-root", join(projectsRoot, "Eve"));
const workRoot = await mkdtemp(join(tmpdir(), "aetheria-browser-provider-"));
const statePath = join(workRoot, "browser-witness.cc");
const bundlePath = join(workRoot, "witness.js");
const daemonProject = join(repoRoot, "Aetheria.State.Daemon", "Aetheria.State.Daemon.csproj");
const daemonDll = join(repoRoot, "Aetheria.State.Daemon", "bin", "Debug", "net10.0", "Aetheria.State.Daemon.dll");
const importProject = join(repoRoot, "Aetheria.State.Import", "Aetheria.State.Import.csproj");
const importDll = join(repoRoot, "Aetheria.State.Import", "bin", "Debug", "net10.0", "Aetheria.State.Import.dll");
const odinProject = join(cultLibRoot, "samples", "eve-browser-network", "EveBrowserNetworkSample.csproj");
const odinDll = join(cultLibRoot, "bin", "EveBrowserNetworkSample", "Debug", "net10.0", "EveBrowserNetworkSample.dll");
const odinToken = "aetheria-browser-witness";
let daemon;
let odin;
let httpServer;
let browser;

try {
  const { build } = await import(pathToFileURL(join(cultLibRoot, "node_modules", "esbuild", "lib", "main.js")).href);
  const { chromium } = await import(pathToFileURL(join(cultLibRoot, "node_modules", "playwright-core", "index.mjs")).href);
  await build({
    entryPoints: [join(repoRoot, "scripts", "aetheria-browser-provider-witness.ts")],
    outfile: bundlePath,
    bundle: true,
    format: "esm",
    platform: "browser",
    target: "es2022",
    alias: {
      "cultmesh-browser": join(cultLibRoot, "packages", "cultmesh-browser", "src", "index.ts"),
      "cultnet-ts/contracts": join(cultLibRoot, "packages", "cultnet-ts", "src", "contracts.ts"),
      "@gamecult/eve-contracts": join(eveRoot, "packages", "eve-contracts", "src", "index.ts"),
      "@gamecult/eve-browser-lowering": join(eveRoot, "packages", "eve-browser-lowering", "src", "index.ts"),
    },
    logLevel: "warning",
  });
  const bundle = await readFile(bundlePath, "utf8");
  assert.doesNotMatch(bundle, /(?:from\s*["']node:|require\(["']node:)/u);

  await run("dotnet", [
    "build",
    daemonProject,
    "-m:1",
    "/p:UseSharedCompilation=false",
    "-clp:ErrorsOnly",
    "--verbosity",
    "quiet",
  ]);
  await run("dotnet", [
    "build",
    importProject,
    "-m:1",
    "/p:UseSharedCompilation=false",
    "-clp:ErrorsOnly",
    "--verbosity",
    "quiet",
  ]);
  await run("dotnet", [
    "build",
    odinProject,
    "-m:1",
    "/p:UseSharedCompilation=false",
    "-clp:ErrorsOnly",
    "--verbosity",
    "quiet",
  ]);
  await run("dotnet", [importDll, repoRoot, statePath]);
  daemon = startDaemon();
  const endpoint = await daemon.waitFor("Aetheria client browser CultMesh endpoint: ", 120_000);
  await daemon.waitFor("Aetheria daemon ready; waiting for a client to load or generate a world.", 30_000);
  const odinPort = await freePort();
  odin = startOdin(odinPort, endpoint);
  const odinEndpoint = await odin.waitFor("ODIN_READY ", 30_000);
  httpServer = await serve(bundlePath);
  const httpAddress = httpServer.address();
  if (!httpAddress || typeof httpAddress === "string") throw new Error("Browser witness HTTP server has no TCP address.");

  browser = await chromium.launch({ executablePath: resolveChromiumExecutable(chromium), headless: true });
  const page = await browser.newPage();
  await page.goto(
    `http://127.0.0.1:${httpAddress.port}/?endpoint=${encodeURIComponent(odinEndpoint)}&token=${encodeURIComponent(odinToken)}`,
  );
  await page.waitForFunction(() => window.__aetheriaWitness || window.__aetheriaWitnessError);
  const error = await page.evaluate(() => window.__aetheriaWitnessError);
  assert.equal(error, undefined, daemon.diagnostics());
  const witness = await page.evaluate(() => window.__aetheriaWitness);
  assert.equal(witness.title, "Aetheria Hangar");
  assert.ok(witness.verseOptions.includes("Local"), "Hangar Verse selector did not lower its Local option.");
  assert.ok(witness.buttons.includes("TERMINUS"));
  assert.ok(witness.buttons.includes("STARBRIDGE"));
  assert.ok(witness.buttons.includes("ARENA"));
  assert.ok(witness.buttons.includes("LAUNCH"));
  assert.equal(witness.connectionStates.at(-1), "connected");
  assert.ok(witness.commandId);
  assert.ok(["queued", "accepted"].includes(witness.commandStatus));
  assert.equal(witness.receiptSchema, "gamecult.eve.command_receipt.v1");
  assert.equal(witness.forgedIdentityStatus, "denied");

  await stop(daemon.process);
  daemon = startDaemon();
  const replacementEndpoint = await daemon.waitFor("Aetheria client browser CultMesh endpoint: ", 120_000);
  await daemon.waitFor("Aetheria daemon ready; waiting for a client to load or generate a world.", 30_000);
  assert.notEqual(replacementEndpoint, endpoint);
  await stop(odin.process);
  odin = startOdin(odinPort, replacementEndpoint);
  assert.equal(await odin.waitFor("ODIN_READY ", 30_000), odinEndpoint);
  await page.waitForFunction(
    () => window.__aetheriaWitness?.connectionStates.includes("reconnecting") &&
      window.__aetheriaWitness.connectionStates.at(-1) === "connected",
    undefined,
    { timeout: 60_000 },
  );
  const replacementCommand = await page.evaluate(async () => {
    if (!window.__aetheriaIssueVerseSelection) throw new Error("Route witness command hook is missing.");
    return await window.__aetheriaIssueVerseSelection();
  });
  assert.ok(replacementCommand.commandId);
  assert.notEqual(replacementCommand.commandId, witness.commandId);
  assert.ok(["queued", "accepted"].includes(replacementCommand.commandStatus));
  assert.equal(replacementCommand.receiptSchema, "gamecult.eve.command_receipt.v1");

  console.log(JSON.stringify({
    provider: "Aetheria.State.Daemon",
    endpoint,
    replacementEndpoint,
    odinEndpoint,
    surface: "aetheria.hangar",
    loweredBy: "Chromium Eve browser lowerer",
    verseOptions: witness.verseOptions,
    modeButtons: witness.buttons.filter(value => ["TERMINUS", "STARBRIDGE", "ARENA", "LAUNCH"].includes(value)),
    command: {
      id: witness.commandId,
      status: witness.commandStatus,
      receiptSchema: witness.receiptSchema,
    },
    replacementCommand,
    routeRotationCount: 1,
    forgedIdentityStatus: witness.forgedIdentityStatus,
  }));
} finally {
  if (browser) await browser.close().catch(() => undefined);
  if (daemon) await stop(daemon.process);
  if (odin) await stop(odin.process);
  if (httpServer) await new Promise(resolvePromise => httpServer.close(resolvePromise));
  await rm(workRoot, { recursive: true, force: true });
}

function argumentRoot(name, fallback) {
  const index = process.argv.indexOf(name);
  return resolve(index >= 0 ? process.argv[index + 1] : fallback);
}

function monitored(process, label = "process") {
  let output = "";
  let error = "";
  const waiters = [];
  process.stdout.setEncoding("utf8");
  process.stderr.setEncoding("utf8");
  process.stdout.on("data", chunk => { output += chunk; settle(); });
  process.stderr.on("data", chunk => { error += chunk; });
  process.on("exit", code => {
    for (const waiter of waiters.splice(0)) {
      clearTimeout(waiter.timer);
      waiter.reject(new Error(`${label} exited ${code}\n${output}\n${error}`));
    }
  });
  function settle() {
    for (let index = waiters.length - 1; index >= 0; index--) {
      const waiter = waiters[index];
      const line = output.split(/\r?\n/u).find(value => value.startsWith(waiter.prefix));
      if (!line) continue;
      waiters.splice(index, 1);
      clearTimeout(waiter.timer);
      waiter.resolve(line.slice(waiter.prefix.length));
    }
  }
  return {
    process,
    diagnostics() {
      return `${output}\n${error}`;
    },
    waitFor(prefix, timeoutMs) {
      return new Promise((resolvePromise, reject) => {
        const waiter = {
          prefix,
          resolve: resolvePromise,
          reject,
          timer: setTimeout(() => reject(new Error(`Timed out waiting for '${prefix}'.\n${output}\n${error}`)), timeoutMs),
        };
        waiters.push(waiter);
        settle();
      });
    },
  };
}

function startDaemon() {
  return monitored(spawn("dotnet", [
    daemonDll,
    "--root", repoRoot,
    "--state", statePath,
    "--client-cultmesh-port", "0",
    "--client-cultmesh-websocket-port", "0",
    "--client-cultmesh-content-port", "0",
    "--client-cultmesh-quic-port", "0",
    "--no-odin-announcements",
  ], { cwd: repoRoot, stdio: ["ignore", "pipe", "pipe"] }), "Aetheria daemon");
}

function startOdin(port, providerEndpoint) {
  return monitored(spawn("dotnet", [
    odinDll,
    "odin",
    "--port", String(port),
    "--provider-endpoint", providerEndpoint,
    "--token", odinToken,
    "--verse-id", "aetheria.local",
    "--verse-name", "Aetheria local product witness",
    "--authority-runtime-id", "aetheria-daemon",
    "--transport-version", "cultmesh.v0",
    "--rules-hash", "aetheria-runtime-world-v1",
  ], { cwd: cultLibRoot, stdio: ["ignore", "pipe", "pipe"] }), "Odin fixture");
}

function run(command, arguments_) {
  return new Promise((resolvePromise, reject) => {
    const process = spawn(command, arguments_, { cwd: repoRoot, stdio: "inherit" });
    process.on("error", reject);
    process.on("exit", code => code === 0
      ? resolvePromise()
      : reject(new Error(`${command} exited with code ${code}`)));
  });
}

function serve(bundle) {
  return new Promise((resolvePromise, reject) => {
    const server = createHttpServer(async (request, response) => {
      if (request.url?.startsWith("/witness.js")) {
        response.writeHead(200, { "content-type": "text/javascript" });
        response.end(await readFile(bundle));
        return;
      }
      response.writeHead(200, { "content-type": "text/html" });
      response.end("<!doctype html><meta charset=utf-8><main id=surface></main><script type=module src=/witness.js></script>");
    });
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => resolvePromise(server));
  });
}

async function freePort() {
  const server = createNetServer();
  await new Promise((resolvePromise, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolvePromise);
  });
  const address = server.address();
  const port = typeof address === "object" && address ? address.port : 0;
  await new Promise(resolvePromise => server.close(resolvePromise));
  return port;
}

async function stop(process) {
  if (process.exitCode != null) return;
  process.kill();
  await new Promise(resolvePromise => process.once("exit", resolvePromise));
}

function resolveChromiumExecutable(chromium) {
  const candidates = [process.env.CHROME_PATH];
  if (process.platform === "win32") {
    for (const root of [process.env.ProgramFiles, process.env["ProgramFiles(x86)"], process.env.LOCALAPPDATA]) {
      if (!root) continue;
      candidates.push(
        join(root, "Google", "Chrome", "Application", "chrome.exe"),
        join(root, "Microsoft", "Edge", "Application", "msedge.exe"),
      );
    }
  } else if (process.platform === "darwin") {
    candidates.push(
      "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
      "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    );
  } else {
    candidates.push(
      "/usr/bin/google-chrome",
      "/usr/bin/google-chrome-stable",
      "/usr/bin/chromium",
      "/usr/bin/chromium-browser",
      "/opt/google/chrome/chrome",
    );
  }
  candidates.push(chromium.executablePath());
  const executablePath = candidates.find(candidate => candidate && existsSync(candidate));
  if (executablePath) return executablePath;
  throw new Error(
    "No Chromium-family browser was found. Set CHROME_PATH or install Playwright Chromium.",
  );
}
