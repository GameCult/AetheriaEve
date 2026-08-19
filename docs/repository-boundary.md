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
  inputs. Compatibility readers consume one manifest-selected CultCache
  generation through the canonical backing-store reader; they never enumerate
  record pages as an alternate catalog authority;
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

## Preservation evidence

The restoration did not discard the modern daemon work or overwrite the
working legacy UI:

- `GameCult/Aetheria` commit `7006a6b0` restores the stable legacy tree. Its
  first parent, `56a156ee`, remains the complete pre-restoration modern tree.
- Filtered `GameCult/AetheriaEve` commit `7d0ed63` is the corresponding modern
  extraction. Across the selected state, daemon, minimal Unity, package, docs,
  scripts, conformance, and tool paths, all 327 shared blobs are byte-identical
  to `56a156ee`.
- Thirty-three extraction-time omissions were subsequently restored or
  superseded in AetheriaEve. The remaining omitted Stage 7 Unity parity script
  depended on the deprecated client and is intentionally retired.
- The modern files no longer present in AetheriaEve are deliberate authority
  cuts: client-owned state/replica/Verse discovery facades, the peer committed-
  fact importer, and their obsolete verifier. Their history remains reachable;
  they are not live dependencies.
- `Assets/Scripts/UI/Menu/InventoryPanel.cs` at restored Aetheria commit
  `7006a6b0` has the same Git blob as legacy `master`. The working drag/drop,
  ghost, occupancy, thermal, and trade-era implementation therefore remains an
  intact reference in the historical Unity project.

## Verification

- `scripts/verify-aetheria-daemon.ps1` proves the daemon dependency graph and
  simulation smokes;
- `scripts/verify-aetheria-unity-client.ps1` rejects Aetheria gameplay
  assemblies in the minimal Unity lowerer;
- `scripts/verify-aetheria-assets-unity.ps1` rejects gameplay scripts in the
  asset project and rebuilds every advertised provider bundle;
- the launcher proves the composed Hangar path against a fresh local state.
