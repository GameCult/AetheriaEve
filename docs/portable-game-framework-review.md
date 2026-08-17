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

## Target authority map

Owner:

- the game provider owns canonical gameplay documents, simulation, accepted
  operations, receipts, and the composition of its Eve surfaces;
- Eve owns the one portable surface/command/binding contract;
- CultMesh owns provider discovery, sessions, typed document delivery,
  authority policy, and negotiated hot-body transport;
- each runtime owns native lowering, input sampling, presentation caches, and
  platform lifecycle.

Inputs:

- typed game documents and catalog rules;
- typed operation intents carrying player/session identity;
- provider identity plus a configured Odin/rendezvous identity;
- runtime capability advertisements.

Outputs:

- canonical typed gameplay commits and operation receipts;
- canonical `gamecult.eve.surface.v1` documents;
- negotiated typed documents or hot bodies consumed directly by lowerers;
- diagnostics that name provider, Verse, route, authority, schema, and version.

Derived state:

- Eve component trees are intentional UI projections of gameplay state;
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
| P0 | Reactive document authority and idle work | C#, TypeScript, and Python expose read-only observed mirrors plus explicit authoritative or prediction writers. Aetheria consumes observed mirrors. Nested-edit and snapshot-mutation negatives are live, and writable mirrors create no idle detection work. | Add the scaling benchmark gate so allocation, timer/thread creation, and update latency remain visible across runtime changes. |
| P0 | Browser lowerer instance isolation | Eve browser lowering now carries surface/options/styles/component indexes per host, patches only bound component subtrees, and coalesces synchronous binding bursts. A two-host DOM test proves command/asset/skin isolation plus focus, selection, scroll, and root preservation. | Add the browser host witness to CI and the released-artifact consumer smoke. |
| P0 | Conformance witnesses | The static pack now resolves repository witnesses, checks Aetheria schema IDs against typed `CultDocument` source, rejects obsolete daemon IDs and absolute workspaces, and labels its evidence boundary. Phantom game/editor surface schemas were deleted in favor of canonical `gamecult.eve.surface.v1`. | Add the separate live provider command/receipt/reconnect witness; static fixture agreement must never satisfy it. |
| P0 | Stable identity path | Eve Unity now discovers and reconnects through `CultMeshClient`; Aetheria's local `.cc` facade no longer accepts physical endpoints or constructs client-owned Eve surfaces. | Add the live Odin/provider command/receipt/reconnect witness and generate domain handles over the generic client without restoring an application-owned replica. |
| P1 | Contract ownership | Eve `0.3.0` owns the renderer-neutral C# surface contract. A clean Unity consumer passed 136/136 tests against the released CultLib `1.0.45`, Eve surface, and EveUnity packages; Aetheria's daemon project now references Eve rather than EveUnity. | Keep the clean-consumer witness in CI and reject any renderer repository reference from headless daemon projects. |
| P1 | Executable onboarding | The artifact-only `samples/eve-two-runtime` verifier installs packed CultCache/CultNet/CultMesh/Eve browser artifacts into an empty consumer, drives one idempotent command from browser and headless clients, observes the same receipt/state, and reopens persisted state. | Extend the same lesson to a real Odin/network session and a second implementation language; keep the artifact-only smoke honest about that boundary. |
| P1 | Client resource lifetime | `CultMeshClient` now exposes disposable document and collection leases, reference-counts dynamic resources, and reports active resource counts. Churn tests prove released handles leave the cache. | Add the 100k-key allocation/subscription benchmark so the fixed ownership remains visible under load. |
| P1 | Long-session state growth | Peer-import chronology is deleted, but full frames still retain cumulative command-id arrays. | Move remaining chronology to indexed append-only records and benchmark checkpoint size over long sessions. |
| P1 | Starbridge finality | The daemon no longer accepts peer committed facts, and the removed CLI lane is rejected explicitly. The authority smoke now enters through canonical Terminus bootstrap instead of treating a rendered frame as durable run state. | Build the typed Pilot candidate, Commander selection/replay, and single-final-log protocol; prove mismatch, late candidate, restart, and non-jurisdiction negatives. |

The active proof is now the live-network boundary. A
runtime must discover a provider by identity, lower the same Eve surface in
browser and headless consumers, execute a typed command, observe one receipt,
and reconnect without application-owned endpoint loops. The artifact-only
sample is the consumer baseline, not a substitute for that live witness.
