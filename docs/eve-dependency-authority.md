# Eve Dependency Authority

Aetheria admits Eve commit
`96839ad34c8d464ef622d8bbdd5d277e1ca9d825`. Its
`org.gamecult.eve.surface` package subtree is
`783e75a25659bd8c43bc4624803ff8a6c58bca03`.

Eve owns renderer-neutral surface documents, navigation authority, immutable
command invocation identity, delegation, receipts, and presentation finality.
The daemon consumes those contracts from one clean source root. Before project
resolution, the root must match the admitted commit and package tree with no
tracked or untracked changes. After assembly resolution,
`GameCult.Eve.Surface.dll` must come from that root. `EveRoot` selects a
checkout; it does not grant authority to whatever happens to be there.

Aetheria's Unity client admits EveUnity commit
`92f0dbbc9bf77c232f0c6cf733f48af64c8ed6b6`. The scene package subtree is
`0b882e923528e88c208c8175816ef3479293044f`; the UI Toolkit package subtree is
`fe335375cd7a5a8888cdbfd9e2934cd67be22e91`.

EveUnity owns transactional provider navigation, bounded in-process
receipt-backed command delivery, route trust, mounted-presentation receipt admission, immutable asset
catalog leases, input lowering, and native presentation lifecycle. Both Unity
packages pin the same admitted repository commit. Package versions, sibling
worktrees, Unity `PackageCache` contents, and API-compatible assemblies are
evidence only; none may replace the recorded commit and subtree witnesses.

To admit another Eve or EveUnity revision, update the source revision, package
tree witnesses, both Unity package graphs, and their verification scripts in
one reviewed change. The daemon must build against the exact Eve root, the
isolated EveUnity package suite must pass against the admitted CultLib and Eve
roots, and a clean Aetheria Unity resolution must consume the published Git
commits.
