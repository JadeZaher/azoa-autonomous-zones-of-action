---
type: product-context
---

# Product: AZOA

## Goal

Take AZOA from "deployable fail-closed node" to a **single stable node serving
two real application consumers**, each driving its own users. Federation is
explicitly **future state** and out of scope. This is the RUNBOOK's own §1
framing (`conductor/RUNBOOK.md` §1) — this file states the destination and the
shape of the two consumers; the workstream-by-workstream plan lives there, not
here.

AZOA itself is the neutral value/workflow substrate underneath both consumers:
identity, custody, chain and fiat rails, and a durable action-graph engine.
Consumer-specific vocabulary, product decisions, and economics belong in the
consumer, never in AZOA's own primitives (D28) — AZOA receives already-decided
amounts and never computes a consumer's economics (D7/D16). The platform is
built so a non-crypto user of a consumer app should barely feel that a
blockchain is involved (D46) — AZOA brings the benefits of on-chain settlement
to that audience without making the platform itself "a blockchain company."

## The two consumers (these define "done" for the builder)

| | **ArdaNova** | **Taawun** |
|---|---|---|
| What it is | Worker-owned cooperative gig platform — "Upwork where work earns equity" | Community app builder (mosques, charities, founders) |
| Stack | Node/JS, game SDK, MCP backend, contracts | Go, Datastar, MCP control plane, CRDT/IndexedDB local-first |
| Econ surfaces | Per-project equity ASAs; ARDA token bridging project tokens → stablecoin **USD payouts**; treasury-backed deterministic rates; Stripe on/off-ramp; soulbound credentials (BRONZE→DIAMOND); task bounties; guild bidding | **Escrow quests** (settle to creator+platform only on buyer confirmation); creator revenue **splits**; donation drives; **principal-only loan** trackers; volunteer stipends |
| Users | Innovator, Contributor, Supporter, Guild Member | Architect, Maintainer, Viewer (congregant/donor) |
| Hard constraint | Equity/valuation math stays in ArdaNova; AZOA receives already-decided amounts | **No interest-bearing primitive** may exist in a flow. **Ambiguity must fail closed and refund** |

Taawun's product doc states *"anything touching money runs on AZOA"* and splits
state into **convergent** (CRDT, theirs) vs **transactional** (balances,
payments, escrows, splits, pledges — **AZOA action graphs**). That split is the
contract the builder must serve. Full detail, including the review ledger and
open decisions behind each row: `conductor/RUNBOOK.md` §1.

⚠ D28 renamed the platform's own nouns (`Avatar`→`User`, `Quest`→`Action`).
This table intentionally keeps each consumer's own vocabulary — de-branding
applies to AZOA's primitives, not to how consumers describe themselves.

## The economic loop (D44) — ArdaNova is the reference implementation

ArdaNova is the first consumer exercising AZOA's fiat rail end to end, and the
owner's direction for that loop is **chain-first and token-native**, not
fiat-first — the review proposed fiat-first and the owner rejected it (D44):

- **IN:** fiat → **ARDA** (the platform token, bought with a card through
  AZOA's `Fiat.USD` rail) → spend ARDA to fund a project → receive that
  project's own **project tokens**, a real per-project ASA minted into a
  platform reserve (D47).
- **OUT:** project tokens → trade for ARDA (an Action DAG plus an
  `ActionGraphBundle` exchange, D50 — deliberately **not** an AMM/Tinyman) →
  sell ARDA for fiat.

Sponsorship is never a card charge against a project directly — **a card buys
ARDA; spending ARDA is what funds a project, on chain.** Fiat is only the
on-ramp and off-ramp for the platform token (D44); ARDA itself is **user-facing
currency people buy, hold and spend**, not an internal-only ledger unit (D44
explicitly rejects "ARDA is internal-only, never shown to users"). Every value
action requires full document KYC and a provisioned custodial wallet, for
sponsor and recipient alike — the wallet stays invisible, the identity check
does not (D46). Custody itself is assumed present for planning under the
owner's own operator role (D48); genesis is a configuration/operations task
over the mint path that already exists (D49).

See `conductor/RUNBOOK.md` decisions D44–D51 and
[`ardanova-production-readiness-waves_20260907`](tracks/ardanova-production-readiness-waves_20260907/spec.md)
for the program that ships this loop end to end.

## What AZOA is not

- Not a consumer-facing brand. No "Sleek", no OASIS-project vocabulary, no
  consumer-specific (religious, Taawun-specific, ArdaNova-specific) terms in
  AZOA's own primitives (D28).
- Not a tax or regulator authority — AZOA exposes analytics facts; consumers
  run their own tax workflows (D15/D16).
- Not an AMM or DEX operator for its own token economy — the ARDA↔project-token
  exchange is a platform-operated Action DAG + `ActionGraphBundle`, never a
  liquidity pool (D50).
- Not something that requires its non-crypto users to learn a blockchain
  concept to participate. If a user must meet a wallet, address, chain, seed
  phrase, signature, or gas fee to sponsor a project or get paid, that is a
  defect, not a detail.

## Success metrics

Tracked as the acceptance criteria of
[`ardanova-production-readiness-waves_20260907/spec.md`](tracks/ardanova-production-readiness-waves_20260907/spec.md),
headline **AC1**: a non-crypto user can sign in, **buy ARDA with a card, spend
ARDA on a project and receive that project's units**, take a task for stake,
cash out, vote, and see a plain-language ledger of where the money went — in a
non-development environment — without ever meeting a wallet, address, chain,
seed phrase, signature or gas fee.
