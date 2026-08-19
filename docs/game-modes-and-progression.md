# Aetheria Game Modes And Progression

Status: adopted design; the daemon-owned Hangar and minimal three-mode deployment slice are implemented

## Product Modes

The first product spine contains three named modes. All three must remain
runnable at a basic level from the start; depth may arrive unevenly, but no mode
is deferred behind a mode-specific rewrite.

`Terminus` is Aetheria's single-player roguelike mode. A run begins from a
hangar-selected ship and loadout, crosses a daemon-generated sequence of
encounters and locations, and ends through extraction, victory, failure, or
abandonment. Its local daemon is the sole gameplay authority. Its minimum
complete loop is:

```text
deploy -> fly -> fight -> salvage -> dock/refit -> choose risk -> continue
       -> extract or die -> settle once into the Hangar
```

`Starbridge` is the cooperative mode. One commander and one to four pilots act
in one shared session. Starbridge uses commander-default simulation with
jurisdictional pilot correction. The Commander daemon simulates the entire
session and owns canonical finality, persistence, and publication. Each Pilot
daemon independently predicts facts for its own ship, its daemon-assigned
nearest environment entities, and its assigned combat engagements. When a
validated pilot result differs from the Commander daemon's provisional result,
the pilot result wins and the Commander daemon corrects and deterministically
replays from that boundary. The Commander player owns Verse authorship for
everything outside pilot jurisdiction. It uses the same player identity, hull catalog,
loadout templates, fitting rules, and Hangar progression as Terminus while
adding the shared base, RTS projection, waves, fabrication, support systems,
and recovered-technology decisions.

`Arena` is the PvP and competitive game-simulation mode. Arena is
server-authoritative: the server owns admission, world state, simulation,
rules, scoring, and outcomes.
Human players and AI controllers are clients of the same typed observation and
operation contracts. It is the primary harness for training and evaluating NPC
agent policies and for running build-versus-build balance matrices under exact
rules, seeds, and loadouts. An AI may run faster than real time or in a batch
harness, but it cannot mutate simulation state through a privileged in-process
shortcut. Arena admission policy may restrict, normalize, loan, or price
eligible Hangar equipment without forking the inventory/loadout schema or
trusting a client-authored ship.

Headless execution is a capability of the shared simulation, not an Arena-only
implementation. Terminus, Starbridge, and Arena can all run without renderers
for regression, agent development, batch simulation, and operational hosting.
Attaching or removing Unity, Electron, or another Eve lowerer cannot change the
authoritative result.

The tutorial is onboarding, not a progression authority or a fourth inventory.
It may constrain a deployment, teach a Terminus-style run, and grant rewards
only through the same settlement path as every other mode.

## Authority Map

- **Owner:** one authoritative typed `Hangar` aggregate owns durable cross-mode
  progression for a player: owned hulls, stored equipment and cargo, currencies
  and materials, unlocks, saved loadout templates, and docked ship
  configuration.
- **Inputs:** authenticated player identity, accepted typed Hangar operations,
  the typed catalog, and terminal settlement requests carrying session id,
  deployment id, terminal fact id, mode-policy/version identity, and proposed
  Hangar delta.
- **Outputs:** canonical Hangar state, saved loadout templates,
  eligibility/affordability facts, and canonical settlement receipts and
  chronology.
- **Derived state:** Eve menus, inventories, compatible-slot lists, run
  summaries, scoreboards, and Unity/Electron presentation. A loadout template
  is a recipe over canonical catalog keys, not a second inventory or a live
  entity graph.
- **Forbidden writers:** Unity, Electron, AI controllers, Eve lowerers, proof
  scenario seeders, run checkpoints, mode authorities, and mode-specific
  bootstrap code cannot apply Hangar deltas or mint accepted settlement
  receipts. The Hangar cannot rewrite a live session after deployment.
- **Shared paths:** every mode reads Hangar state by identity, requests
  admission through a typed Deployment, and submits terminal outcomes through
  the same Settlement boundary. Hangar refit and template edits use Hangar
  operations. In-session dock/refit uses session operations and crosses the
  boundary only at settlement. Resume reopens Hangar or session state by
  identity and cannot redeploy or resettle.
- **Deletion line:** no `TerminusHangar`, `StarbridgeHangar`, Arena inventory,
  client-local unlock store, or mode-private loadout authority may exist.
  Fixture-only mutations remain confined to explicitly named witness profiles
  and are structurally unable to settle progression.

## Session Ownership

