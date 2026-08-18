# Aetheria Verse Client Contract

Aetheria has two deliberately different client boundaries. They must not blur
into an application-owned replica.

## Network consumers

Unity, browser, Electron, TUI, headless agents, and other network consumers use
the generic `CultMeshClient`. They select an Odin rendezvous endpoint, discover
a provider by stable Verse/provider/surface identity, lease typed documents and
collections, and submit typed operations. Physical routes are discovery output,
not application state.

The retained CultMesh session target is the explicit pair `(VerseId,
ProviderRuntimeId)`. The Eve surface provider ID remains a separate UI-owner
identity. Aetheria's Hangar may present only the Verse selector to the player;
the daemon resolves an authoritative runtime advertised by that Verse and opens
the typed target. Lowerers do not guess, duplicate, or persist that derivation.

The Aetheria daemon publishes the complete Eve/CultUI surface. A lowerer mounts
that document and resolves its CultMesh state and operation bindings. It does
not reconstruct menus, inventory panels, Hangar screens, or zone details from
gameplay records. Unity owns rendering and input collection; it owns no gameplay
or UI composition truth.

A network consumer must not:

- open a physical provider endpoint directly;
- maintain a private Aetheria gameplay replica or snapshot loop;
- hard-code a logical Verse identity such as `aetheria.local` for remote play;
- build an Aetheria Eve surface from daemon records;
- mutate provider state except through an accepted typed operation.

Provider restart or route movement is handled by `CultMeshClient` using the
stable identity selected by the application. The consumer may cache downloaded
content and non-authoritative presentation data, but that cache cannot become a
second state owner.

## Local state tools

Only daemon-owned tools that are explicitly operating on local persistence may
open an Aetheria `.cc` path. They use `AetheriaStateNode`, the same state owner
as daemon bootstrap/import/smoke work. That type is not a client facade and is
not distributed to renderers as a shortcut around CultMesh.

A tool observing a running local daemon is still a network consumer: it uses
`CultMeshClient`, the configured Odin, and stable provider identity. Locality
may let CultMesh select an in-process or mapped transport, but it does not
change ownership or grant the tool direct-file command authority.

## Typed domain contract

The default is one canonical document type per gameplay concept. If
`AetheriaRuntimeFooDocument` is canonical state, the daemon commits it and
clients receive that type. A second projection exists only for a named semantic
reason: hidden-information filtering, expensive aggregation, viewport
windowing, SoA/native layout, lossy summaries, or an explicit compatibility
boundary.

Callers should ask CultMesh for a semantic document, query, collection, native
view, or operation handle. They should not manually join frames, record keys,
schema slots, renderer-local indexes, and route decisions to reconstruct one
gameplay value. Generated Aetheria handles may wrap the generic client, but they
must preserve its identity, lifetime, reconnection, and authority semantics.

Input follows the same typed operation boundary. Terminus and Arena reconcile
to daemon/server-authoritative state. Starbridge uses Commander-default
simulation with Pilot correction inside typed Pilot jurisdiction: a valid Pilot
mismatch corrects the provisional Commander result before finality. Pilot output
enters as candidate evidence, never as an already committed peer fact.

## Eve surface boundary

The daemon owns and publishes every Aetheria Eve surface, including Hangar,
mode selection, Verse selection, gameplay, editor, inventory/refit, and compact
TUI variants. `GameCult.Eve.Surface.EveSurfaceDocument` is the single surface
contract. CultMesh owns discovery, transport, leases, state refs, operation
bindings, and routes. Eve lowerers own presentation.

The lowerer-visible test is the contract: two clients can discover the same
surface, render it independently, submit typed commands, observe one canonical
receipt/state result, and reconnect after provider route movement without an
Aetheria-owned endpoint loop or UI reconstruction path.
