# Services/Analytics — directory notes

`AnalyticsReadService` is a **read projection** over `operation_log`. It adds no table,
no node type, and no write path. If a change here needs a write, the change is wrong.

## §unpriced-representation

Two rate sources exist: `UnpricedValueRateSource`, which never quotes, and
`TenantDeclaredValueRateSource` (§declared-rate-source), which quotes **only** what a tenant
declared for a **named** asset. A node that has not called `AddValueRateSources`, or whose
consumer has declared nothing, therefore reads back entirely unpriced. That is not a defect to
route around — it is the honest state of such a node, and this service's only job on the value
axis is to not lose it between the column and the wire.

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

Forbidden in this directory, explicitly: pricing anything from inside the read projection; a
rate source that derives, interpolates or defaults a number instead of relaying a declared one;
back-filling `value_*` from any later price; a "best effort" or "approximate" rate; a
last-known value presented as current; any write to `operation_log`. `AnalyticsReadService`
must never take an `IValueRateSource` dependency: pricing happens once, at settlement, and a
read that could price would re-price history on every call.

## §declared-rate-source

`TenantDeclaredValueRateSource` is the one sanctioned implementation of `IValueRateSource`
here, and it is a **relay, not an oracle**. Under D7/D16 rate policy belongs to the consumer:
ArdaNova computes the rate, AZOA executes the move and records the rate it was given, with
that consumer's own source string and observation timestamp. The class computes no economics
and holds no opinion about price; its whole substance is a list of refusals, each returning
`null` (which the manager records as "never priced"):

| refused | why |
| --- | --- |
| a fiat dimension | a fiat amount is already money; converting one currency to another needs an FX rate nobody declared |
| a dimension that names no asset | **the D16 trap.** Chain type alone does not identify an asset — every ASA on Algorand is chain type `Algorand` — so pricing an unnamed amount is how N project-token units become N micro-Algos on a statement |
| a declaration for a different chain or asset than was asked for | defence in depth against a store that keys loosely; a near-miss is a different price, not a fallback |
| a declaration in any currency but USD | D15 pins the stamped currency; a second currency is a deliberate change here, not a silent conversion |
| a declaration dated after the settlement it would price | a timestamp that cannot be true is not evidence (5-minute skew grace) |
| a declaration older than `MaxDeclarationAge` | stale-presented-as-current is the fabrication §value-stamp forbids by name |

