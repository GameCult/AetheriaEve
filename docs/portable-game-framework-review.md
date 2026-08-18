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

The CultMesh getting-started series previously stopped before the claimed
outcome: it linked a durable-state test and showed identity-first connection,
but had no command-driven two-runtime program. The repaired series now treats
that executable path as its acceptance test rather than tutorial prose.

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

The generic getting-started command now runs two explicit layers. The first
packs the TypeScript/CultMesh/Eve packages and installs them into an empty
temporary consumer before proving DOM lowering, headless observation,
idempotency, receipt identity, and `.cc` reopen. The second boots a C# provider
that publishes the canonical Eve C# surface type, a local Odin fixture, real
Chromium, and a retained C# client. Chromium invokes the first command; after
the provider moves to a new route, C# invokes the second through
`CultMeshClient.InvokeAsync`. Both retained clients rediscover by identity and
observe the same provider-authored state and receipt chronology. No sample owns
physical route selection or reconnect policy.

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
The same C# probe now writes 16 KiB generations directly into the mapped
triple-buffer hot-body path and reads them through one retained cursor. Over the
full ten-second interval it committed/read 600 timed frames plus bootstrap at
0.71 ms p99, allocated 2.04 MiB, blocked zero writes, and reported zero
unavoidable copies. The convenience span-copy publisher now reports one copy;
direct slot writes report zero, so copy claims are telemetry rather than mood.
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
package-consumer verifier no longer assumes `node.exe` or `npm.cmd`. The same
one-command artifact plus live-network checkpoint is a Windows/Linux CI gate;
a serialized sample build avoids the shared source-generator output race that
the Windows runner exposed.

After the live route-replacement chronology, the retained C# client now sends
10,000 typed no-op operations over the real WebSocket transport. A local full
run completed at roughly 3,144 operations/second and 2.28 ms p99 with 348 KiB
post-GC managed growth; private-memory movement is reported separately. The
provider stores no ping history, which prevents persistence growth from
impersonating a client leak. The gate rejects p99 above 250 ms or post-GC
managed growth above 8 MiB and runs on both CI operating systems.

The August 18 registry check returned `E404` for
`@gamecult/eve-browser-lowering`, `@gamecult/eve-contracts`, and the CultMesh
TypeScript package. The clean-consumer proof therefore packs local checkout
artifacts; it is not a published-npm proof. Publishing and versioning those
packages is a release-authority decision, not something this repair loop can
paper over with another source-path fallback.

The former RTS binding check was also false evidence. Its 1,600-line generator
extracts MessagePack slot names but discards C# property types, emits most
TypeScript declarations from maintained templates, and had already stopped
running after the render-splats contract moved to Eve fields. The checked-in
client also targets a removed CultMesh query-source API, so the deprecated
Stage 7 Electron body is not a current portable-client proof.

The live command boundary now has a focused generated codec instead. Schema
id, enum values, slot numbers, the retired key-20 tombstone, and the 32-slot
array extent come from `AetheriaRuntimeDaemonDocuments.cs`. An executable
binary witness makes C# emit a canonical MessagePack command for TypeScript to
decode and return, then makes TypeScript emit a movement command for C# to
deserialize, validate, canonicalize, and return. Both directions pass locally.
The witness uses an isolated two-package npm consumer and runs in the Windows/
Linux architecture matrix; it does not compile or pardon the obsolete RTS
query/Electron shell.

The generic provider sample no longer implements CultNet as application code.
`CultNetOperationServer` owns operation route and envelope validation,
MessagePack encoding, correlation, and portable framework-failure replies.
The application handler receives one typed request plus its durable
idempotency key and remains the sole owner of domain validation, mutation, and
receipts. Both the C# and browser CultMesh clients turn the same correlated
`gamecult.cultnet.operation_failure.v1` payload into a typed exception carrying
stable status and code; neither waits for a timeout or parses diagnostic prose.
The executable sample rejects any return of raw operation-envelope dispatch.
After this cut the full clean-consumer plus real-network chronology passed with
10,000 retained-session operations at 0.68 ms p99 and 272 KiB post-GC managed
growth.

