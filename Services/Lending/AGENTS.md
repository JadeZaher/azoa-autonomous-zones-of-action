# Services/Lending — directory notes

The persisted vocabulary and the pure balance fold of the **principal-only loan tracker**
(D21). No IO, no clock, no chain: the handlers in `Services/Actions/Handlers/Loan*.cs` hand
this code holons they have already read runner-scoped. The node-level rules live in
`Services/Actions/AGENTS.md` §loan-tracker; what follows is what this directory decides.

**No interest, anywhere** — see §loan-tracker and
`tests/AZOA.WebAPI.Tests/Lending/NoInterestPrimitiveGuardTests.cs`, which reflects over every
type, member, parameter and metadata key in this namespace. A member named `Duration`,
`GenerateX` or `DetermineY` fails the build here: `rate` and `term` are substring-matched.

## §status-follows-the-balance

`LoanStatus` is derived from the **outstanding balance**, never from the presence of a row:

| balance | write-offs | status |
| --- | --- | --- |
| `> 0` | any | `Open` |
| `0` | none | `Repaid` |
| `0` | some | `WrittenOff` |
| `< 0` | any | `OverRepaid` |

A partial write-off against a still-collectible principal therefore leaves the tracker `Open`.
Reading "there exists a write-off row" as terminal `WrittenOff` declared 900 of collectible
principal abandoned because 100 had been written off — and D21 makes partial repayment and
partial write-off **ordinary states**, not endings. The tracker keeps accepting rows until the
balance actually reaches zero.

## §over-repayment

`outstanding` is floored at zero so no row can express a negative debt — `RailAmount` refuses
a negative count outright. The floor is a guard, **not an eraser**: when repayments plus
write-offs exceed the principal the status is `OverRepaid` and `LoanBalanceTotals.Excess`
carries the overshoot. Smoothing an over-repaid ledger into a clean `Repaid` would hide the
one thing worth reporting — that the ledger and the value that actually moved disagree.

`LoanRepaymentRecordNodeHandler` refuses an over-repaying row, but that check is a
read-then-write: two branches could each read the same balance, each pass, and each append.
`LoanLedgerWriteGate` holds the read → guard → append sequence as one step per tracker, so the
second branch sees the first branch's row. **Its reach is one process**: it cannot serialize
two API instances, which is exactly why the projection reports `OverRepaid` instead of hiding
it. A cross-instance guarantee needs a conditional write on the holon store; until that
exists, an over-repaid tracker is at least visible.

## §replay-identity

Two identities dedupe a ledger row, both matched **only against the kinds the repayment node
writes**:

1. `executionKey` — `runId:nodeId`, stamped on every row, the same idempotency seed the
   Bridge / Back / Swap / FungibleTokenCreate handlers use. This is what makes replay
   protection **unconditional**: the durable engine retries by design, and `reference` is
   optional, so a tenant that omits it must still not double-count.
2. `reference` — the tenant's own external reference, when supplied.

The **disbursement row is excluded from both**. `LoanOpen` stamps it with the tenant's
`reference`, so matching it swallowed the first legitimate repayment citing that same
reference and reported `Ok` for a row that was never appended.

## §amounts

Every amount is a base-unit decimal STRING in holon metadata, read and written through
`LoanAmountMetadata` (D13 — `ulong` overflows at ~18.4 ETH in wei). This lane TIGHTENS
`Persistence/SurrealDb/Models/AGENTS.md` §amount-columns' "absent reads as zero": on a ledger,
an absent or unparseable amount is a row that cannot say what it did, so it throws
`LoanLedgerFormatException` rather than summing as nothing. A mixed-unit ledger throws for the
same reason — an ALGO repayment cannot be subtracted from a USDC principal.
