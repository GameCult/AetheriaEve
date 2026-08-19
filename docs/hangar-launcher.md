# Hangar Launcher

Status: the shared Hangar, deployment admission, portable Hangar surface, and
Terminus launch/continue path exist. Starbridge and Arena session admission are
the remaining mode bootstrap cuts.
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

Derived state: selected bay, selected mode, preview, fit metrics, affordability,
compatibility warnings, and launch readiness are UI projections. The preview
and metrics never become equipment or deployment owners.
The preview is a standard Eve `world.scene3d` containing one
`world.entity3d` for the selected Hangar ship. The world qualifies its asset
catalog by provider, Verse, authority runtime, record, and Odin rendezvous
routes. When progression is local, those fields identify the local daemon. When
another Verse is selected, they identify the progression authority that supplied
the Hangar and catalog; the routing daemon does not re-author remote assets or
satisfy remote references from a same-named local catalog. The preview carries
the selected loadout identity/equipment projection. It does not read gameplay
frame, entity-view, or zone-render pointers and does not create or load a run.
Each authority publishes its asset manifest and Eve asset catalog during cold
presentation boot, before serving its Hangar. Gameplay topology reuses the same
publication primitive; the first gameplay tick is never a launcher dependency.

Forbidden writers: Unity, Electron, Eve lowerers, mode session checkpoints,
witness seeders, and mode-local inventory stores cannot mutate Hangar assets or
mint accepted deployments. A mode cannot launch from an uncommitted UI
selection.

Shared paths: rendered clients, headless controllers, tests, reconnect, and AI
orchestration all submit the same deployment request and consume the same
receipt. Terminus, Starbridge, and Arena differ by policy and session owner, not
by Hangar schema.

Cut line: Starbridge and Arena must follow the established
`Hangar launch operation -> deployment admission -> mode session bootstrap`
path. Generic New Game must not return as a gameplay-state writer.

## Verification

- duplicate request id returns the original accepted receipt;
- stale Hangar revision, wrong player, unavailable ship, unowned template,
  mismatched hull, unknown mode, or wrong mode policy cannot mutate Hangar;
- accepted receipt contains a loadout snapshot, not a mutable reference to the
  saved template;
- all three modes pass through the same admission primitive;
- flush/reopen preserves the Hangar revision, ship deployment state, and
  receipts;
- a published Eve drag can remove an installed item, restore it at explicit
  cells, launch Terminus from that loadout, and continue the saved run through a
  remote Odin-discovered progression Verse;
- a remote refit concurrent with projection refresh yields either the complete
  earlier projection generation or the complete later generation, never a
  Hangar/loadout/catalog mixture;
- the portable surface exposes owned bays, ship preview, fit summary, existing
  inventory/refit entry, three mode selectors, and Launch;
- on a fresh daemon with no run, the preview lowers one visible entity for the
  selected ship, resolves and downloads its current-platform Unity bundle, and
  follows ship/loadout changes without touching gameplay state;
- attaching a renderer does not change admission output.
