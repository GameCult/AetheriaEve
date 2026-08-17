# Aetheria Verse Client Contract

Aetheria has two deliberately different client boundaries. They must not blur
into an application-owned replica.

## Network consumers

Unity, browser, Electron, TUI, headless agents, and other network consumers use
the generic `CultMeshClient`. They select an Odin rendezvous endpoint, discover
a provider by stable Verse/provider/surface identity, lease typed documents and
collections, and submit typed operations. Physical routes are discovery output,
not application state.

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

`AetheriaRuntimeVerseClient` is the explicit local `.cc` boundary. It opens a
caller-supplied state path with the runtime contract registry for daemon-adjacent
tools, importers, smokes, and local state inspection. Its `aetheria.local`
context describes that local file-backed process only.

```csharp
using var client = await AetheriaRuntimeVerseClient.OpenAsync(
    statePath,
    runtimeId: "state-inspector");

using var frames = client
    .WatchRecord<AetheriaRuntimeDaemonFrameDocument>(
        AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest)
    .Subscribe(change =>
    {
        var frame = change.Document;
        if (frame != null)
            Inspect(frame);
    });
```

The local facade exposes typed records and headless domain projections. It does
not expose remote endpoint, remote refresh, remote shard, or client-side Eve
surface construction APIs. If a tool needs a remote provider, it uses
`CultMeshClient` like every other network consumer.

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
