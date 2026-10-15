---
type: Track Review Verdict
title: Governance vote of record — independent adversarial review (Phase 4)
description: The separate-context adversarial review Phase 4 of governance-vote-of-record_20260906 requires, with the verdict that decides whether the three governance node types leave NotEnabledActionNodeTypes.
tags: [review, verdict, governance-vote-of-record_20260906, launch-readiness_20260909, security]
timestamp: 2026-09-09T00:00:00Z
resource: ./metadata.json
---

# Governance vote of record — Phase 4 independent verdict

VERDICT: FAIL — the vote of record is unforgeable, but the ELECTORATE is: any dApp developer on the node can raise a proposal naming another tenant's members as its electorate, and no proposal can ever be closed. The three governance node types stay in `NotEnabledActionNodeTypes`.

**Item:** L4.1 of `conductor/tracks/launch-readiness_20260909/plan.md`. **Closes:** OI-50.
**Reviewed tree:** AZOA wave-L4 start (L0–L3 landed; `Services/Governance/**`, `Managers/HolonManager.cs`,
`Services/Actions/Handlers/Governance*NodeHandler.cs`, `Services/Kyc/PersonUniquenessGate.cs`,
`Providers/Stores/Surreal/SurrealPersonOperationClaimStore.cs`).
**Reviewer:** a context that did not write this code and did not verify it. W9.2 wrote the lane and
recorded its own pass at `Services/Governance/AGENTS.md` §phase-4-verdict, which says in its own words
that it is "the IMPLEMENTER's pass, not an independent sign-off". This file is the independent one.
**Told in advance, so they are not re-found as findings:** OI-47 and OI-48 (L4.3, AZOA) and OI-46 plus
the DelegatedVotes row (L4.2, ArdaNova). They are confirmed still open below, for the record only.

## 1. The decisive question Phase 4 asked: answered, and it holds

> *Can a vote that never took a claim still be counted?*

**No.** This half of the lane is sound, and it is sound by construction rather than by a check somebody
has to remember. Four independent legs, each verified against source:

| Leg | Where | What it does |
| --- | --- | --- |
| One writer | `Services/Actions/Handlers/GovernanceVoteNodeHandler.cs:166` | The **only** production caller of `IPersonUniquenessGate.TryClaimAsync` in the whole tree. Nothing else can write a decision row. Verified by exhaustive grep, not by reading the docs. |
| One reader | `Services/Governance/GovernanceClaimLedgerTally.cs:103` | Reads `ListLiveByOperationAsync` and nothing else. No holon store, no join to drop. |
| Scoped projection | `Providers/Stores/Surreal/SurrealPersonOperationClaimStore.cs:209-219` | `WHERE uniqueness_scope = $_scope AND operation_key = $_operation_key AND released_at = NONE`, projecting five columns; no `person_key`, and deliberately no `LIMIT` — a truncated electorate would publish a wrong count under a correct-looking hash. |
| Refusal at the publish | `Services/Actions/Handlers/GovernanceTallyNodeHandler.cs:141` | `SourceTable != person_operation_claim` is refused, not dressed up. Plus `GovernanceVoteOfRecordGuard.ThrowIfTallyIsNotLedgerOnly()` in the constructor (`:71`). |

Attacks attempted against this half and **refuted**:

- **Forged ballot holons.** `POST /api/holon` writes `GovernanceVote` children; the tally never reads them.
- **Second claim path.** None exists. `IPersonUniquenessGate` has exactly one consumer.
- **Rewriting the uniqueness key to point the tally at a foreign domain.** Both the vote node
  (`GovernanceVoteNodeHandler.cs:157-160`) and the tally (`GovernanceTallyNodeHandler.cs:129-132`)
  require the recorded key to equal `UniquenessOperationKeyFor(proposal.Id)`, and the key itself is
  frozen (`ConsumerGovernanceHolonTypes.cs:236-247`) — and the freeze is evaluated on the **pre-merge**
  state (`Managers/HolonManager.cs:176`), so it cannot be dodged by moving the domain and the question
  in one request.
