import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createServer } from "node:http";
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
let daemon;
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
    "--verbosity",
    "quiet",
  ]);
  await run("dotnet", [
    "build",
    importProject,
    "-m:1",
    "/p:UseSharedCompilation=false",
    "--verbosity",
    "quiet",
  ]);
  await run("dotnet", [importDll, repoRoot, statePath]);
  daemon = monitored(spawn("dotnet", [
    daemonDll,
    "--root", repoRoot,
    "--state", statePath,
    "--client-cultmesh-port", "0",
    "--client-cultmesh-websocket-port", "0",
    "--client-cultmesh-content-port", "0",
    "--client-cultmesh-quic-port", "0",
    "--no-odin-announcements",
  ], { cwd: repoRoot, stdio: ["ignore", "pipe", "pipe"] }));
  const endpoint = await daemon.waitFor("Aetheria client browser CultMesh endpoint: ", 120_000);
  await daemon.waitFor("Aetheria daemon ready; waiting for a client to load or generate a world.", 30_000);
  httpServer = await serve(bundlePath);
  const httpAddress = httpServer.address();
  if (!httpAddress || typeof httpAddress === "string") throw new Error("Browser witness HTTP server has no TCP address.");

  const executablePath = process.env.CHROME_PATH || "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
  browser = await chromium.launch({ executablePath, headless: true });
  const page = await browser.newPage();
  await page.goto(`http://127.0.0.1:${httpAddress.port}/?endpoint=${encodeURIComponent(endpoint)}`);
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

  console.log(JSON.stringify({
    provider: "Aetheria.State.Daemon",
    endpoint,
    surface: "aetheria.hangar",
    loweredBy: "Chromium Eve browser lowerer",
    verseOptions: witness.verseOptions,
    modeButtons: witness.buttons.filter(value => ["TERMINUS", "STARBRIDGE", "ARENA", "LAUNCH"].includes(value)),
    command: {
      id: witness.commandId,
      status: witness.commandStatus,
      receiptSchema: witness.receiptSchema,
    },
    forgedIdentityStatus: witness.forgedIdentityStatus,
  }));
} finally {
  if (browser) await browser.close().catch(() => undefined);
  if (daemon) await stop(daemon.process);
  if (httpServer) await new Promise(resolvePromise => httpServer.close(resolvePromise));
  await rm(workRoot, { recursive: true, force: true });
}

function argumentRoot(name, fallback) {
  const index = process.argv.indexOf(name);
  return resolve(index >= 0 ? process.argv[index + 1] : fallback);
}

function monitored(process) {
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
      waiter.reject(new Error(`Aetheria daemon exited ${code}\n${output}\n${error}`));
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
    const server = createServer(async (request, response) => {
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

async function stop(process) {
  if (process.exitCode != null) return;
  process.kill();
  await new Promise(resolvePromise => process.once("exit", resolvePromise));
}
