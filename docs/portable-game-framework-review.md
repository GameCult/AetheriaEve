# Portable Game Framework Adversarial Review

This is the working body map for the Eve/CultMesh developer-experience repair.
It records current evidence, the target boundary, and the proof required before
the framework can claim that one multiplayer game travels cleanly between
runtimes.

## Real developer outcome

A developer should be able to define typed game state, start one provider,
publish one Eve surface, connect two players by provider identity, and lower the
same game through two runtimes without writing transport loops, renderer-local
game state, or application-specific surface adapters.

## First fracture: duplicate surface truth

```text
Aetheria typed gameplay documents
-> AetheriaRuntime*SurfaceBuilder
-> gamecult.aetheria.runtime_surface.v1
-> AetheriaRuntimeSurfaceDocuments.ToPortableSurface
-> gamecult.eve.surface.v1
-> CultMesh publication
-> EveUnity / browser / Electron lowering
```

The first two surface documents describe the same UI tree. The Aetheria shape
does not protect hidden information, aggregate expensive state, window a large
collection, or provide a native hot layout. It is a second wire model whose only
job is to become the canonical Eve wire model.

The CultMesh getting-started series also stops before the claimed outcome. It
links a durable-state test, shows an identity-first client, and explicitly says
that the browser/Electron counterpart and a command-driven two-runtime sample
are still missing. That is useful honesty, but not yet onboarding.

### Repaired on 2026-08-17

Aetheria surface builders now emit `EveSurfaceDocument` directly. The cloned
document, tree, component, command, style, and embedded-slot types are deleted,
as is their bespoke MessagePack codec and the portable-surface conversion
bridge. The document registry contains one Eve surface schema. The Hangar's
second writer—which replaced the real Hangar with a renamed gameplay
surface—was deleted at the same boundary.

`scripts/verify-portable-game-framework.ps1` rejects the duplicate schema,
conversion bridge, duplicate registration, and multiple Hangar writers. The
state build, daemon build, typed-state smoke, daemon/Ymir smoke, and authority
smoke build pass after the cut.

### Repaired on 2026-08-18

The actual Aetheria daemon now exposes its CultMesh schema/session endpoint to
browser clients over an anonymous loopback-only WebSocket route. Raw document
records carry both the CultCache content hash and the stable portable schema
name/version, so C#, TypeScript, and browser consumers agree on identity
without pretending a content-derived hash is the public contract name.

The Eve browser lowerer now treats `control.select` as a native interactive
select rather than an inert unknown component. The executable browser witness
boots the real daemon against an isolated imported catalog, discovers and
leases `aetheria.hangar` through the canonical Odin Verse catalog, lowers it in
headless Chromium, changes the Verse selector, submits the resulting typed Eve
intent through CultMesh, and observes the daemon-issued command receipt. It
also forges a different client identity and requires the daemon to deny it
before materializing a canonical command. The witness then restarts the daemon
on another physical WebSocket endpoint, updates Odin at the same rendezvous
identity, and requires the retained browser lease to reconnect, resubscribe,
and obtain a second receipt without learning the replacement endpoint.