- **Numeric scope widening.** `TryParseScope` rejects any input containing a digit, so `"1"` cannot
  silently promote a tenant ballot to the node-global domain (`ConsumerGovernanceHolonTypes.cs:384-397`).
- **Tally-hash canonicalization collision.** The preimage is length-prefixed per option
  (`GovernanceTallyHash.cs:52-60`); an option containing `:` or `=` cannot forge a colliding tally.
- **Unwired-store fail-open.** `ProposalFreezeState.Unknown` covers both a store error and a missing
  `IPersonOperationClaimStore`, and both refuse the rewrite (`Managers/HolonManager.cs:808-826`).
- **Person-to-choice leak through the receipt.** `AllowedVoteMetadataKeys` is closed and re-checked
  before the write (`GovernanceVoteNodeHandler.cs:205-207`); `GovernanceVote` is deliberately absent
  from `HolonManager.PublishableAssetTypes` (`:61-65`).

That list is here because a verdict that only lists faults is not a review. **D39 works.**

## 2. Why the verdict is still FAIL

The Phase 4 question was too narrow, and W9.2 said so itself. An outcome does not have to be miscounted
to be moved — it is enough to move the *question*, or to move the *electorate*, or to make the vote
never end. Two of those are open, and neither is OI-47, OI-48 or OI-46.

### F-1 — BLOCKER — An author can nominate somebody else's members as their electorate

The design states the rule in its own words, twice:

- `Services/Actions/Handlers/GovernanceProposalCreateNodeHandler.cs:113-115` — *"a proposal author must
  not be able to nominate somebody else's members as their electorate."*
- `Services/Governance/AGENTS.md` §electorate-must-name-a-tenant — *"never from config: an author must
  not be able to nominate somebody else's members."*

The only code that enforces anything here is
`ConsumerGovernanceHolonTypes.ProposalElectorateRejection` (`Services/Governance/ConsumerGovernanceHolonTypes.cs:142-163`),
and all it asks is `IsUsableElectorateValue` (`:121-126`) — *"is the declared value a non-empty
`Guid("N")`"*. **It never asks whose tenant it is.** It cannot: `HolonManager.CreateAsync`
(`Managers/HolonManager.cs:115`) and `UpdateAsync` (`:166`) take a `userId` and **no tenant at all**, so
the chokepoint W9.2 chose is structurally incapable of enforcing the sentence its own documentation
claims for it. `ActingTenantId` exists only on `ActionNodeExecutionContext` and `SigningContext`, read
from the `act_as_tenant` claim (`Helpers/ClaimsPrincipalExtensions.cs:127`); it never reaches the manager.

The exploit is three ordinary API calls by any principal that satisfies the `DappDevelop` policy
(`Controllers/HolonController.cs:42-43`; policy at `Program.cs:604-626`) — i.e. any dApp developer on the
node, which on the many-tenants-one-node model this repo runs is the standing adversary:

1. `POST /api/holon` with `assetType: "GovernanceProposal"`, `isPublic: true`, and metadata
   `{status: "Open", options: "[\"yes\",\"no\"]", uniquenessScope: "Tenant", electorateTenant: "<VICTIM TENANT GUID N>"}`.
   `PublicationError` permits it — `GovernanceProposal` is the one publishable type
   (`Managers/HolonManager.cs:61-65`). `ProposalElectorateRejection` permits it — the value parses.
2. `PUT /api/holon/{id}` recording `uniquenessOperationKey = governance:proposal:{id}`. This passes the
   freeze because `TryResolveClaimDomain` returns false until that key is present
   (`ConsumerGovernanceHolonTypes.cs:262-268`), so the proposal is `NotFreezable` for exactly this one write.
   The proposal is now indistinguishable from one raised by `GovernanceProposalCreate`.
3. The victim tenant's members read it (`CanRead` is owner-or-public with no tenant term,
   `Managers/HolonManager.cs:45-46`) and vote. `IsInElectorate` (`:181-183`) compares the voter's acting
   tenant to the declared value and **passes** — the declared value really is their tenant. Their votes of
   record land in their own tenant's person-key domain, under a real claim, counted by a real tally.

