# AetheriaEve

AetheriaEve is the renderless, daemon-owned Aetheria game. The daemon owns
simulation, progression, mode sessions, operations, and the Eve/CultUI surface
that constitutes the entire game interface. Unity is a generic Eve lowerer; it
does not contain Aetheria gameplay or UI authority.

The game begins in the shared Hangar and launches three modes:

- **Terminus** — single-player roguelike;
- **Starbridge** — co-op with Commander-default simulation and Pilot correction;
- **Arena** — server-authoritative PvP and a deterministic headless harness for
  AI-policy training and build balancing.

All three modes share Hangar progression, ships, equipment, and fitting rules.
Each mode demonstrates a distinct CultMesh authority configuration.

## Projects

- `Aetheria.State` — canonical typed CultCache/CultMesh documents and rules.
- `Aetheria.State.Daemon` — gameplay daemon and Eve surface provider.
- `Aetheria.Unity` — minimal Unity host for the released EveUnity packages.
- `Aetheria.Assets.Unity` — provider-owned Unity asset authoring and bundle
  builder. It packages presentation assets; it is not a game client.
- `Aetheria.Rts.Web` — deprecated Stage 7 Electron/RTS reference plus the
  C#↔TypeScript command-wire witness. Product Electron clients lower the same
  daemon-published Eve surface as every other renderer.
- `Aetheria.State.*Smoke` and `Aetheria.State.AuthoritySmoke` — state, daemon,
  authority, and client proofs.

The historical Unity game remains in the separate `GameCult/Aetheria`
repository as a working behavioral and visual reference.

## Run the Unity client

Requirements:

- Unity `6000.4.2f1`;
- .NET SDK 10;
- sibling checkouts of `CultLib`, `Eve`, and `Ymir` under the same parent
  directory for daemon development builds. Unity consumes released EveUnity
  lowering packages and does not make the daemon depend on the renderer repo.

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-aetheria-unity.ps1
```

The launcher imports local typed state when necessary, builds provider bundles
with `Aetheria.Assets.Unity`, builds the minimal Eve client, starts the daemon,
and mounts `aetheria.hangar`.

To populate the Hangar's Verse dropdown from one or more configured Odin
rendezvous endpoints:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-aetheria-unity.ps1 `
  -OdinDiscoveryEndpoint wss://odin.example/cultmesh `
  -OdinRootP256 gamecult-odin-2026:<base64-x>:<base64-y>
```

The dropdown itself is daemon-published Eve/CultUI. **Local** uses the local
moddable `.cc` progression state; discovered remote Verses keep their own
authority. Remote routes must chain to one of the explicitly configured Odin
P-256 roots and prove the advertised provider key over WSS or QUIC. Loopback
Odin endpoints may be unsigned only in the automatically selected local
development policy. Accepted launch/continue receipts route the generic
lowerer to the selected Verse's Pilot surface.

The current remote route proof authenticates the provider, not the player.
Local Hangar progression is executable end to end; a production GameCult Verse
must additionally bind an authenticated account principal to per-player Hangar
and draft records before serving authoritative progression. A session runtime
ID is not an account credential.

## Architecture

Start with:

- [repository boundary](docs/repository-boundary.md);
- [game modes and progression](docs/game-modes-and-progression.md);
- [renderless architecture](docs/renderless-aetheria-architecture.md);
- [developer navigation](docs/developer-navigation.md).
- [portable game framework adversarial review](docs/portable-game-framework-review.md).

Persistent state is typed CultCache `.cc` data. Services speak CultNet through
CultMesh. User interfaces are daemon-published Eve/CultUI compositions. JSON is
reserved for schema publication, diagnostics, and foreign-system boundaries.

## History

This repository retains the filtered commit history of the daemon/Eve rebuild
and the path-renamed history of the provider asset tree. The extraction commit
joins those histories explicitly so the modern project and its authored assets
retain provenance from `GameCult/Aetheria`.

## License

The majority of the repository is available under the Mozilla Public License.
See [LICENSE](LICENSE) and per-file notices for details.
