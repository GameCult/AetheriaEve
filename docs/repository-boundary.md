# AetheriaEve Repository Boundary

## Owner

`GameCult/AetheriaEve` owns the renderless Aetheria product: typed state,
daemon simulation, CultMesh publication, Eve/CultUI composition, minimal
lowering clients, and provider presentation assets.

`GameCult/Aetheria` owns the working historical Unity game. It is a behavioral,
interaction, visual, and content-authoring reference. It is not a runtime
dependency of AetheriaEve.

## Inputs

- legacy MessagePack catalog files retained under `GameData` as explicit import
  inputs;
- typed CultCache state and configured Verse identity;
- provider presentation assets authored in `Aetheria.Assets.Unity`;
- sibling development checkouts of CultLib, Eve, EveUnity, EvePlugins,
  CultMath, and Ymir, or their released package equivalents. The daemon reads
  renderer-neutral Eve contracts from Eve; only lowering clients consume
  EveUnity.

## Outputs

- daemon-owned typed gameplay and progression documents;
- daemon-owned Eve/CultUI surfaces, including the Hangar and Verse selector;
- CultMesh asset catalogs and provider-owned Unity bundles;
- generic Unity and Electron lowering targets;
- deterministic headless Terminus, Starbridge, and Arena sessions.

## Derived state

- `Aetheria.Unity` contains only EveUnity configuration and lowering bootstrap;
- `Aetheria.Assets.Unity` contains presentation assets and their package
  builder, not gameplay scripts;
- generated provider prefabs are canonical presentation assets derived once
  from the historical Unity sources and thereafter versioned here;
- legacy IDs and MessagePack payloads are import provenance, not live authority.

## Forbidden writers

- Unity and Electron clients cannot own or repair gameplay, progression, Verse
  selection, loadout, launch, or resume state;
- the asset-authoring project cannot contain gameplay or UI authority;
- the historical Aetheria repository cannot be read at build or runtime;
- migration verifiers cannot require the deprecated Unity client to remain
  modified as evidence of the live architecture.

## Shared paths

Local launch, editor launch, released client launch, headless sessions, tests,
and CI all consume the same typed state package, daemon entry point, Eve
surfaces, and provider asset bundles. `scripts/run-aetheria-unity.ps1` is the
reference composition of those owners.

## Cut line

The extraction retains filtered modern history and merges the path-renamed
history of the provider asset tree. The extraction commit removes Unity
gameplay scripts from `Aetheria.Assets.Unity`, commits the script-free generated
presentation prefabs, and repoints daemon and launcher defaults. Once the new
repository passes its independent proofs, `GameCult/Aetheria` returns to the
stable legacy Unity tree.

## Verification

- `scripts/verify-aetheria-daemon.ps1` proves the daemon dependency graph and
  simulation smokes;
- `scripts/verify-aetheria-unity-client.ps1` rejects Aetheria gameplay
  assemblies in the minimal Unity lowerer;
- `scripts/verify-aetheria-assets-unity.ps1` rejects gameplay scripts in the
  asset project and rebuilds every advertised provider bundle;
- the launcher proves the composed Hangar path against a fresh local state.