The result: an outsider owns the notice, and until the first ballot lands owns the question outright
(`GovernanceProposalRewriteTests.Proposal_QuestionIsStillEditable_BeforeTheFirstVoteOfRecord`, `:383`).
Members of a co-op the attacker does not belong to cast genuine, unforgeable, one-human-one-vote ballots
on a decision that co-op never raised. The vote of record is real. The *franchise* is forged.

This is another instance of the ledger class in `conductor/RUNBOOK.md` §2: **the claim is in prose and the
control checks a weaker property.** It is not OI-47 (a different seam) and not OI-48 (a different field);
L4.3 does not touch it.

*What would close it:* the electorate must be bound to the writer, not merely well-formed — either thread
the acting tenant into `IHolonManager.CreateAsync`/`UpdateAsync` and require
`declared == ElectorateValueFor(actingTenantId)` at the same chokepoint that already refuses `node`, or
refuse `GovernanceProposal` writes on the raw holon routes entirely and make
`GovernanceProposalCreate` — which does pin from the acting tenant (`:120`) — the only way in. Until
then, delete the two prose sentences above, because they are not true.

### F-2 — BLOCKER — There is no close authority, and a proposal without `closesAt` can never be closed

The brief asked this review to attack close-vs-cancel authority. In AZOA there is **neither, and no
authority to hold either one**. There is only an optional timer that can never be added later.

- `closesAt` is optional at creation: `GovernanceProposalCreateNodeConfig.ClosesAt` is `DateTimeOffset?`
  (`GovernanceProposalCreateNodeHandler.cs:234`) and the handler writes the key only when it is present
  (`:124-125`). Nothing requires it.
- `status` and `closesAt` are both frozen from the first ballot
  (`ConsumerGovernanceHolonTypes.cs:236-247`), and `DescribeDecisionBearingRewrite` (`:296-310`) counts an
  absent-recorded / present-incoming pair as a change — so `closesAt` cannot be **added** later either.
- `DeleteAsync` is frozen too (`Managers/HolonManager.cs:243`).

So for a proposal raised without `closesAt`: `status` can never become `Closed`, `closesAt` can never
appear, and the proposal can never be deleted. It accepts ballots **forever**. Worse, the vote of record
stays mutable forever, because the only thing that freezes a claim is `DecisionClosesAt`
(`Services/Kyc/PersonUniquenessGate.cs:72-80`, and the `decision_closes_at` pinned on the row) — which is
null. Spec **AC4**, *"a claim frozen at close cannot be updated"*, is vacuous for such a proposal, because
no close exists to freeze at.

A `tallyHash` published over that proposal is a snapshot, not a record: every voter may change their
recorded option after it is published, and a later tally legitimately disagrees with the anchored one.
This is the failure mode a co-op notices — not a miscount, but a decision that never becomes final.
And it is reached by **omitting one optional config field**, which is the shape a real deployment
produces most easily. That is the same shape as instance 24 in the ledger: the safe case is the one the
code never resolves.

*What would close it:* require `ClosesAt` at `GovernanceProposalCreate` (and refuse a
`GovernanceProposal` holon that records none, at the same chokepoint that refuses a `node` electorate),
or give close a real authority — an explicit close seam, held by someone other than the author, that is
exempt from the metadata freeze and pins `closesAt`/`status` in one server-side write.

### F-3 — MAJOR — The published tally has no denominator and no floor

`GovernanceTallyNodeHandler` publishes `totalVotes`, per-option `counts`, `tallyHash`, `tallyAt` and
`closesAt` (`:145-153`) — and nothing that says how large the electorate was, whether a quorum was met,
or whether voting has finished. One ballot in a four-thousand-member electorate yields the same shaped,
hash-stamped "decision of record" as three thousand. The handler's own remarks (`:47-52`) invite a tenant
to wire `GovernanceTally -> BlockchainExecute` and anchor that hash on chain; anchoring a number whose
denominator is undefined and whose finality (F-2) is undefined records a decision that cannot be
interpreted later. This is the AZOA-side mirror of OI-46, which L4.2 is raising on the ArdaNova side —
but L4.2 raises a floor that exists, and here there is no floor concept to raise.