The provider sample now opens one public `CultMeshNode` for its `.cc` cache and
typed database instead of manually assembling those two halves beside the
framework. The first getting-started chapter uses the same node boundary. The
unchanged full verifier passed after this simplification: the clean packed-
artifact consumer reopened its `.cc` state, and the real Chromium/C# witness
survived route replacement before completing 10,000 operations at 0.74 ms p99,
about 4,144 operations/second, and 207 KiB post-GC managed growth.

The product browser witness previously checked only the outer CultNet record's
declared receipt schema. Once it decoded the payload through Eve's generated
contract, it exposed that C# was emitting `EveCommandReceiptDocument` as a
positional MessagePack array even though the portable contract requires a
keyed object. Eve now owns a compatibility formatter that writes the canonical
string-keyed receipt and navigation maps, omits absent optional navigation,
and reads the legacy 13-slot representation for existing `.cc` state. Focused
wire tests cover canonical shape, optional-field omission, and legacy reads.
The real Aetheria Chromium witness now decodes and validates both pre- and
post-restart receipts, correlates each receipt with its command id, and asserts
the accepted state plus provider and Hangar identities. This closes a false-
evidence hole rather than adding a browser-side dialect.

Eve's renderer-neutral C# contract is now a real local NuGet artifact rather
than tutorial shorthand for a sibling `ProjectReference`. Its packer first
produces CultLib's managed dependency closure, then packs
`GameCult.Eve.Surface` and restores an empty temporary project whose only
GameCult dependency is that package. The consumer builds and executes the Eve
builder from the local feed without either source tree. This .NET artifact gate
now runs inside the same getting-started command before the TypeScript artifact
and live-network layers. The package has not been published to a registry; the
proof establishes closure and install shape, not release availability.