A mode admission authority validates a typed deployment request against a
versioned Hangar snapshot and mode policy, then emits an immutable committed
Deployment receipt/snapshot. The Hangar owns available durable assets and
configuration; it does not own Arena admission or Starbridge role assignment.

A mode session owns its live world: instantiated ships and equipment, damage,
temperature, energy, cargo gathered during the session, encounter state,
score, mode-local stock, and outcome. Starting a mode instantiates session state
from one committed deployment. After launch, changing the Hangar does not reach
into the live world.

The authority policy differs without changing the document model:

- **Terminus:** the local daemon decides all gameplay state.
- **Starbridge:** the Commander daemon owns default simulation and canonical
  persistence. Pilot daemons have prediction authority plus mismatch priority
  inside typed jurisdictions; the Commander daemon reconciles to a validated
  differing pilot result rather than correcting the pilot to its own result.
- **Arena:** the authoritative server decides all gameplay state; humans and
  AIs submit operations and consume observations. The current minimal Arena
  bootstrap installs `aetheria.mode.arena.server.v1` atomically with deployment
  activation. One daemon-owned roster assigns stable entity identities and exact operation kinds
  to controller runtimes. Launch may fill the first seat once; Continue cannot
  rewrite it. Additional humans and headless AIs request the next open seat
  through the same Hangar Eve command, without choosing their own actor. Each
  active seat receives a daemon-authored Eve pilot surface whose player,
  camera, controllable entity, hot entity body, visible contacts, zone-render
  document, and input capability are derived from that roster assignment. Each
  seat has its own observation record namespace and visible-world projection.
  Names are not capabilities: the CultMesh snapshot/subscription/demand gate
  binds every private record and body request to the transport-established
  controller runtime and current roster. Global advertisements omit all Arena
  seats and the global pilot surface; an authenticated provider snapshot adds
  only the caller's seat. The full daemon frame, primary-pilot records, command
  journal, and global fact stream are not Arena controller observations; a
  controller can read only receipts whose immutable command envelope names
  that controller. Arena export is default-deny: canonical run, zone, entity,
  session, roster, player, and progression records stay inside the operational
  cache. Only typed presentation assets, the provider advertisement, Hangar
  surface, and the caller's exact seat/receipt records cross the public
  CultMesh boundary. Arena realtime broadcast is disabled
  until the QUIC path can identify and filter each receiving controller; local
  headless and rendered clients consume the same authenticated document/body
  boundary meanwhile.
  At projection and ingress the daemon resolves that stable identity against
  the canonical run to obtain the entity's current zone/index key. Movement,
  zone transfer, compaction, and restart therefore cannot transfer a seat when
  another entity inherits its old positional key. A missing or duplicate stable
  identity fails closed. A stale/global surface or forged entity payload cannot
  choose another seat. Those operations remain proposals: the
  daemon validates and simulates them, authors the committed fact, and records
  the proposing runtime separately. Controller identity is never rewritten as
  host identity, and no controller may author canonical facts directly.
  Headless AI policies use those same actor operations; the `ai` claim remains
  reserved for the daemon-owned run-wide agent-task scheduler.

Each mode is also a concrete CultMesh authority proof:

- **Terminus proves local authority:** one local daemon owns simulation and
  finality while clients remain replaceable lowerers.
- **Arena proves server authority:** remote human and AI controllers submit to
  one authoritative simulation server.
- **Starbridge proves mixed authority:** the Commander daemon owns the default
  simulation and final log while validated Pilot predictions correct facts in
  explicitly assigned jurisdictions.

After Arena server authority and Starbridge mixed authority are proven in live
play and deterministic replay, the next authority milestone is
**witness-authoritative** operation. Witness authority must reuse the same typed
fact slots, inputs, versions, evidence, finality, and replay contracts. It is
not permission to fork gameplay state or build consensus machinery before the
three product modes work.

The exact deployment/risk policy belongs to typed mode policy. Terminus may
risk the deployed hull, insured value, carried equipment, extracted salvage,
or some combination without changing the ownership model. Starbridge may loan
or share session equipment. Arena may normalize deployments. In every case,
the policy is evaluated by the applicable authority and reported in the
deployment and settlement receipts, not inferred by a menu.

## First-Wave Vertical Slices

Each mode must prove the shared spine early:

1. **Terminus:** deploy, fly, fight, salvage, dock/refit, and settle one run.
2. **Starbridge:** one commander and one pilot share a base/session, disagree on
   one pilot-jurisdiction fact, select the pilot result, replay Commander state,
   and converge on one finalized outcome.
