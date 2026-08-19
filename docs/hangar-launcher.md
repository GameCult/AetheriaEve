# Hangar Launcher

Status: the shared Hangar, deployment admission, portable Hangar surface, and
minimal Terminus, Starbridge, and Arena launch/continue paths exist. Arena
installs its host-authoritative policy with session activation; Starbridge's
pilot-veto policy remains the major mode-authority cut.
The state assembly and viewport queries compile against CultMath `0.1.2`, whose
canonical `rect` stores normalized `min`/`max` bounds.

The Hangar is the launcher and durable preparation space for Terminus,
Starbridge, and Arena. Its composition borrows the useful information geometry
of the early MechWarrior Online MechLab without copying its visual identity.

```text
+-----------------------------------------------------------------------+
| HANGAR                         TERMINUS | STARBRIDGE | ARENA | LAUNCH  |
+----------------------+-----------------------------+------------------+
| selected ship        |                             | fit summary      |
| hull / status        |     authored ship preview   | weapons/support  |
| readiness            |                             | mass/heat/power  |
| deployment policy    |                             | EDIT LOADOUT     |
+----------------------+-----------------------------+------------------+
| OWNED SHIPS: [bay] [bay] [bay] ...                                    |
+-----------------------------------------------------------------------+
```

The ship-bay strip selects one durable Hangar ship. Mode selection chooses one
typed authority/admission policy. `Launch` submits the selected ship, loadout
template, mode, policy id, and expected Hangar revision as one deployment
request. The Hangar owner validates and commits the immutable deployment before
any mode session may instantiate live state.

The Hangar Eve document publishes stored equipment and the selected ship fit as
`inventory.grid` / `inventory.item` components. The source inventory behaves
like the trade menu's stock or cargo source; the ship equipment grid is the
target. Eve lowerers may derive shaped drag ghosts and obvious occupancy
warnings from the published cells. They submit only a revision-bound typed drop;
the Hangar daemon validates placement and commits the loadout. Buy, sell, rotate,
and richer inspection remain extensions of these same refit/trade operations,
not a second fitting model.

The Verse selector is part of this same daemon-published Hangar surface. It
offers Local plus the Verses discovered through the configured Odin. Switching
the selection changes which Verse owns Hangar progression; it does not move
progression authority into the renderer.

Each progression authority publishes one immutable typed Hangar projection
generation containing the Hangar, draft selection, selected loadout, catalog,
and qualified asset source. A routing daemon reads that single generation. It
does not independently sample those records and join them into a surface; a
surface may never combine a Hangar revision from before a refit with a loadout
or catalog from after it.

## Authority Map

Owner: the canonical `gamecult.aetheria.hangar.v1` document owns durable ships,
inventory, currencies, unlocks, template references, revision, and accepted
deployment receipts.

Inputs: authenticated player identity, revision-bound typed refit operations, a
typed deployment request, one owned ship, one owned loadout template, the
selected progression Verse, and the exact mode policy id.

Outputs: committed Hangar/loadout revisions and one immutable accepted
deployment receipt embedded atomically in the new Hangar revision, or a typed
rejection that does not mutate the Hangar. The progression authority also emits
one `gamecult.aetheria.hangar_projection.v1` generation for routers and
lowerers; this projection is derived from one committed state generation and
owns no mutations.

Hangar command finality includes projection finality. The progression authority
commits the canonical mutation, its new projection generation, and the terminal
receipt in one state transaction; that authority's receipt names the projection
generation. A routing daemon must observe at least that generation and, while
the same Verse remains selected, commit a strictly newer Eve Hangar surface.
The client-facing receipt preserves that provider-state generation in
`sourceVersion` and separately names the exact routing-surface lease in
`presentationSurfaceVersion`. `sourceVersion` is causal state provenance; it
must not be reinterpreted as a renderer version. Periodic projection refresh is
recovery and discovery, not the first publisher of command-caused truth.

