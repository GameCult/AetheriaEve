# CultLib Dependency Authority

Aetheria admits exactly CultLib commit
`334e60f1928b4212a29dd8b0d19b2c099fe6365e`. Its Unity package subtree is
`f771db72619bf668dcb94a8ad00ac9c2e73a1435`.

`CultLibRoot` is an input path, not authority. Every .NET project that directly
references a CultLib project imports `Aetheria.State.Dependencies.props` and
`Aetheria.State.Dependencies.targets`. Before project resolution, the checkout
must be at the admitted revision with no tracked or untracked source changes.
After assembly resolution, every CultLib-owned assembly must lie beneath that
exact root. Both Unity projects pin `org.gamecult.cultlib` to the admitted commit
in their manifest and lockfile.

CultLib owns route-bound authority, authenticated sessions, atomic persisted
generations, demand-generation fencing, and the package artifacts built from
those implementations. Aetheria consumes those contracts; it does not copy or
reinterpret them. Package versions, local paths, NuGet caches, Unity
`PackageCache` contents, endpoint arrays, and branch names are derived evidence,
not owners.

Alternate release, feature, NuGet, PackageCache, or sibling worktrees cannot
satisfy source builds merely because their APIs or versions appear compatible.
To admit another CultLib revision, update the commit, Unity subtree witness,
both Unity package graphs, and the verification scripts in one reviewed change.

Eve and EveUnity admission is recorded separately in
[`eve-dependency-authority.md`](eve-dependency-authority.md).