3. **Arena:** start a deterministic dedicated-server match, attach at least two
   controllers (human or AI), exchange typed observations/operations, resolve a
   scored combat outcome, and reproduce it from the same seed and inputs.

Arena is the canonical competitive simulation harness for AI development and
balance work. Batch and accelerated execution use the same server simulation,
admission policy, equipment rules, and command gate as live PvP. Controllers
can pit NPC policy versions against each other, compare ship/loadout builds, and
produce scored replayable outcomes. Renderers are optional consumers; headless
execution is not a separate gameplay implementation.

Run-local loot and credits do not become durable merely because a client can
display them. A mode authority submits a terminal outcome and proposed delta;
the Hangar validates identity, policy, and idempotency before committing it.
Every accepted terminal outcome—extraction, victory, failure, or abandonment—
produces exactly one canonical Settlement receipt, even when its delta is zero.
Retries return the existing receipt. A session without an accepted terminal
fact produces none. Reconnect and restart resume canonical Hangar or session
state; they do not replay rewards.

## Terminus Product And Witness Profiles

Terminus is the product mode. Its standard run is not a fixture. The existing
deterministic scenarios remain useful subordinate verification profiles:

- `released-client-proof` creates controlled combat and salvage conditions;
- `cargo-capacity-rejection-proof` creates a full-cargo rejection condition.

Those profiles exercise the same Terminus simulation, Eve surfaces, operations,
and Unity lowering as the product. They use distinct run identities and cannot
write Hangar progression. A proof profile may simplify content; it may not own
a parallel gameplay path.

## Current Migration Seam

The daemon now owns one typed progression-source selection plus the local
Hangar and a separate typed Hangar draft. Durable Hangar state owns ships,
equipment, templates, deployments, and revision; the draft owns only the
currently selected ship, mode, and Hangar view. It publishes `aetheria.hangar` as
the ordinary entry surface. Its Eve `control.select` lists **Local** first and
then every stable Verse identity discovered through the configured Odin
rendezvous endpoints. Discovery refreshes while the daemon is ready in the
Hangar, so a Verse may appear without restarting the client. The persisted
selection, not renderer state, decides
which Verse supplies Hangar and progression truth.

Local selection reads and writes the daemon's moddable `.cc` state. A remote
selection reads that Verse's typed Hangar/catalog documents and forwards equip,
remove, selection, launch, and continue operations to that Verse's authority.
Launch consumes the daemon-owned draft and instantiates its selected committed
loadout into a newly identified Terminus, Starbridge, or Arena session. The
accepted Deployment receipt owns that run identity and exact run-record key;
Hangar mutation, receipt, immutable loadout snapshot, generated run, and active
`GameSession` enter one staged batch under the state node's single
mutation-and-commit boundary. The executing async flow reads its buffered
overlay while every other reader and observer remains on the prior committed
generation. Catalog and name-corpus records are hydrated at daemon boot;
backing-store hydration cannot run inside a mutation transaction and waits for
any active transaction before publishing records or observers. Command ingress
and periodic multi-document publication use that same owner and cannot flush
the batch halfway through. Each Eve command owns its own finality transaction;
one poison request cannot roll back or starve unrelated commands. Unexpected
generation or finality failure escapes that command transaction and leaves only
that command pending; it cannot become a denial receipt around partially staged
deployment state. Eve surfaces are derived after canonical command finality and
cannot rewrite the accepted state if projection refresh fails. Aetheria exposes only a
snapshotting read facade for cache inspection, and its database rejects
authoritative record writes outside the state-node transaction.
CultCache directory storage writes immutable generation pages and exposes the
batch with one atomic manifest swap; readers hold the selected generation
through complete page hydration, then release it before publishing observers.
A failed write reopens entirely before or after the deployment, never between
them. `ActiveRunKey` is only a derived convenience pointer.
Continue resolves the receipt-owned run instead of guessing through that global
pointer, and reopens only a deployment matching both selected ship and mode.
The active `GameSession` owns live run identity. A prior daemon frame is reusable
only when its run ID matches that session; it cannot resurrect the previous run
after a new launch. The three modes
already share this minimal headless deployment boundary. Their deeper rules,
network admission, and settlement remain mode-owned work.
The accepted Eve receipt carries the selected Verse, its Odin-discovered
rendezvous route, and `aetheria.pilot` as a renderer-neutral navigation target.
EveUnity prepares the destination beside the mounted Hangar, tries every
receipt-carried Odin endpoint, and lowers it under a separate inactive scene
root. The old host, runtime, scene root, and presentation remain mounted until
the candidate is ready. Before the provider route changes, the old root is
quiesced so its shutdown callbacks still address the old Verse. The route then
commits and the candidate wakes against the new Verse; the old presentation is
retained only as rollback state until finalization.
Preparation or mount failure discards the candidate without remounting or
reconstructing the prior surface. A failed route therefore cannot strand the
client without a usable Hangar. Aetheria's Unity
shell has no Verse, Hangar, or navigation policy code.