Verse selection follows the same boundary. The routing daemon prepares the
target authority's projection while the previous Verse and surface remain
canonical and emits no terminal receipt during that preparation. It then
commits the selected progression source, successor Hangar surface, terminal
receipt, and request deletion together. A command issued after an accepted
selector receipt therefore binds to the new surface; the previous surface can
no longer route a post-selection refit or launch. A lowerer exposes that
terminal receipt only after the named surface and its provider-qualified asset
generation are mounted; while preparation is pending or failed, the previous
surface remains visible but read-only. Embedded-surface updates may recompose
that visible tree, but their versions cannot satisfy the advertised Hangar
surface's finality barrier.

Derived state: selected bay, selected mode, preview, fit metrics, affordability,
compatibility warnings, and launch readiness are UI projections. The preview
and metrics never become equipment or deployment owners.
The preview is a standard Eve `world.scene3d` containing one
`world.entity3d` for the selected Hangar ship. The world qualifies its asset
catalog by provider, Verse, authority runtime, record, Odin rendezvous routes,
and exact required asset-catalog version. Each exact catalog lives at an
immutable generation-qualified record key; the mutable latest-catalog record is
discovery only. A lowerer stages the surface and that immutable catalog as one
candidate, reads it once, and lets the bounded presentation retry owner handle a
missing or corrupt record. It never waits for an old number to reappear on the
latest pointer. Once committed, that pinned catalog remains the presentation
owner until a newer base surface declares another generation. When
progression is local, those fields identify the local daemon. When
another Verse is selected, they identify the progression authority that supplied
the Hangar and catalog; the routing daemon does not re-author remote assets or
satisfy remote references from a same-named local catalog. The preview carries
the selected loadout identity/equipment projection. It does not read gameplay
frame, entity-view, or zone-render pointers and does not create or load a run.
Each authority publishes its asset manifest and Eve asset catalog during cold
presentation boot, before serving its Hangar. Gameplay topology reuses the same
publication primitive; the first gameplay tick is never a launcher dependency.
Surface ordering and candidate lifecycle are separate: an older base surface
cannot supersede the newest observed version, while a failed newest asset
candidate retires its own attempt so replaying that same canonical surface can
retry. The candidate lifecycle itself performs bounded cancellable retries of
the exact surface/catalog pair; a newer surface cancels the older retry. Only
policy exhaustion retires the candidate and reports presentation failure. A
failure never forces the provider to mint a fictitious newer surface version
merely to recover presentation.

Forbidden writers: Unity, Electron, Eve lowerers, mode session checkpoints,
witness seeders, and mode-local inventory stores cannot mutate Hangar assets or
mint accepted deployments. A mode cannot launch from an uncommitted UI
selection.

Shared paths: rendered clients, headless controllers, tests, reconnect, and AI
orchestration all submit the same deployment request and consume the same
receipt. Terminus, Starbridge, and Arena differ by policy and session owner, not
by Hangar schema.

Cut line: every mode follows the established `Hangar launch operation ->
deployment admission -> mode session bootstrap` path. Arena launch and continue
atomically select `aetheria.mode.arena.server.v1`; startup derives the same
policy from an active Arena session. Generic New Game must not return as a
gameplay-state writer.

## Verification

- duplicate request id returns the original accepted receipt;
- stale Hangar revision, wrong player, unavailable ship, unowned template,
  mismatched hull, unknown mode, or wrong mode policy cannot mutate Hangar;
- accepted receipt contains a loadout snapshot, not a mutable reference to the
  saved template;
- all three modes pass through the same admission primitive;
- browser, Unity, and headless Eve commands require an established CultMesh
  session identity before journaling; outer and inner caller IDs must match it,
  and the journal derives `ClientId` from that established identity;
- the local daemon binds Hangar mutation to the configured
  `--hangar-principal-runtime-id` (`aetheria-unity` in the Unity launchers).
  Arena controllers never inherit that progression capability: they discover a
  separate lobby surface exposing only `Join Arena`, then receive their scoped
  seat surface. Each Join carries the exact Arena session and run that authored
  it; ingress and the atomic roster mutation both reject an earlier match;