*What would close it:* publish the electorate size the count was taken against and a `quorumMet` /
`isFinal` determination, and put both in the tally preimage — or state in code, not prose, that this node
publishes a raw count and is not a decision of record, and withdraw the anchoring advice.

### F-4 — MINOR — `PropagateAsync` is a fourth mutation seam that writes holons it does not own

`Managers/HolonManager.cs:420-475` checks ownership on the **root only** (`:426-428`), then BFS-upserts
every descendant with no ownership check and no freeze check. A proposal's children are the voters'
receipts, owned by the voters, so a parent holon's owner can flip `IsActive` or stamp `propagated_*` keys
across an entire electorate's receipts.

`Services/Governance/AGENTS.md` §proposal-freeze claims this seam needs no freeze because *"it writes
only `IsActive` and keys it prefixes with `propagated_`, neither of which any governance node reads."*
**I attempted to refute that claim and could not** — `$"propagated_{request.Property}"` (`:459`) cannot
escape its prefix, `CanRead` has no `IsActive` term (`:45-46`), and no governance path reads either. The
claim holds and no vote moves. It is recorded as MINOR because the reasoning is one refactor away from
being wrong on a seam nobody has instrumented, and because defacing other users' receipts is its own
small wrong.

## 3. Confirmed still open, reported here for the record only (not re-found)

| Row | Confirmed at | Owner |
| --- | --- | --- |
| **OI-47** — `InteractAsync` is the third write seam and gets only the freeze, not `ProposalElectorateRejection` | `Managers/HolonManager.cs:256-313`; the rejection appears only at `:87`, `:109`, `:140`, `:216` | **L4.3** |
| **OI-48** — the question itself is not frozen: `Name`/`Description` merge at `Managers/HolonManager.cs:182-183`, after the freeze check at `:176`, and are absent from `FrozenProposalMetadataKeys` (`ConsumerGovernanceHolonTypes.cs:236-247`); pinned deliberately by `GovernanceProposalRewriteTests.cs:404` | as cited | **L4.3** |
| **OI-46** — quorum floor, author-close, close-resolves-in-one-request, inert DelegatedVotes | `ardanova/.../GovernanceServices.cs`, `ProjectsController.cs` | **L4.2** |

Note that **F-1 survives L4.3 in full** and **F-2 survives L4.3 in full**. Fixing the question freeze does
not stop an outsider from choosing whose members answer it, and does not give anyone the power to end it.

## 4. Consequence, and what a re-review needs

Per L4.1: on FAIL the three types stay in place and the findings are recorded. They stay:

- `Services/Actions/NotEnabledActionNodeTypes.cs` keeps `GovernanceProposalCreate`, `GovernanceVote` and
  `GovernanceTally` (and `CredentialRevoke`, whose own track is unrelated to this review).
- That file's stated *reason* for blocking governance was stale — it still said the tally reads raw
  holons, which `fa2dfcf` fixed. It has been corrected to the reasons above, so the gate's justification
  is checkable rather than folkloric.
- `tests/AZOA.WebAPI.Tests/Actions/NotEnabledActionNodeTypesTests.cs` now binds the two together: the
  blocked-state of the three governance types must match the `VERDICT:` line in **this file**. Flipping
  one without the other goes red. A phase with no recorded verdict is unreviewed, not done — and now the
  build says so.

**To flip this verdict to PASS**, a separate context must confirm in code: F-1 closed (the electorate is
bound to the writer at the write chokepoint, not merely well-formed), F-2 closed (a proposal cannot exist
without a close, or a real close authority exists), F-3 closed or the anchoring advice withdrawn, and
L4.3's OI-47/OI-48 landed. Then change the `VERDICT:` line above and the blocked set together; the test
will hold you to it.