Remote Verse discovery, submission, and receipt waiting run in a bounded
per-command forwarding worker, never inside the state mutation gate or the
simulation tick. The first attempt commits only the immutable route pin. A
remote receipt later enters one short finality transaction with the local
receipt and inbox deletion. Timeout leaves that request pending without
blocking local commands, ticks, or other state writers.
Every Hangar control and inventory-drop target, including the Verse selector, carries a
provider-issued Hangar surface version plus Verse, authority, and source-revision
hints. Admission resolves the canonical tuple from the daemon's stored Eve
projection, rejects stale versions or altered hints, and only then preserves
that provider-owned binding in the immutable command journal. Verse selection
validates the same projection but remains a local routing operation; it cannot
be delegated to the selected progression Verse.
Semantic operands such as ship, deployment, mode, and expected Hangar revision
are explicit `payload.*` properties in that Eve projection. Labels, selected
state, disabled state, and status remain presentation properties; lowerers do
not infer command payloads from them.
The central ship preview is an inline standard Eve world/entity projection
derived from the selected Hangar ship, loadout, catalog, and asset manifest. It
never reads the active gameplay frame and cannot activate a run.
Client asset topology is a daemon-boot publication owned independently of any
mode session, so a fresh Hangar can resolve its preview before Launch.
Classification and route creation read that envelope, never the dropdown's
current value. A command targeting `Local` executes against the routing
daemon's local store; a command targeting the daemon's own Verse executes
there; only a foreign Verse enters forwarding. If the advertised target is no
longer resolvable, the command remains pinned and fails closed rather than
falling through to another Verse or another authority advertising the same
Verse.
The receipt must match the immutable request and pinned Verse/authority route;
a typed document under the expected key with another command, provider,
surface, authority, or navigation Verse is rejected and leaves the request
retryable. Its navigation target preserves the pinned authority runtime;
discovery may verify that runtime but cannot select a different daemon merely
because it advertises the same Pilot surface. Hangar surface publication has one coalescing daemon owner. It reads
the selected progression source outside the mutation transaction, then commits
only if no newer Hangar mutation overtook that candidate. Commands mark the
projection dirty and never launch their own surface publisher, so a delayed
remote read cannot overwrite a newer selection. Odin discovery owns only the
available-Verse observation. It merges observations from configured Odin
endpoints by stable Verse identity while preserving every advertised authority
runtime as a distinct possible supplier. It cannot write or roll back `SelectedVerseId`;
an availability failure for an old selection likewise cannot poison the new
selection.

Inventory ghost placement is lowerer-local presentation only. Drag and click
gestures emit the provider-advertised typed refit operation even when the local
preview predicts rejection; only the progression authority's receipt accepts
or denies the mutation.

An Eve `pending` receipt is an observation, not finality. The client retains
the immutable command, retries, and exact receipt lease until the provider
publishes a matching terminal `accepted`, `denied`, or `reconciled` receipt.
A matching terminal receipt ends delivery retries immediately, but its optional
`presentationSurfaceVersion` independently gates visible finality. When that
version is newer than the mounted advertised base surface, the lowerer retains
the receipt lease, freezes obsolete controls, and exposes terminal finality
only after that exact base surface and its immutable generation-qualified asset
catalog record commit together. Mutable latest-catalog records are discovery
pointers and cannot satisfy an exact presentation lease. `sourceVersion`
remains provider-state causality; receipt
arrival and composed embedded-surface versions do not own presentation
finality.
For a remote progression Verse, the local router first verifies that terminal
receipt against the pinned remote authority, then publishes a client-facing
receipt under its own local authority. The navigation target retains the remote
authority that owns the launched run; Unity does not relax its trust boundary
to accept cross-authority receipts on a local subscription.
The forwarded Eve invocation carries a typed delegation record containing the
original client and invocation hash. The remote receipt binds finality to that
delegated envelope; the local client-facing receipt restores the original
invocation hash. The router rejects a same-ID receipt for either a different
local payload or a different delegated envelope before re-enveloping it.

