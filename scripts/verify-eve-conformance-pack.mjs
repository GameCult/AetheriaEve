import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "..");
const pack = readJson("conformance/eve/provider-pack.json");

assert.equal(pack.schema, "gamecult.eve.provider_conformance_pack.v1");
assert.equal(pack.providerId, "aetheria");
assert.equal(pack.ownerRepo, "AetheriaEve");
assert.equal(pack.evidenceLevel, "static-contract-fixture");

const contractSources = [
  ...walkCs("Aetheria.State/Documents"),
  ...walkCs("Packages/org.gamecult.aetheria.state/Runtime"),
];
const sourceText = contractSources.map((file) => fs.readFileSync(file, "utf8")).join("\n");
const declaredSchemas = new Set(
  [...sourceText.matchAll(/\[CultDocument\(\s*"[^"]+"\s*,\s*(?:[A-Za-z0-9_.]+\.)?"([^"]+)"\s*\)\]/g)]
    .map((match) => match[1]),
);

const advertisement = readJson(pack.advertisementPath);
assert.equal(advertisement.providerId, pack.providerId);
assert.equal(advertisement.evidence?.level, "static-contract-fixture");
assert.match(advertisement.evidence?.limitations ?? "", /does not prove/i);
assertNoAbsoluteWorkspacePaths(advertisement, pack.advertisementPath);

for (const schemaId of advertisement.schemas) {
  assert.doesNotMatch(schemaId, /^aetheria\.runtime\.daemon\./, `Obsolete daemon schema: ${schemaId}`);
  if (schemaId.startsWith("aetheria.") || schemaId.startsWith("gamecult.aetheria.")) {
    assert.ok(declaredSchemas.has(schemaId), `Advertised Aetheria schema has no typed CultDocument: ${schemaId}`);
  }
}

for (const witness of advertisement.witnesses) {
  if (witness.kind === "repo" || witness.kind === "source") {
    const resolved = resolveInsideRoot(witness.ref);
    assert.ok(fs.existsSync(resolved), `Unresolved ${witness.kind} witness: ${witness.ref}`);
  }
}
for (const contact of advertisement.contacts ?? []) {
  if (contact.kind === "repo") {
    assert.ok(fs.existsSync(resolveInsideRoot(contact.path)), `Unresolved repo contact: ${contact.path}`);
  }
}

const requiredSurfaces = new Map([
  ["aetheria.pilot", "pilot"],
  ["aetheria.starbridge.commander", "commander"],
  ["aetheria.daemon.editor", "operator"],
]);
for (const [surfaceId, audience] of requiredSurfaces) {
  const surface = advertisement.surfaces.find((candidate) => candidate.surfaceId === surfaceId);
  assert.ok(surface, `Missing advertised surface: ${surfaceId}`);
  assert.equal(surface.audience, audience);
  assert.ok(sourceText.includes(`"${surfaceId}"`), `Advertised surface is absent from typed source: ${surfaceId}`);
  assert.equal(surface.worldInteraction?.commandBoundary, "aetheria.daemon.commands");
  assert.equal(surface.worldInteraction?.receiptSchema, "gamecult.eve.command_receipt.v1");
  assert.ok(
    !Object.hasOwn(surface.worldInteraction ?? {}, "loweringTargets"),
    `${surfaceId} must not present compatibility as verified lowering evidence`,
  );
}

const pilotSurface = advertisement.surfaces.find((surface) => surface.surfaceId === "aetheria.pilot");
assert.match(pilotSurface.summary, /Terminus and Starbridge/);
assert.doesNotMatch(pilotSurface.surfaceId, /starbridge|terminus/);

for (const fixture of pack.fixtures) {
  const surface = readJson(fixture.surfacePath);
  const metadata = readJson(fixture.metadataPath);
  assert.equal(surface.providerId, pack.providerId);
  assert.equal(metadata.fixtureId, fixture.fixtureId);
  assert.equal(metadata.ownerRepo, pack.ownerRepo);
  assert.equal(metadata.evidenceLevel, "static-contract-fixture");
  assert.match(metadata.limitations ?? "", /shape only/i);
  assert.equal(metadata.surface.path, fixture.surfacePath);
  assertNoAbsoluteWorkspacePaths(surface, fixture.surfacePath);
  assertNoAbsoluteWorkspacePaths(metadata, fixture.metadataPath);
}

for (const scenarioPath of pack.scenarios) {
  const scenario = readJson(scenarioPath);
  assert.equal(scenario.providerId, pack.providerId);
  assert.equal(scenario.ownerRepo, pack.ownerRepo);
  assert.equal(scenario.advertisementPath, pack.advertisementPath);
  assert.equal(scenario.evidenceLevel, "declarative-scenario");
  assert.equal(scenario.executable, false);
  assert.match(scenario.purpose, /^Describes /);
  for (const receipt of scenario.expectedReceipts ?? []) {
    assert.equal(receipt.ownerRepo, pack.ownerRepo);
    assert.equal(receipt.schema, "gamecult.eve.command_receipt.v1");
  }
  assertNoAbsoluteWorkspacePaths(scenario, scenarioPath);
}

for (const assetPath of pack.assets) readJson(assetPath);
console.log(`Aetheria Eve static contract pack passed: ${path.join(root, "conformance/eve/provider-pack.json")}`);
console.log("Evidence boundary: source/fixture agreement only; no live provider or lowerer claim was made.");

function readJson(relativePath) {
  const absolutePath = resolveInsideRoot(relativePath);
  assert.ok(fs.existsSync(absolutePath), `Missing provider-pack path: ${relativePath}`);
  return JSON.parse(fs.readFileSync(absolutePath, "utf8"));
}

function walkCs(relativeDirectory) {
  const directory = resolveInsideRoot(relativeDirectory);
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walkCs(path.relative(root, absolute)));
    else if (entry.isFile() && entry.name.endsWith(".cs")) files.push(absolute);
  }
  return files;
}

function resolveInsideRoot(relativePath) {
  assert.equal(typeof relativePath, "string");
  assert.ok(relativePath.length > 0, "Empty repository path");
  assert.ok(!path.isAbsolute(relativePath), `Repository evidence must be relative: ${relativePath}`);
  const resolved = path.resolve(root, relativePath);
  assert.ok(resolved === root || resolved.startsWith(`${root}${path.sep}`), `Path escapes repository: ${relativePath}`);
  return resolved;
}

function assertNoAbsoluteWorkspacePaths(value, sourcePath) {
  const serialized = JSON.stringify(value);
  assert.doesNotMatch(serialized, /[A-Za-z]:\\\\Projects\\\\/i, `Absolute workspace path in ${sourcePath}`);
}
