# Core — agent notes

Directory-level rationale for the primitives every other layer shares. Terse one-line
doc-comments live in the code; the "why" lives here.

## §rail-amount — the amount type on every value-moving call

`RailAmount` (`Core/RailAmount.cs`) is an arbitrary-precision `BigInteger` base-unit count
plus the `ValueDimension` it is counted in. It replaced a `ulong` amount surface (decisions
D13 + D15).

**Why the old type had to go.** `ulong` was chosen because Algorand's ASA `AssetAmount` is
ulong-native. `ulong.MaxValue` is ~1.845e19 wei ≈ **18.45 ETH**, so the shared interface
could not express an ordinary Ethereum holding at all — Ethereum was the one chain the
signature genuinely could not serve, and it needed a parallel `BigInteger` module plus an
edge-conversion type to work around it. Both are now gone.

**Why the dimension exists.** An amount with no unit is only safe while there is exactly one
kind of unit. The moment a fiat rail exists, `500` means five dollars on one rail and five
base units of some asset on another, and nothing downstream can tell the difference. The
dimension travels *with* the count so a provider can refuse rather than reinterpret:

- `BaseBlockchainProvider.RejectUnsettleableAmount` refuses a fiat-denominated amount on a
  chain provider. Every chain value method calls it.
- `BaseBlockchainProvider.NarrowToChainBaseUnits` additionally narrows to the 64-bit count a
  ulong-native chain SDK takes (Algorand ASA, SPL), and **refuses rather than wraps** when
  the amount does not fit. That refusal is the old boundary type's one non-negotiable
  property, kept. Ethereum deliberately does not narrow at all.

D15 pins fiat to **USD**, but as DATA (`ValueDimension.Code`), so a second currency is a
value change rather than a type change. Fiat codes are upper-cased on construction: two
spellings of one currency comparing unequal would silently split a ledger.

**Invariants worth knowing before editing:**

- Amounts are non-negative by construction. A reversal is a different operation, never a
  signed amount on the same call. `RailAmount.In` throws on a negative count.
- `default(ValueDimension) == ValueDimension.NativeChainAsset`. A chain call already names
  its asset in a sibling `tokenId` argument, so the common case must not require spelling it
  twice — but a caller that *wants* to pin the asset can, via `ValueDimension.ChainAsset`.
- There is an implicit `ulong` → `RailAmount` widening (lossless) and deliberately **no**
  implicit narrowing back. `TryToUInt64` is the only way down and it can say no.
- Moq matchers must be typed `It.IsAny<RailAmount>()`. Moq refuses a matcher that only
  reaches the parameter through a user-defined conversion, so `It.IsAny<ulong>()` fails at
  runtime with "is unmatchable" rather than at compile time.

**Still ulong, on purpose (INTEGRATION NOTE):** `IMintOperation.Amount` and the
`ICrossChainBridgeService` / `IWormholeAdapter` surfaces. Those are persistence- and
service-shaped and are widened with their own records; the manager widens losslessly at the
provider boundary. The `Parameters["Amount"]` path (Burn/Transfer) was always a string and is
now parsed straight to `BigInteger`, so it is NOT capped.

## §settlement-profile — a reversal window, never a finality flag

`SettlementProfile` (`Core/SettlementProfile.cs`) describes how settlement on a rail can be
undone: a `SettlementReversibility` (irreversible / reversible-within-window /
unbounded-reversal) plus the window's duration.

It is deliberately not a boolean `IsFinal`. "Final" is not a property a rail *has*; it is a
property of a moment in time relative to that rail's reversal window. A chain is effectively
irreversible the instant it confirms. A card rail carries a chargeback tail measured in
months, during which the money is sitting in the payee's account and is still not theirs. A
boolean forces both into one lie, and the lie that costs money is the one that says a card
payment is final.

Callers ask `TryGetReleaseTimeUtc(settledAt, out releaseAt)` for a **time**, not a flag. A
rail with `UnboundedReversal` answers `false` — "there is no such instant" — which a boolean
cannot express at all. Escrow release logic in later phases reads exactly this.

## §rail-environment — the network concept, de-chained

`ChainNetwork` is Devnet / Testnet / Mainnet. A fiat rail has test and live and no third, so
asking one for "Devnet" forces it to invent an answer. `RailEnvironment` carries the only
distinction every rail shares — `RailEnvironmentKind.Test` vs `Live`, i.e. is the value real
— plus the rail's own name for the environment, and converts to and from `ChainNetwork` only
where a chain is genuinely on the other side. `TryToChainNetwork` returns false for a fiat
rail's `live`; it must never silently become `Mainnet`.

`ValueRailBinding` is what the factory hands a provider to bind it. Its `Endpoint` is a
`BlockchainNetworkConfig` — named for the chain era but rail-generic in content (base URL,
API key/token, timeout and retry budgets, enabled flag), which is what a payment-processor
rail needs too. Renaming that type is a mechanical follow-up; its shape was never the reason
a fiat rail could not be registered.

## §signing-context

See `Core/SigningContext.cs`. One field is load-bearing for the custody seam: `ChainType` is
stamped ONCE, in `Managers/BlockchainOperationManager.BuildSigningContext`, so the chain-aware
platform key door derives the right chain's key. An unstamped context takes the door's
Algorand-pinned legacy path (`KeyCustodyService.LegacyPlatformChainType`), which fails closed
for every other chain. Stamping at the single build site means providers need no edits and
none can drift.