**Units are the sharpest edge on this seam.** A declared rate is **minor units of the currency
per ONE BASE UNIT of the asset** — the convention `ValueStamp.From` multiplies by. At ARDA's 2
decimals, "1 ARDA = $0.01" is `0.01`, not `1`; the difference is a silent 100x on a financial
record. `TenantDeclaredRate.FromWholeUnitPrice` converts from the way a consumer states a
price (dollars per whole token, plus the asset's decimals) and **throws rather than rounds**
when the conversion is not exact, because a rounded money constant is a fabricated one.

`ITenantDeclaredRateStore` is the read seam. The default implementation shipped here,
`ConfiguredTenantDeclaredRateStore`, parses the `ValueRates:Declared` configuration once; the
durable store that reads declarations registered over the wire lands with the reserve exchange
(`Services/Reserve/TenantDeclaredRateStore`). A malformed or duplicated configuration entry
**throws at construction** instead of being skipped: a rate typo that quietly disabled pricing
would look exactly like a node whose consumer declared nothing, and those two states must not
be confusable.

```jsonc
"ValueRates": {
  "MaxDeclarationAge": "1.00:00:00",
  "Declared": [
    {
      "ChainType": "Algorand",
      "AssetId": "1001",                    // the ASA id; never blank
      "CurrencyCode": "USD",
      "MajorUnitsPerWholeToken": "0.01",    // or MinorUnitsPerBaseUnit — exactly one
      "AssetDecimals": 2,
      "DeclaredAt": "2026-09-07T00:00:00Z", // when the TENANT observed it
      "Source": "ardanova:treasury-rate",
      "Version": "3"
    }
  ]
}
```

The stamp's `RateSource` column ends up as `tenant-declared:<source>@<version>`, so rate,
source, version and observation instant all survive onto the row — the four things a figure
needs to be re-derivable a year later.

**Not enforced yet, and worth knowing:** `ValueRateQuote` carries no back-reference to the
dimension it priced, so `BlockchainOperationManager` cannot verify that a returned quote is
for the asset it asked about — the check lives inside this source instead. Giving
`ValueRateQuote` a priced-dimension field (in `Core/ValueStamping.cs`) and asserting it at the
stamping site would move that guarantee out of every source's good behaviour and into the
type.

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
stamp a Burn, Transfer, Exchange or Swap whose amount parameter (`Amount`, or `AmountIn` for a
Swap) is absent or unreadable. The execution paths may default a missing amount (a transfer of
one asset); the value stamp may not, because a fiat figure computed from a defaulted count is
indistinguishable on the wire from one computed from a real one.

## §stampability

A stamp needs two things: a broadcast status, and a quantity the row actually records in an
asset the row actually names.

* **Mint** stamps the typed `Amount`, dimensioned with `AssetId`/`TokenId` when the row names
  one — an ASA-create names none yet, and then the dimension stays whatever the caller set.
* **Burn** stamps `Amount` in `TokenId`; **Transfer** stamps `Amount` in `SourceTokenId`.
* **Exchange** and **Swap** stamp the leg they GIVE UP — `Amount` in `SourceTokenId`, and
  `AmountIn` in `TokenIn`. That leg is the consideration the initiator paid and the only one
  whose quantity the row records. The received leg is NOT stamped and must never be inferred
  from the rate: a stamp is the value of one named quantity, not the story of a trade.
* **DeployContract/CallContract** move no quantity and are never stamped; a non-broadcast
  status (`Failed`, `AwaitingSignature`, `Unknown`, `Pending`) is never stamped whatever the
  operation type.

**The dimension is the point.** `ValueDimension.NativeChainAsset` means "whatever asset this
call itself named", so an amount reaching a rate source undimensioned invites the exact
mispricing D16 exists to prevent. `TryReadRailAmount` therefore takes the asset id
**explicitly** and every branch resolves it the same way the matching `Execute…Async` resolves
the token id it hands the provider — the stamp names what the call named.

`TransactionHistoryEntry.IsStampable` carries stampability per row and
`PositionSummary.UnstampableEntryCount` per position, so a consumer does not read an
unstampable Deploy as a missing price. That predicate mirrors
`BlockchainOperationManager.TryResolveStampableAmount` / `HasBroadcastForValueStamp`; if that
one moves, this one moves with it.

> ⚠ **Divergence, open.** `AnalyticsReadService.IsStampable` still answers `Mint|Burn|Transfer`
> only, so a stamped Exchange or Swap now reads back `"pricing": "Priced"` beside
> `"isStampable": false`. The wave item that widened the manager does not own
> `AnalyticsReadService.cs`; closing this means widening that predicate to the five types above
> and the `AnalyticsReadServiceTests` theory with it. Written down here rather than left to be
> discovered on the wire.

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

The value-rate seam brings its own extension so `Program.cs` spends one line on it:
`builder.Services.AddValueRateSources(builder.Configuration);`
(`Services/Analytics/AnalyticsRegistration.cs`). Both registrations inside it are `TryAdd`, so
an override — a durable `ITenantDeclaredRateStore`, or a different `IValueRateSource` — must be
registered **before** that call. Omitting the call entirely is safe and fails in the right
direction: `BlockchainOperationManager` takes `IValueRateSource` as an optional constructor
parameter and falls back to the source that never quotes, so a node that forgets the line
records unpriced rows, never mispriced ones.

Unlike the Phase 5 governance and credential primitives, nothing on this surface is gated:
the analytics routes are live. It is a read projection with no write path and no node
type, which is exactly why it needed no gate.
