# components/ecosystem-tree — design notes

STARODK ecosystem tree UI (final-hardening-cutover Phase D / D2). Read-only React
Flow render of a STARODK's ecosystem — a tree of DappSeries/STARODK nodes — plus
an inline attach form (interactive add is the D2 bonus).

## §reuse

- **Reuses, does not edit, the quest-builder canvas.** `ecosystem-tree-flow.tsx`
  imports `layoutGraph` from `../quest-builder/layout` (a pure, dependency-free
  longest-path layered layout). We intentionally do NOT import the quest node
  types or catalog — an ecosystem node is keyed by `refKind` (DappSeries / StarOdk)
  not by quest node category, so `ecosystem-node.tsx` is a sibling of
  `quest-builder/quest-node.tsx` with the same card shape but a kind-color scheme.
- The quest-builder files under `components/quest-builder/` are owned by the D1
  worker; this component only *imports* the layout helper from them.

## §data-shape

`EcosystemTreeFlow` consumes the backend `GET /api/starodk/{id}/ecosystem` shape:
`{ ecosystem, roots: EcosystemTreeNode[] }`, where each `EcosystemTreeNode` is
`{ node, children }` (recursive). `flatten()` walks it depth-first into React Flow
nodes + parent→child edges, with a `seen` set as a defensive UI-side cycle break
(the backend already guards acyclicity in `AssembleTree`).

## §wiring

The tree section is surfaced in the STARODK detail panel
(`app/(dashboard)/star-odk/page.tsx` → `EcosystemSection`): it fetches the tree,
renders `EcosystemTreeFlow`, and POSTs to
`/api/starodk/{id}/ecosystem/dapp-series` to attach a DappSeries by id. Ownership
is enforced server-side against the authenticated avatar — the form sends no owner
id.

## §value-caveat

The tree composes dApp descriptors; it does not itself move value. Real
cross-chain value in the composed ecosystem flows through the Phase-B bridge —
**Algorand real, Solana fail-closed**. The UI must not imply end-to-end Solana
ecosystem value works.