The getting-started chapters now keep the same counter application from the
first `.cc` write through the live Chromium/C# route-replacement proof. The
former fourth chapter switched without warning to a VoidBot TypeScript
provider and made the sequence look compositional when it was not; that
lifecycle material remains in the TypeScript package guide, while chapter 4
now executes and explains the game built by chapters 1–3. Public quickstarts
use `.cc` and no longer present an undefined `playerKey`/`PlayerData` sketch as
runnable code. The exact persistence command printed in chapter 1 passes, and
all relative links in the chapter set resolve.

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
| P0 | Reactive document authority and idle work | C#, TypeScript, and Python expose read-only observed mirrors plus explicit authoritative or prediction writers. The executable scaling gate proves 1, 100, and 1,000 idle documents schedule zero work and editing one percent schedules only that one percent. The shared measured workload records payload, CPU, runtime-appropriate memory, and p50/p95/p99 latency in all three runtimes. Full 1,000-document results were C# 6,000/6,000 at 0.77 ms p99, TypeScript 6,010/6,010 at 0.90 ms, and Python 6,000/6,000 at 11.94 ms with one scheduler thread. The mapped 16 KiB hot-body probe completed 600 timed 60 Hz frames at 0.71 ms p99, 2.04 MiB allocated, zero blocked writes, and zero unavoidable copies. | Run the representative document/body workload through the promoted remote realtime transport and a real renderer cadence. Local mapped bodies now prove copy telemetry and native-slot behavior; they do not prove QUIC backpressure or GPU/native-lowerer cost. |
| P0 | Browser lowerer instance isolation | Eve browser lowering now carries surface/options/styles/component indexes per host, patches only bound component subtrees, and coalesces synchronous binding bursts. A two-host DOM test proves command/asset/skin isolation plus focus, selection, scroll, and root preservation. Eve runs the source build and test on Windows and Linux and rejects generated-output drift. | Add the browser host witness to the released-artifact consumer smoke; source-package CI does not prove published artifact closure. |
| P0 | Conformance witnesses | The static pack resolves repository witnesses and checks schema IDs against typed source. The actual Aetheria browser witness boots the product daemon, discovers and leases the Hangar through a local Odin fixture, lowers it in Chromium, submits the native Verse select command, observes its daemon receipt, rejects a forged client identity, then follows the restarted daemon to a new route and obtains a second receipt through the retained lease. | Repeat the same product chronology through deployed Odin and a retained native consumer; static fixture agreement and the local Odin fixture cannot satisfy those infrastructure/native-runtime proofs. |
| P0 | Stable identity path | Eve Unity discovers and reconnects through `CultMeshClient`; Aetheria's direct `.cc` client facade, client-target sidecar, boot selector, replica worker, SDK-style Unity state reader, and client-built surfaces are deleted. The only player Verse selector is the daemon-published Hangar dropdown backed by `AetheriaProgressionVerseCoordinator`. `CultMeshBrowserOdinRendezvous` gives browsers the same identity-first Verse-catalog boundary and survives a physical provider move. | Generate Aetheria domain handles over the generic client and prove the actual daemon through local and configured Odin routes without restoring an application-owned replica. |
| P1 | Contract ownership | Eve owns the renderer-neutral C# surface contract. A clean Unity consumer passed 136/136 tests against the released CultLib, Eve surface, and EveUnity packages; Aetheria's daemon project now references Eve rather than EveUnity. `GameCult.Eve.Surface` also packs with CultLib's NuGet closure and runs from an empty PackageReference-only .NET consumer. Eve contract and browser packages build/test on Windows and Linux. | Publish/version the verified NuGet artifact and put the released clean-Unity consumer in CI. Local NuGet closure and source package matrices do not prove registry availability or the released Unity graph. |
| P1 | Cross-runtime schema generation | The old RTS generator is quarantined as legacy evidence: it discards C# property types, emits handwritten TS declarations, and no longer generates against the live render-splats/CultMesh query contract. A focused codec now derives the daemon-command schema, enum, slots, tombstone, and array extent from C# and passes an actual C#↔TypeScript MessagePack round trip in both directions. | Move this proof into shared CultMesh schema/IDL generation and cover every promoted document and operation. One command codec proves the wire boundary it names; it does not make the handwritten RTS document catalog generated. |
| P1 | Executable onboarding | One documented command runs an empty PackageReference-only .NET Eve consumer, the empty-consumer packed TypeScript checkpoint, and the real Chromium/C# network checkpoint on Windows and Linux CI. The C# provider uses Eve's canonical surface type. Chromium invokes before provider route replacement; the retained C# client invokes afterward through the generic identity-owned operation API. Both clients rediscover, resubscribe, and converge on the same durable state and receipt ids without application transport loops. The NuGet and npm artifacts are local packs rather than published-registry dependencies, so no registry claim is made. | Choose and execute package publication/version ownership, then rerun the unchanged command from released artifacts; keep the local Odin fixture distinct from deployed-Odin evidence. |
| P1 | Client resource lifetime | `CultMeshClient` exposes disposable document and collection leases, reference-counts dynamic resources, and reports active resource counts. Retained sessions multiplex logical subscribers through one physical callback per message type. The 100,000-distinct-key gate leaves zero resources and exactly two transport callbacks. A separate real-WebSocket gate completes 10,000 correlated operations on the retained post-reconnect C# session at 2.28 ms local p99 and 348 KiB post-GC managed growth while reporting private memory separately. Both gates run on Windows/Linux CI. | Add real-transport dynamic document/collection lease churn and sustained realtime-body delivery. Operation waiters are now measured, but transport subscription churn and long-running QUIC/native consumers remain separate risks. |
| P1 | Long-session state growth | Peer-import chronology is deleted. Indexed committed-command facts and receipts own history; handled Eve invocations and processed daemon commands leave their transient inboxes; hot-frame chronology fields are compatibility tombstones. A 10,000-command smoke holds final serialized frame size within 64 bytes of the first, while the live progression smoke requires each handled Eve request to disappear after its receipt. | Define retention/segmentation for the durable fact and receipt journal, then benchmark total `.cc` growth and restart cost. The hot checkpoint and ingress queues are bounded; the audit store is not yet. |
| P1 | Starbridge finality | The daemon no longer accepts peer committed facts, and the removed CLI lane is rejected explicitly. The authority smoke now enters through canonical Terminus bootstrap instead of treating a rendered frame as durable run state. | Build the typed Pilot candidate, Commander selection/replay, and single-final-log protocol; prove mismatch, late candidate, restart, and non-jurisdiction negatives. |

The generic and product live-network boundaries now have executable witnesses.
The generic witness proves command submission from Chromium and retained C# on
opposite sides of an Odin-mediated provider route replacement. The Aetheria
witness proves the real daemon, retained browser lease, commands, receipts, and
the same physical replacement shape. The remaining product proof is to expose
the Aetheria receipt/state to a retained native consumer and repeat the product
chronology against deployed Odin. Released-registry closure remains blocked on
package publication authority; source witnesses do not launder that gap.