The public CultMesh document boundary is command-only. It decodes the registered
typed `EveSurfaceCommandRequest`, requires the exact command record key, and
binds `ClientId` to the runtime identity established for that transport session;
it cannot apply arbitrary raw document puts to Hangar, draft, run, or policy
state. Admission durably binds command ID and payload hash to the provider-owned
Verse, exact authority runtime, and progression-source revision resolved from
the referenced Eve surface; client payload strings are consistency hints, not
route authority. Forwarding verifies that authority and pins the Odin endpoint set. A later dropdown
change affects only commands authored from the newly published surface. The pending command
retries that same target until its canonical receipt arrives;
the forwarding daemon cannot manufacture a denial after the remote authority
may have committed. Remote providers validate every Odin-issued route grant
against the configured root, provider key, endpoint, generation, protection,
and expiry before opening a public listener.

This is the first vertical slice, not the completed cross-mode progression
machine. Session runtime identity is not an authenticated player/account
principal. Consequently Local is the proven progression mode, while a
production GameCult Verse still requires authenticated account binding and
per-principal Hangar/draft record ownership before it may expose player
progression. Non-loopback daemon publication currently fails closed at startup
until that principal boundary exists. Arena deployments carry
`aetheria.mode.arena.server.v1`, install the matching host-finality policy, and
create one authoritative session roster in the same transaction. The roster,
not Continue or a client-supplied actor id, owns controller assignment.
Starbridge still leaves `ModePolicyId` empty until its Pilot-correction protocol
is installed.
Settlement, currencies/unlocks, richer fitting
interaction, complete Starbridge admission, and the Arena scored match harness
still need to enter
the same Hangar boundary. The old main-menu
`New Game` writer is no longer a product run-creation authority. Explicit
Terminus proof profiles remain verification-only inputs.

Arena still lacks its complete match/score document family and public
observation/operation replay witness. Its minimal daemon session now preserves
  multiple controller seats, stable entity identity across positional reindex,
  per-seat Eve pilot surfaces and visibility-filtered observation bodies, roster-backed
  snapshot/subscription admission, roster-derived command actors, and the
  operation/fact split in managed verification, but does
  not yet prove a complete PvP or balance run through a real transport. Starbridge's
minimal session bootstrap likewise does not by itself prove pilot-veto
convergence.

The next state-architecture cut is exactly-once Settlement plus Starbridge
admission and the Arena match/score/replay family. Until those cuts land, documentation
and verification must not describe the local starter Hangar as completed
cross-mode progression, or a generic simulation smoke as Arena authority proof.

## Required Proofs

1. A saved Hangar loadout deploys into Terminus and Starbridge and enters Arena
   admission through the same typed operation and catalog keys.
2. A Terminus run completes the playable loop, settles exactly once, and a new
   session observes the resulting durable Hangar state.
3. Failure, abandonment, reconnect, and daemon restart cannot duplicate rewards
   or resurrect losses.
4. Witness profiles cannot mutate Hangar progression.
5. Unity and Electron can read and request operations but cannot write Hangar,
   deployment, session, or settlement state directly.
6. No mode-specific inventory or loadout document can override the canonical
   Hangar.
7. Arena produces the same authoritative result for the same initial state,
   seed, ordered typed operations, and simulation version whether controllers
   are humans, AIs, rendered clients, or a headless batch harness.
8. Starbridge jurisdiction changes without changing fact/document schemas;
   diagnostics expose assignment epoch, provisional Commander result, pilot
   result, mismatch decision, replay boundary, and finalized result.
9. Terminus and Starbridge also complete representative scenarios headlessly;
   attaching a renderer does not change their ordered facts or outcome.
10. Arena balance batches record simulation version, content/catalog version,
    seed, admission policy, controller policy identities, deployments, ordered
    operations, and scored outcome so build and AI comparisons are reproducible.
11. A terminal outcome cannot mutate Hangar state before Hangar acceptance;
    duplicate or reordered delivery returns the same receipt and delta.
    In-session refit and resume cannot invoke deployment or settlement.
12. A Commander result inside active pilot jurisdiction cannot become externally
    final before the prediction window closes. A valid differing pilot result
    replaces it; stale, wrong-epoch, out-of-jurisdiction, or invalid pilot
    results cannot rewrite finalized history.
13. Terminus, Starbridge, and Arena publish diagnostics identifying their active
    CultMesh authority configuration. Witness-authoritative work begins only
    after the Starbridge and Arena proofs pass without mode-specific state or
    operation schemas.
