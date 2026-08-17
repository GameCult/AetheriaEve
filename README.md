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
- `Aetheria.Rts.Web` — Starbridge-oriented Electron lowering target.
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
