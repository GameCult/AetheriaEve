# Eve Dependency Authority

Aetheria admits Eve commit
`bfdaff2d68249491e73f514bf4fa664393a25f39`. Its
`org.gamecult.eve.surface` package subtree is
`acde4ba4e4a3db68de10b4fe295300ba54eea252`.

Eve owns renderer-neutral surface documents, navigation authority, immutable
command invocation identity, delegation, receipts, and presentation finality.
The daemon consumes those contracts from one clean source root. Before project
resolution, the root must match the admitted commit and package tree with no
tracked or untracked changes. After assembly resolution,
`GameCult.Eve.Surface.dll` must come from that root. `EveRoot` selects a
checkout; it does not grant authority to whatever happens to be there.

Aetheria's Unity client admits EveUnity commit
`f0afa7681bddf439615d8c76d4808c63dde51acf`. The scene package subtree is
`b8c36e4d634b8777b885fa835cd2515bcb671e5d`; the UI Toolkit package subtree is
`e7ec31d110cc1fcd385abcaa27bb1d450a674fd8`.

EveUnity owns transactional provider navigation, durable command outbox
behavior, route trust, mounted-presentation receipt admission, immutable asset
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
