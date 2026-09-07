# Services/Analytics — directory notes

`AnalyticsReadService` is a **read projection** over `operation_log`. It adds no table,
no node type, and no write path. If a change here needs a write, the change is wrong.

## §unpriced-representation

`IValueRateSource` has exactly one implementation, `UnpricedValueRateSource`, and it never
quotes. **Every row therefore reads back unpriced today.** That is not a defect to route
around — it is the honest state of the node, and this service's only job on the value axis
is to not lose it between the column and the wire.

`ValueStamp.FromPersisted` (`Core/ValueStamping.cs`) already returns `null` when any one of
the five `value_*` columns is absent, so a partial stamp is not a stamp before this layer
ever sees it. The projection maps straight through and decides nothing.

**Three-way, never two-way.** `TransactionHistoryEntry` carries a REQUIRED, always-serialized
`Pricing` alongside the nullable `Value`:

| `pricing` | `value` | meaning |
| --- | --- | --- |
| `NeverPriced` | `null` | no rate source ever quoted this operation |
| `Priced` | present | a real source quoted it — possibly at `"rate": "0"` |

The redundancy is deliberate. A JSON client with default null-stripping, or a `.Value ?? 0`
in a consumer's tax spreadsheet, turns an absent object into a silent zero; an explicit
`"pricing": "NeverPriced"` cannot be defaulted away. A `Rate` of `"0"` is a real quote of
zero and stays `Priced` — collapsing it into `NeverPriced` would destroy the distinction the
five columns move as a group to preserve.

**Never sum an unpriced leg as zero.** `PositionSummary.RealizedValue` is emitted only when
the position is both value-complete (`UnpricedEntryCount == 0`) and quantity-complete, every
stamp agrees on one currency, and every figure parses. Otherwise it is `null`. A fiat total
that silently omits unpriced legs is a wrong number that looks right, and this is a
tax-workflow surface. Today that means every position returns `null` totals,
`isValueComplete: false`, and a full `unpricedEntryCount` — the truthful answer.

`UnrealizedValue` is **always** `null`, and not merely because nothing is stamped: a current
valuation needs a *current* quote. Re-presenting a settlement stamp as today's price is the
stale-as-current fabrication `Persistence/SurrealDb/Models/AGENTS.md` §value-stamp forbids —
"a fabricated, defaulted, or stale-presented-as-current rate on a financial record is worse
than an absent one".

Forbidden in this directory, explicitly: implementing `IValueRateSource`; back-filling
`value_*` from any later price; a "best effort" or "approximate" rate; a last-known value
presented as current; any write to `operation_log`.

## §unknown-quantity

The same rule as §unpriced-representation, on the **quantity** axis. `operation_log` does not
always record an amount: the typed `Amount` field is a Mint concept, and Burn/Transfer put
theirs in the `Amount` *parameter*, which may be absent or unparseable.

`ResolveAmount` returns `null` for that state, and `TransactionHistoryEntry` carries a
REQUIRED `AmountStatus` (`Known` / `Unknown`) beside the nullable `Amount` — the exact shape
`Pricing`/`Value` uses, and for the exact same reason: `amount ?? 0` in a consumer's
spreadsheet turns "the log does not say" into a real movement of zero. Returning the literal
`"0"` here was the §unpriced-representation defect relocated from the price axis to the
quantity axis, and it is just as wrong: a fabricated quantity on a tax record is a fabricated
figure.

A **recorded** zero stays `Known` with `"0"`, exactly as a stamped rate of `"0"` stays
`Priced`. The absence is the fact; a real zero is a different fact.

`PositionAccumulator` therefore leaves an unknown-quantity entry OUT of `Quantity` and counts
it in `UnknownAmountEntryCount`, which `IsQuantityComplete` requires to be zero. Folding it in
as zero would under-report the position while the completeness flag still claimed the total
covered every entry.

The mirror rule upstream: `BlockchainOperationManager.TryResolveStampableAmount` REFUSES to
stamp a Burn or Transfer whose `Amount` parameter is absent or unreadable. The execution paths
may default a missing amount (a transfer of one asset); the value stamp may not, because a
fiat figure computed from a defaulted count is indistinguishable on the wire from one computed
from a real one.

## §stampability

Per §value-stamp only **Mint, Burn and Transfer** are ever stampable — Swap and Exchange move
two assets and Deploy/Call move none — and a non-broadcast status (`Failed`,
`AwaitingSignature`, `Unknown`, `Pending`) is never stamped. `TransactionHistoryEntry.IsStampable`
carries that fact per row, and `PositionSummary.UnstampableEntryCount` carries it per position,
so a consumer does not read an unstampable Swap as a missing price. The predicate here mirrors
`BlockchainOperationManager.TryResolveStampableAmount` / `HasBroadcastForValueStamp`; if that
one moves, this one moves with it.

## §completeness-is-a-field

Every total on this surface ships next to the count of what was left out of it.

* `Quantity` folds only **settled**, **direction-classified**, **quantity-bearing** entries.
  An entry whose amount the log does not record is counted in `UnknownAmountEntryCount`
  (§unknown-quantity) rather than folded in as zero. Swap/Exchange/Deploy/Call
  have no direction the log determines, so they contribute nothing and are counted in
  `UnclassifiedEntryCount` rather than folded in at a guessed sign; anything not in a terminal
  state is counted in `UnsettledEntryCount`. `IsQuantityComplete` is true only when both are zero.
* `PositionsResponse.IsComplete` is false when the scan hit `MaxPositionScanEntries` before
  exhausting the window. A truncated total is stated as truncated, never rendered as final.

Direction is `Mint: +1`, `Burn: -1`, `Transfer: -1` — `operation_log` records the *initiator's*
operation, so a transfer is an outflow from the subject. `Quantity` is consequently a **signed**
base-unit string, unlike `RailAmount`, which refuses negatives because a rail *call* moving a
negative amount would move value the wrong way. A net position is not a rail call.

## §self-scope

Both routes read the subject exclusively from the credential (`ClaimTypes.NameIdentifier` /
`sub`). `AnalyticsQueryRequest` deliberately has no user-id field, so none can be smuggled in
through model binding — the same shape as the ActionGraphBundle owned-resource precedent. A
node-operator cross-user view belongs behind the existing `NodeGovern` policy on its own
route; it must never become an optional parameter on a self route, because then the safe call
and the privileged call are one request apart.

## §paging

`IBlockchainOperationStore.QueryAsync` exists because `GetByUserAsync` returns a user's entire
log unbounded, which cannot serve a history surface. The Surreal implementation is shaped to
the existing `operation_log_user_created` index — equality on `user_id`, a range on
`created_date`, ordered on the same column — with a secondary `id` sort so paging stays stable
when several rows share a timestamp. The two date bounds bind to two distinct lowercase
parameter names: one parameter reused for both bounds would overwrite itself, and a mixed-case
name is case-folded in the query text but not in the RPC vars, so it binds to `NONE` and
matches nothing without raising an error.

## §wiring

`IAnalyticsReadService` → `AnalyticsReadService` is registered explicitly in `Program.cs`
(`AddScoped`), because this directory is **not** covered by the
`Services/Actions/Handlers` assembly scan that picks up node handlers. A new service added
here needs its own registration line or `AnalyticsController` fails to resolve at request
time — there is no convention that will find it for you.

Unlike the Phase 5 governance and credential primitives, nothing on this surface is gated:
the analytics routes are live. It is a read projection with no write path and no node
type, which is exactly why it needed no gate.