Run it from this repository with:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-aetheria-browser-provider.ps1
```

This is a source-tree product witness: it deliberately names sibling CultLib
and Eve roots and uses the executable local Odin fixture. It proves the real
provider/browser boundary and provider route replacement, not released-
artifact onboarding or the deployed Odin service. Anonymous provider transport
is confined to loopback development; the Odin fixture uses its authenticated
host adapter, and a remotely reachable provider still requires one too.

CultMesh reactive documents now perform no polling or serialization while
idle. Python uses one lazy process scheduler instead of creating an operating-
system timer/thread for each dirty document, and C# no longer clones both sides
of a matching canonical echo before discovering that they agree. TypeScript's
headless scheduler uses one cancellable microtask rather than `setTimeout(0)`;
on Windows that cut removed an accidental roughly 16 ms timer floor without
changing the browser's animation-frame batching policy.

One executable probe now applies the same workload to C#, TypeScript, and
Python: hold 1, 100, and 1,000 writable 16 KiB documents idle for ten seconds,
then mutate one percent at 60 Hz for ten seconds. In the full 1,000-document
run, C# published 6,000/6,000 updates at 0.77 ms p99 with a 7.05
allocation-to-payload ratio, TypeScript published 6,010/6,010 at 0.90 ms p99
with a 13.9 MiB heap peak, and Python published 6,000/6,000 at 11.94 ms p99
with a 775 KiB traced-allocation peak and one scheduler thread. Runtime-specific
memory counters are reported honestly rather than pretending GC heap growth,
traced Python allocations, and cumulative .NET allocations are the same unit.
The quick form of this probe runs on Windows and Linux CI and fails on a missed
publication, 250 ms p99, or 128 MiB runtime-appropriate peak; C# also rejects an
allocation-to-payload ratio above 10.

Retained CultMesh sessions now install one physical callback per message type
and multiplex disposable logical subscribers behind it. A 100,000-distinct-key
lease/release test finishes with zero active document/collection resources and
two physical callbacks, not a dead callback pair for every released handle.
These gates and the cross-runtime explicit-update scheduling gate now run on
Windows and Linux CI. Eve contract generation and browser lowering, including
the two-host DOM isolation test, have the same Windows/Linux matrix. The clean
package-consumer verifier no longer assumes `node.exe` or `npm.cmd` and passes
from a fresh temporary install on Windows; Linux execution is now owned by CI
rather than implied by a Windows-only script.

The August 18 registry check returned `E404` for
`@gamecult/eve-browser-lowering`, `@gamecult/eve-contracts`, and the CultMesh
TypeScript package. The clean-consumer proof therefore packs local checkout
artifacts; it is not a published-npm proof. Publishing and versioning those
packages is a release-authority decision, not something this repair loop can
paper over with another source-path fallback.

## Target authority map

Owner:

- the game provider owns canonical gameplay documents, simulation, accepted
  operations, indexed receipts/facts, and the composition of its Eve surfaces;
- Eve owns the one portable surface/command/binding contract;
- CultMesh owns provider discovery, sessions, typed document delivery,
  authority policy, and negotiated hot-body transport;
- each runtime owns native lowering, input sampling, presentation caches, and
  platform lifecycle.

Inputs:

- typed game documents and catalog rules;
- typed operation intents carrying player/session identity;
- transient Eve invocation and translated daemon-command inbox records;
- provider identity plus a configured Odin/rendezvous identity;
- runtime capability advertisements.

Outputs:

- canonical typed gameplay commits and operation receipts;
- canonical `gamecult.eve.surface.v1` documents;
- negotiated typed documents or hot bodies consumed directly by lowerers;
- diagnostics that name provider, Verse, route, authority, schema, and version.

Derived state:

- Eve component trees are intentional UI projections of gameplay state;
- handled invocation documents and translated command documents are transient
  inbox entries; indexed receipts and committed facts own durable idempotency;
- hot-frame and acceptance-status command-id lists summarize only the current
  batch and are not history;
- DOM, UI Toolkit, Electron, TUI, and headless views are renderer-local
  projections;
- endpoint candidates, transport health, and reconnect state are CultMesh
  session state;
- rendered pixels and presentation interpolation never become game truth.

Forbidden writers:

- a renderer may not commit gameplay or repair provider state;
- a game package may not define a second copy of the Eve surface schema;
- application code may not own reconnect loops or physical endpoint ranking;
- client prediction may not silently change the declared authority policy;
- hot frames, status documents, and retained inbox entries may not become
  lifetime command ledgers;
- JSON fixtures and conformance packs may not impersonate a live multiplayer
  path.

Shared paths:

- browser, Unity, Electron, TUI, headless agents, reconnect, replay, and test
  harnesses consume the same typed documents and submit through the same typed
  operation boundary;
- local and remote Verses differ by discovery and authority configuration, not
  by application code or state shape;
- direct input, AI policy input, and automated tests enter through the same
  operation contract.
- replayed or duplicated commands resolve through the same indexed receipt
  identity before any simulation or progression mutation.

Cut line:

1. Delete `AetheriaRuntimeSurfaceDocument` and its cloned component/tree/
   command/style/embedded-slot family.
2. Make every Aetheria surface builder return `EveSurfaceDocument` directly.
3. Remove conversion calls, the duplicate schema registration, and the custom
   duplicate serializer path.
4. Build one command-driven multiplayer sample that is executed—not merely
   excerpted—through two lowerers from a clean checkout.
5. Measure startup, steady-state allocation, update rate, payload size, and hot
   body copy count on the same path used by the sample.

## Completion evidence

- repository search finds no Aetheria-owned surface document schema or
  `ToPortableSurface`/`FromPortableSurface` bridge;
- Aetheria daemon/state/client smokes compile and pass with the canonical Eve
  document registered once;
- an isolated getting-started command starts the provider and two consumers,
  submits commands from both, and observes the same receipt ids and finalized
  state version in both runtimes;
- a negative test proves neither lowerer can mutate provider state without an
  accepted operation;
- a benchmark records startup latency, allocations/update, serialized control
  payload size, updates/second, and copy telemetry for hot bodies;
- all documented commands run from a clean checkout using released or
  explicitly declared local dependencies.

Until those checks pass, portability and ergonomics remain active claims under
test rather than achieved properties.

## Adversarial queue after the first cut

| Priority | Fracture | Current evidence | Required repair/proof |
| --- | --- | --- | --- |
| P0 | Reactive document authority and idle work | C#, TypeScript, and Python expose read-only observed mirrors plus explicit authoritative or prediction writers. The executable scaling gate proves 1, 100, and 1,000 idle documents schedule zero work and editing one percent schedules only that one percent. The shared measured workload records payload, CPU, runtime-appropriate memory, and p50/p95/p99 latency in all three runtimes. Full 1,000-document results were C# 6,000/6,000 at 0.77 ms p99, TypeScript 6,010/6,010 at 0.90 ms, and Python 6,000/6,000 at 11.94 ms with one scheduler thread. | Add the same workload over a real transport and native hot bodies. The local document probes now falsify idle polling and scheduler explosions; they do not measure network backpressure, copy count, or renderer cadence. |
| P0 | Browser lowerer instance isolation | Eve browser lowering now carries surface/options/styles/component indexes per host, patches only bound component subtrees, and coalesces synchronous binding bursts. A two-host DOM test proves command/asset/skin isolation plus focus, selection, scroll, and root preservation. Eve runs the source build and test on Windows and Linux and rejects generated-output drift. | Add the browser host witness to the released-artifact consumer smoke; source-package CI does not prove published artifact closure. |
| P0 | Conformance witnesses | The static pack resolves repository witnesses and checks schema IDs against typed source. The actual Aetheria browser witness boots the product daemon, discovers and leases the Hangar through a local Odin fixture, lowers it in Chromium, submits the native Verse select command, observes its daemon receipt, rejects a forged client identity, then follows the restarted daemon to a new route and obtains a second receipt through the retained lease. | Repeat the same product chronology through deployed Odin and a retained native consumer; static fixture agreement and the local Odin fixture cannot satisfy those infrastructure/native-runtime proofs. |
| P0 | Stable identity path | Eve Unity discovers and reconnects through `CultMeshClient`; Aetheria's local `.cc` facade no longer accepts physical endpoints or constructs client-owned Eve surfaces. `CultMeshBrowserOdinRendezvous` now gives browsers the same identity-first Verse-catalog boundary and survives a physical provider move. | Generate Aetheria domain handles over the generic client and prove the actual daemon through local and configured Odin routes without restoring an application-owned replica. |
| P1 | Contract ownership | Eve `0.3.0` owns the renderer-neutral C# surface contract. A clean Unity consumer passed 136/136 tests against the released CultLib `1.0.45`, Eve surface, and EveUnity packages; Aetheria's daemon project now references Eve rather than EveUnity. Eve contract and browser packages now build/test on Windows and Linux. | Put the released clean-Unity consumer in CI and reject any renderer repository reference from headless daemon projects. Source package matrices are necessary but do not prove the released Unity graph. |
| P1 | Executable onboarding | The artifact-only `samples/eve-two-runtime` verifier installs missing build dependencies, packs the local packages, installs them into an empty temporary consumer, and proves DOM lowering plus a headless observer. Its launcher is OS-neutral and passes on Windows. The separate `samples/eve-browser-network` witness runs a C# provider, local Odin fixture, real Chromium Eve lowerer, and C# observer; it rotates the provider route and proves rediscovery, resubscription, receipts, and durable state. The relevant npm package names are currently unpublished (`E404`), so no published-registry claim is made. | Choose and execute package publication/version ownership, then run both fixtures on Windows/Linux from those released artifacts; add retained C# lease reconnection, and keep the local Odin fixture distinct from deployed-Odin evidence. |
| P1 | Client resource lifetime | `CultMeshClient` exposes disposable document and collection leases, reference-counts dynamic resources, and reports active resource counts. Retained sessions multiplex logical subscribers through one physical callback per message type. The 100,000-distinct-key gate leaves zero resources and exactly two transport callbacks and runs on Windows/Linux CI. | Add a real-transport long-duration memory plateau measurement; structural counts now prove ownership, while process memory remains a separate observation. |
| P1 | Long-session state growth | Peer-import chronology is deleted. Indexed committed-command facts and receipts own history; handled Eve invocations and processed daemon commands leave their transient inboxes; hot-frame chronology fields are compatibility tombstones. A 10,000-command smoke holds final serialized frame size within 64 bytes of the first, while the live progression smoke requires each handled Eve request to disappear after its receipt. | Define retention/segmentation for the durable fact and receipt journal, then benchmark total `.cc` growth and restart cost. The hot checkpoint and ingress queues are bounded; the audit store is not yet. |
| P1 | Starbridge finality | The daemon no longer accepts peer committed facts, and the removed CLI lane is rejected explicitly. The authority smoke now enters through canonical Terminus bootstrap instead of treating a rendered frame as durable run state. | Build the typed Pilot candidate, Commander selection/replay, and single-final-log protocol; prove mismatch, late candidate, restart, and non-jurisdiction negatives. |

The generic and product live-network boundaries now have executable witnesses.
The Aetheria witness proves the real daemon, retained browser lease, commands,
receipts, and one Odin-mediated physical route replacement. The remaining
product proof is to expose the same receipt/state to a retained native consumer
and repeat the chronology against deployed Odin. The generic sample remains
framework evidence, not a substitute for those product/infrastructure gates.
