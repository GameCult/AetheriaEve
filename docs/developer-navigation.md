# AetheriaEve Developer Navigation

This repository owns the daemon-backed Aetheria product. The historical Unity
game lives in `GameCult/Aetheria`; do not rebuild its gameplay or UI authority
here.

## Start Here

- `README.md` - product modes, prerequisites, and launch commands.
- `docs/repository-boundary.md` - repository and runtime ownership.
- `docs/game-modes-and-progression.md` - Hangar, Terminus, Starbridge, Arena,
  deployment, and settlement doctrine.
- `docs/renderless-aetheria-architecture.md` - daemon and Verse dataflow.
- `docs/aetheria-perfect-machine-map.md` - migration history and detailed body
  map; historical Unity paths in this document are evidence, not live routes.

## Runtime Projects

- `Aetheria.State` defines typed CultCache/CultMesh state and shared rules.
- `Packages/org.gamecult.aetheria.state` is the Unity/package projection of the
  same shared runtime source.
- `Aetheria.State.Daemon` owns gameplay simulation, persistence, operations,
  progression, sessions, and Eve/CultUI publication.
- `Aetheria.Unity` is the minimal Unity host for the generic EveUnity lowerer.
- `Aetheria.Assets.Unity` authors and bundles provider presentation assets. It
  is not a game client and contains no gameplay or product UI code.
- `Aetheria.Rts.Web` is the Starbridge-oriented Electron lowering target.

## Common Work

Typed documents and record keys:

- `Packages/org.gamecult.aetheria.state/Runtime/AetheriaRuntimeDaemonDocuments.cs`
- `Packages/org.gamecult.aetheria.state/Runtime/AetheriaRuntimeGameDocuments.cs`
- `Packages/org.gamecult.aetheria.state/Runtime/AetheriaRuntimeVerseClient.cs`
- `Aetheria.State/AetheriaDocumentRegistry.cs`

Daemon operations and simulation:

- `Packages/org.gamecult.aetheria.state/Runtime/AetheriaRuntimeDaemonOperations.cs`
- `Packages/org.gamecult.aetheria.state/Runtime/AetheriaRuntimeDaemonSimulation.cs`
- `Aetheria.State.Daemon/Program.cs`

Hangar and product UI:

- `Packages/org.gamecult.aetheria.state/Runtime/AetheriaRuntimeHangarDocuments.cs`
- `Packages/org.gamecult.aetheria.state/Runtime/AetheriaRuntimeHangarOperations.cs`
- `Packages/org.gamecult.aetheria.state/Runtime/AetheriaRuntimeHangarSurfaceBuilder.cs`
- `Packages/org.gamecult.aetheria.state/Runtime/AetheriaRuntimeInventoryPanelSurfaceBuilder.cs`

Provider assets:

- `Aetheria.Assets.Unity/Assets/Editor/EveAssetBundleBuilder.cs`
- `scripts/verify-aetheria-assets-unity.ps1`
- `scripts/prune-aetheria-provider-assets.ps1`

Thin clients:

- `Aetheria.Unity/Assets/AetheriaUnityClient.cs`
- `Aetheria.Rts.Web/Electron`

## Verification

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-aetheria-daemon.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\verify-aetheria-unity-client.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\verify-aetheria-assets-unity.ps1
dotnet run --project .\Aetheria.State.Unity.Smoke\Aetheria.State.Unity.Smoke.csproj -- . .\Aetheria.Unity\Build\aetheria-unity.cc
```

The removed `Aetheria.State.Verify` project and Stage 7 legacy parity script are
available in Git history. They inspected the old `Assets/Scripts` client and
therefore cannot serve as live AetheriaEve gates.

## Invariants

1. The daemon owns canonical gameplay and publishes the entire product UI as an
   Eve/CultUI surface.
2. Unity and Electron lower typed state and submit typed operations; they do not
   own gameplay, progression, or product UI composition.
3. Shared UI reads use canonical typed documents or intentional Eve projections.
4. Provider assets remain script-free and are built only by
   `Aetheria.Assets.Unity`.
5. Generic lowering belongs in EveUnity. Aetheria-specific lowering belongs in
   daemon-published surface semantics, not Unity scripts.
6. Historical migration code remains recoverable from Git without remaining a
   live authority.