- a routing daemon is accepted by a remote progression Verse only when that
  Verse explicitly configures it as an authenticated progression gateway (or
  later binds an account principal). Forwarding/delegation metadata preserves
  provenance and never grants Hangar authority;
- Arena deployment, active session, and authority policy carry the same
  nonempty policy id; one daemon-owned roster survives reopen, Continue cannot
  steal a seat, and the Arena lobby `Join Arena` command assigns an authenticated
  human or headless AI the next open actor. Admission uses exact operation kinds,
  so target/global administration cannot hide behind a coarse combat claim.
  Launch/join navigation points each controller at a roster-specific Eve pilot
  surface. The roster retains a stable entity identity rather than a zone/index
  key; projection and ingress resolve its current key from the canonical run,
  failing closed on zero or multiple matches. The daemon therefore derives the
  actor from authenticated runtime plus roster at command ingress, ignoring
  caller-supplied actor overrides even after zone transfer or restart. The same
  seat projector owns a visibility-filtered frame, scoped hot body/view,
  zone-render record, and input capability. The CultMesh read gate resolves the
  authenticated peer against the roster for snapshot, subscription, and body
  demand; exact keys for another seat and the global frame fail closed. The
  Arena export boundary is an allowlist, so a canonical run/zone/entity/session
  record or schema-wide subscription cannot escape merely because its key was
  absent from a privacy list. The
  provider advertises only the requesting controller's seat. Explicit snapshots,
  subscription snapshots, live subscription updates, and reconnect all use the
  same authenticated per-peer advertisement projector. One tri-state exposure
  resolver distinguishes non-Arena play, a complete active Arena generation,
  and an active Arena whose session, installed server-authority policy, roster,
  or frame disagree. The active form requires the same policy id, host identity,
  and host-authoritative default as command admission. That invalid state
  fails closed for records, subscriptions, bodies, and provider projection; it
  can never inherit the non-Arena export boundary. The subscription server owns
  a delivered-record ledger for each peer. An Arena exposure-generation change
  reconciles that ledger, emits tombstones for records that lost visibility,
  updates changed per-peer projections, and withdraws body demand before the
  next hot-body publication. The mapped-body publisher is retired before that
  reconciliation; any later regrant creates a fresh mapping capability and
  producer epoch. Retained cursors therefore remain frozen on the revoked
  generation instead of becoming a side door into future frames. A prepared
  frame may commit only while holding the demand tracker's exact generation;
  withdrawal that wins first discards the frame without advancing the mapping.
  Seat
  publication consumes this same resolved context rather than a separately
  cached roster. Arena does not
  use global realtime broadcast until that transport has per-peer identity.
  Headless and graphical controllers therefore observe the same bounded world
  around the ship they can command. Resulting facts
  remain daemon-authored with separate proposer provenance;
- flush/reopen preserves the Hangar revision, ship deployment state, and
  receipts;
- a published Eve drag can remove an installed item, restore it at explicit
  cells, launch Terminus from that loadout, and continue the saved run through a
  remote Odin-discovered progression Verse;
- a remote refit concurrent with projection refresh yields either the complete
  earlier projection generation or the complete later generation, never a
  Hangar/loadout/catalog mixture;
- an accepted remote refit receipt preserves the remote projection generation
  in `sourceVersion` and names the matching routing-surface version in
  `presentationSurfaceVersion`; that surface names the same remote projection
  and the exact asset-catalog generation it requires;
- an accepted Verse-selector receipt is atomic with the selected source and
  a strictly newer successor surface; lowerers do not expose the receipt or
  permit an immediate command until that surface's assets commit, after which
  the command reaches only the selected Verse;
- the portable surface exposes owned bays, ship preview, fit summary, existing
  inventory/refit entry, three mode selectors, and Launch;
- on a fresh daemon with no run, the preview lowers one visible entity for the
  selected ship, resolves and downloads its current-platform Unity bundle, and
  follows ship/loadout changes without touching gameplay state;
- attaching a renderer does not change admission output.
