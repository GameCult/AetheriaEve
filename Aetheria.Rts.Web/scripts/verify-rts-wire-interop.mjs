import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";
import { resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
const webRoot = resolve(fileURLToPath(import.meta.url), "..", "..");
const repoRoot = resolve(webRoot, "..");
const project = resolve(repoRoot, "Aetheria.State.WireInterop", "Aetheria.State.WireInterop.csproj");
const assembly = resolve(repoRoot, "Aetheria.State.WireInterop", "bin", "Release", "net10.0", "Aetheria.State.WireInterop.dll");
const generatedSource = resolve(webRoot, "Electron", "aetheria-daemon-command-codec.ts");
const generatedOutput = resolve(webRoot, "wire-dist", "aetheria-daemon-command-codec.js");
const generator = resolve(webRoot, "scripts", "generate-aetheria-command-codec.mjs");
const wireRoot = resolve(webRoot, "wire");
const wireRequire = createRequire(resolve(wireRoot, "package.json"));
const { decode, encode } = wireRequire("@msgpack/msgpack");
const typescript = resolve(wireRoot, "node_modules", "typescript", "bin", "tsc");

execFileSync(process.execPath, [generator, "--check"], { stdio: "inherit" });
execFileSync(process.execPath, [typescript, generatedSource, "--target", "ES2022", "--module", "ES2022", "--moduleResolution", "Bundler", "--strict", "--outDir", resolve(webRoot, "wire-dist")], { stdio: "inherit" });
const {
  AetheriaRuntimeDaemonCommandKinds,
  aetheriaRuntimeDaemonCommandDocumentSlots: slots,
  encodeSetMoveVectorCommand,
} = await import(pathToFileURL(generatedOutput).href);

execFileSync("dotnet", ["build", project, "-c", "Release", "-m:1", "-clp:ErrorsOnly"], { stdio: "inherit" });

const csharpBytes = invoke("emit");
const csharpCommand = decode(csharpBytes);
requireArray(csharpCommand, "C# command");
requireValue(csharpCommand.length, 32, "C# array length preserves the highest MessagePack key");
requireValue(csharpCommand[20], null, "C# key 20 tombstone");
requireValue(csharpCommand[slots.commandId], "wire-csharp-command", "C# command id in TypeScript");
requireValue(csharpCommand[slots.clientId], "wire-csharp", "C# client id in TypeScript");
requireValue(csharpCommand[slots.kind], AetheriaRuntimeDaemonCommandKinds.setMoveVector, "C# command kind in TypeScript");
requireValue(csharpCommand[slots.authorRuntimeId], "wire-csharp", "C# author runtime in TypeScript");
requireValue(csharpCommand[slots.subjectKey], "entity:wire-csharp", "C# authority subject in TypeScript");
requireValue(csharpCommand[slots.claimKind], "movement", "C# authority claim kind in TypeScript");
invoke("verify-csharp", encode(csharpCommand));

const typescriptCommand = encodeSetMoveVectorCommand(
  "wire-typescript-command",
  "2026-08-18T12:00:01.0000000Z",
  "wire-typescript",
  {
    actorEntityKey: "entity:wire-typescript",
    directionX: -0.125,
    directionY: 0.625,
    scalar: 0.875,
    observedFrameId: 42,
  },
);
const canonicalBytes = invoke("verify-typescript", encode(typescriptCommand));
const canonicalCommand = decode(canonicalBytes);
requireArray(canonicalCommand, "canonical TypeScript command");
requireValue(canonicalCommand.length, 32, "C# canonicalized TypeScript array length");
requireValue(canonicalCommand[slots.commandId], "wire-typescript-command", "TypeScript command id after C# round trip");
requireValue(canonicalCommand[slots.directionX], -0.125, "TypeScript direction X after C# round trip");
requireValue(canonicalCommand[slots.directionY], 0.625, "TypeScript direction Y after C# round trip");
requireValue(canonicalCommand[slots.scalarValue], 0.875, "TypeScript scalar after C# round trip");
requireValue(canonicalCommand[slots.authorRuntimeId], "wire-typescript", "TypeScript author runtime after C# round trip");
requireValue(canonicalCommand[slots.subjectKey], "entity:wire-typescript", "TypeScript authority subject after C# round trip");
requireValue(canonicalCommand[slots.claimKind], "movement", "TypeScript authority claim kind after C# round trip");

console.log("Aetheria C# <-> TypeScript MessagePack command interop passed.");

function invoke(mode, bytes) {
  const args = [assembly, mode];
  if (bytes)
    args.push(Buffer.from(bytes).toString("base64"));
  return Buffer.from(execFileSync("dotnet", args, { encoding: "utf8" }).trim(), "base64");
}

function requireArray(value, label) {
  if (!Array.isArray(value))
    throw new Error(`${label} was not a MessagePack array.`);
}

function requireValue(actual, expected, label) {
  if (!Object.is(actual, expected))
    throw new Error(`${label}: expected ${String(expected)}, received ${String(actual)}.`);
}
