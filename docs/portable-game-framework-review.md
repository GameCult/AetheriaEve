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
| P0 | Reactive document authority and idle work | The C# reference cut now exposes read-only observed mirrors plus explicit authoritative or prediction writers, and Aetheria consumes observed mirrors. Python still creates recursive timers and TypeScript still uses shallow proxy mutation. | Apply the explicit mutation boundary to TypeScript and Python. Prove near-zero idle work and identical nested-edit traces in all three runtimes. |
| P0 | Browser lowerer instance isolation | Browser lowering stores active surface/options/styles in module globals and rebuilds the whole root for each binding update. | Move all state into one host instance. Add two-host command/asset isolation, focus/scroll preservation, and render-count tests. |
| P0 | Conformance witnesses | Aetheria fixtures still name the legacy repository and stale schemas; the verifier trusts self-declared JSON. | Resolve witnesses against this repository and a running provider, validate schemas, execute a command/receipt/reconnect chronology, and reject unproved lowerer claims. |
| P0 | Stable identity path | `AetheriaRuntimeVerseClient` manually snapshots physical endpoints and uses `aetheria.local`; the intended `CultMeshClient` already owns provider identity and reconnection. | Replace the parallel remote client and client-owned surface reconstruction with generated typed handles over `CultMeshClient`. |
| P1 | Contract ownership | Renderer-neutral C# Eve contracts live inside EveUnity, forcing a headless daemon and CultLib tutorials to depend on a renderer repository. | Move/publish the Eve contract from a renderer-neutral owner and consume it as a released dependency. |
| P1 | Executable onboarding | The getting-started series openly lacks its browser/Electron client and command-driven two-runtime sample. | Build one clean-checkout provider plus two clients, shared receipts/state versions, reconnect, and a forbidden direct-write proof. Use its source as the tutorial. |
| P1 | Client resource lifetime | `CultMeshClient` retains dynamic document resources until the entire client dies. | Add leases/reference counting or bounded eviction and a 100k-key churn memory/subscription gate. |
| P1 | Long-session state growth | Full frames retain cumulative command/imported-fact ID arrays. | Move chronology to indexed append-only records and benchmark checkpoint size over long sessions. |

The active repair is the cross-runtime half of the reactive-document authority
boundary. C# no longer lets a reader acquire mutation polling or lets a generic
setter select an authority path. TypeScript and Python must now converge on the
same explicit update/replace-local contract before this boundary is complete.
