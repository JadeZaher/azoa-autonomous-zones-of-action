# components/ecosystem-tree — design notes

ActionGraphBundle ecosystem tree UI (final-hardening-cutover Phase D / D2). Read-only React
Flow render of an ActionGraphBundle's ecosystem — a tree of DappSeries/ActionGraphBundle nodes — plus
an inline attach form (interactive add is the D2 bonus).

## §reuse

- **Reuses, does not edit, the action-builder canvas.** `ecosystem-tree-flow.tsx`
  imports `layoutGraph` from `../action-builder/layout` (a pure, dependency-free
  longest-path layered layout). We intentionally do NOT import the action node
  types or catalog — an ecosystem node is keyed by `refKind` (DappSeries / ActionGraphBundle)
  not by action node category, so `ecosystem-node.tsx` is a sibling of
  `action-builder/action-node.tsx` with the same card shape but a kind-color scheme.
- The action-builder files under `components/action-builder/` are owned by the D1
  worker; this component only *imports* the layout helper from them.

## §data-shape

`EcosystemTreeFlow` consumes the backend `GET /api/actiongraphbundle/{id}/ecosystem` shape:
`{ ecosystem, roots: EcosystemTreeNode[] }`, where each `EcosystemTreeNode` is
`{ node, children }` (recursive). `flatten()` walks it depth-first into React Flow
nodes + parent→child edges, with a `seen` set as a defensive UI-side cycle break
(the backend already guards acyclicity in `AssembleTree`).

## §wiring

The tree section is surfaced in the ActionGraphBundle detail panel
(`app/(dashboard)/action-graph-bundle/page.tsx` → `EcosystemSection`): it fetches the tree,
renders `EcosystemTreeFlow`, and POSTs to
`/api/actiongraphbundle/{id}/ecosystem/dapp-series` to attach a DappSeries by id. Ownership
is enforced server-side against the authenticated user — the form sends no owner
id.

## §value-caveat

The tree composes dApp descriptors; it does not itself move value. Real
cross-chain value in the composed ecosystem flows through the Phase-B bridge —
**Algorand real, Solana fail-closed**. The UI must not imply end-to-end Solana
ecosystem value works.
