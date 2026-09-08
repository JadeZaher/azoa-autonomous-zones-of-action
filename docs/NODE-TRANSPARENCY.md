# NODE-TRANSPARENCY — node economic-rules transparency

The correct name for this surface, everywhere it is described (docs, code
comments, UI copy, PR descriptions), is **node economic-rules transparency**
— never the bare word "transparency" on its own. "Transparency" alone reads
as a promise made to the *sponsor* about *their* money; this surface makes no
such promise. It publishes the **rules this node runs by**, for a technical
audience, and it is described here precisely so the distinction cannot be
lost the next time someone reaches for the shorter name.

## What it actually is

`GET /api/node-transparency/*` (`Controllers/NodeTransparencyController.cs`,
backed by `Managers/NodeTransparencyManager.cs`) is a public, credential-free,
read-only API that discloses exactly three things about this node, plus an
append-only audit history of changes to each:

1. **Governance parameters** — which chains and asset types this node's
   governance currently allows (`PublicNodeGovernanceParametersResponse`).
2. **Fee schedule** — the flat-plus-bps fee this node charges for mint,
   transfer, swap, action-complete, and federation-publish operations
   (`PublicNodeFeeScheduleResponse`).
3. **Treasury destinations** — the chain/network/address this node routes
   platform treasury funds to (`PublicNodeTreasuryDestinationResponse`).

| Endpoint | Returns |
| --- | --- |
| `GET /api/node-transparency/current` | The current snapshot of all three, content-hashed for cache validation |
| `GET /api/node-transparency/audit/governance` | Newest-first, cursor-paged history of governance-parameter changes |
| `GET /api/node-transparency/audit/fees` | Newest-first, cursor-paged history of fee-schedule changes |
| `GET /api/node-transparency/audit/treasury` | Newest-first, cursor-paged history of treasury-destination changes |
| `GET /api/node-transparency/audit/checkpoint` | A signed checkpoint over the combined audit history, when the operator has enabled dedicated node-identity checkpoints (unavailable otherwise — that is an expected state, not an outage) |

That is the whole surface. It is not a report of any sponsor's money
movements, not a per-project ledger, and not this node's interop/compliance
conformance status — see the next section.

## Who this is for

Other AZOA nodes verifying interop assumptions before they federate,
third-party auditors or regulators, integrators building against a specific
node's rules, and — the reason a console page exists at all — **this node's
own operator**, confirming exactly what is being disclosed before and after
a governance or fee-schedule change.

It is **not** for sponsors, and it must never be cited as sponsor-facing
proof of anything. The product's sponsor-trust story is told in plain words
on the actual product surfaces (a project's funding tab, its money view) —
amounts, dates, and descriptions a non-crypto sponsor can read without
learning what a chain, a wallet address, or a fee schedule is. A sponsor
should never be linked to this API, this document, or the operator console
below as evidence of "transparency." Doing so is the exact misreporting this
rename exists to prevent: pointing a sponsor at operator machinery and
calling it their own visibility into their money.

## Not this document: the conformance manifest

`GET /.well-known/azoa-node.json` (`Extensions/NodeConformanceEndpointExtensions.cs`,
`Services/Conformance/NodeConformanceManifestService.cs`,
`INodeConformanceManifestService`) is a **separate** public, credential-free
artifact: a bounded, independently verifiable L0 descriptor of this node's
federation/interop conformance — gate posture and capability attestation for
other nodes deciding whether to federate with this one. It shares the
"credential-free public JSON document about this node" shape with node
economic-rules transparency, which is exactly why the two keep getting
conflated in prose.

They are not the same thing and must stay described separately:

- The conformance manifest answers *"can another node safely federate with
  this one, and does it meet the conformance gates it claims to meet?"*
- Node economic-rules transparency answers *"what rules does this node
  currently apply to mints, transfers, swaps, and treasury routing, and what
  were they before?"*

Neither substitutes for the other, and neither is a sponsor-transparency
feature. A write-up, page, or paragraph that presents both under one
"transparency" umbrella — or that links the conformance manifest from a
user-transparency narrative — is the defect this document exists to prevent.
The operator console below intentionally shows only the three economic-rules
categories; it does not surface the conformance manifest.

## The operator console

`frontend/src/app/(dashboard)/node-transparency/page.tsx` is a read-only
console page inside the ordinary authenticated dashboard (gated the same way
every other `(dashboard)/admin/*` page is — the route group's own login
guard, not a separate credential). It is a **consumer** of the public API
above: the same anonymous responses a third party would see, rendered for
the operator so they can confirm what is being disclosed without needing to
call the API by hand. It shows the current snapshot (governance parameters,
fee schedule, treasury destinations, content hash, last-updated time), the
three paged audit histories with cursor-based "load more," and the signed
checkpoint's availability. It does not add, hide, or reinterpret anything the
public API does not already return — the console is a view onto the same
disclosure, not a second, richer one only the operator gets.

## Doing this again correctly

- New copy describing this surface: say "node economic-rules transparency,"
  not "node transparency" or "transparency" alone.
- New UI: if it is meant for sponsors, it does not belong here — build it on
  the product surface instead, in plain words, with no chain terminology.
- New machine-readable public node documents: decide up front whether they
  are economic-rules disclosure (this document, extend the manager) or
  conformance/interop attestation (the manifest, extend that service) and
  keep them in their own section, their own endpoint namespace, and their
  own write-up. Do not add a third public JSON surface without deciding
  which of the two it is.
