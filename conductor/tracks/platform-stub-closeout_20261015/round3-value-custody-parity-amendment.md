---
type: contract-amendment
status: accepted-with-changes
owner: lane-B
coordinates-with: [lane-A, lane-C, ethereum-provider-lane]
reviewed: 2026-10-16
---

# Value custody, Hold and exchange parity for Ethereum and Solana (B3-12)

**Verdict: accepted with changes** (independent review 2026-10-16; the security review findings are in
`round3-join-ledger.md` §Lane B findings). This revision includes every required change. It is a
round-4 design and adds no code. Section 3's Ethereum atomicity is accepted **as a direction only**:
the reviewers must first choose between permit and escrow (see §3). Lane A owns the Solana provider
core. Ethereum provider files stay with their provider lane. Lane B owns only the generic contracts
below.

## Current state (source inventory, 2026-10-16)

- Algorand implements `IValueTransferTransactionModule`, `IValueCustodyModule` and an unadvertised
  `IExchangeTransactionModule`.
- Ethereum (`EthereumProvider.ValueTransfer.cs`) and Solana (`SolanaProvider.ValueTransfer.cs`)
  implement only `IValueTransferTransactionModule`. They honour `PreparationKey` and ignore
  `ValueTransferIntent.Cost`. Neither provides the custody or exchange module.
- So Hold and exchange refuse both chains before admission. This fails closed, and it must stay that
  way until this amendment is implemented and reviewed.

## Binding contract (unchanged generic seam)

The generic records in `Models/Blockchain/ValueCustodyContracts.cs` and `ExchangeWireContracts.cs` do
not change. Each chain maps its own mechanics onto the existing fields:

| Generic field | Algorand | Ethereum (ERC-20) | Solana (SPL) |
| --- | --- | --- | --- |
| `RecipientReady` | ASA opt-in exists | always `true` | recipient associated token account (ATA) exists for the exact mint and token program |
| `AcceptanceNetworkFeeBaseUnits` | opt-in fee | `0` | ATA-create signature fee (recipient-paid path) |
| `AcceptanceMinimumBalanceBaseUnits` | min-balance increment | `0` | rent-exempt minimum when the recipient pays |
| `TransferCosts.NonrefundableProtocolCostBaseUnits` | `0` | `0` | ATA rent when the **reserve** pays: a capped nonrefundable cost (§2) |
| `TransferNetworkFeeBaseUnits` | flat fee × members | `gasLimit × maxFeePerGas`, plus the L1 data fee on a rollup (§4) | base signature fee + compute-unit price × limit |
| Absence proof | caught-up past `lastValid` | a **different** finalized transaction consumed the nonce (§1) | finalized height > `lastValidBlockHeight` (§3) |

## 1. Ethereum absence, cancellation and nonces

- **Absence is proved only by a finalized different transaction at the same nonce.** A signed EIP-1559
  transaction never expires. "Nonce unused after a fee-cap window" is therefore not absence: the
  original could be rebroadcast after compensation. This is the same rule lane C uses for DEX
  stages. The only alternative is a settlement contract that enforces `validUntil` and keeps a
  used-intent mapping, and then absence means the deadline passed with no use recorded.
- **A same-nonce cancel is a real coordinator step.** It has its own authority (the original
  signer's platform or participant context, captured at admission), its own saved signed bytes and
  its own source state (`Cancelling` → `Cancelled` or `Confirmed`). It is prepared, submitted and
  observed like any other stage. Compensation runs only after the cancel (or any other
  different-transaction consumer) finalizes. If the original wins the race, the source settles.
- **Durable nonce allocation journal.** Each signing account allocates nonces from a journal row in
  the same transaction that saves the unsigned plan. This aligns with lane C's allocator: a nonce
  may be reused only once it has been released, never signed and never exposed. A nonce whose bytes
  were ever signed or sent stays bound to that operation until its finalized consumer is known.

## 2. Solana custody mechanics

- **No durable-nonce instructions** (`AdvanceNonceAccount`) and **no address lookup tables** in
  custody or exchange transactions. Each wire uses a recent blockhash and static account keys, so
  its validity window is bounded and every account is visible to the validator.
- **Recipient ATA rent.** If the reserve pays, the rent is refunded to whoever later closes the
  account (the recipient), and repeated creation would drain reserve SOL. So reserve-paid rent is
  disclosed as `NonrefundableProtocolCostBaseUnits`, capped by the accepted quote, and counted
  against the caller's cost consent. Otherwise the recipient pays through the separate acceptance
  stage. Both quotes show which path applies.
- **Token mechanics.** Token-2022 extensions that change the net amount are refused before
  admission: transfer fee, transfer hook, confidential transfer, permanent delegate and default
  frozen state.

## 3. Exchange atomicity

- **Solana.** One versioned transaction carrying every ordered leg as `TransferChecked`. The reserve
  is the fee payer and the participant and treasury co-sign. A memo binds the canonical intent hash.
  Absence counts only once the **finalized** block height exceeds `lastValidBlockHeight`, and
  `getSignatureStatuses` with `searchTransactionHistory=true` returns nothing. Confirmation needs a
  finalized transaction with `meta.err == null` whose pre/post token balances match the legs exactly.
- **Ethereum (direction only).** One reviewed settlement-contract call is the spender. It has no
  generic multicall and no arbitrary target or calldata. The participant leg is either:
  - a **Permit2 witness transfer** bound to the canonical intent hash, with the settlement contract
    as spender, the exact amount, and `deadline` = the source's validity window; or
  - a **pre-committed escrow** deposit released only by that contract.

  Before any wire work, the reviewers choose one, and that choice needs its own contract security
  review. Confirmation needs a finalized-block receipt whose decoded `Transfer` logs exactly match
  the legs, with no extra effects.

## 4. Fee caps

The accepted cap covers everything the payer can be charged. On an L2 rollup it includes the L1 data
fee (the provider quotes the L1 component from the chain's fee oracle at preparation, and the cap
bounds L2 execution + L1 data together). Until that quote exists, the Ethereum modules are **scoped
to L1 networks only**, and admission on a rollup network fails closed.

## Acceptance before activation

The fixture classes mirror `round3-exchange-provider-wire.md`:

- overlapping preparation returns one plan;
- a lost response rebroadcasts identical bytes;
- authority revoked before first egress forbids the send;
- a hidden extra instruction, an extra log, a lookup table, a durable-nonce instruction or excess
  fees are refused;
- partial evidence cannot confirm;
- incomplete absence cannot compensate;
- on Ethereum, a same-nonce cancel that loses to the original settles the source instead of
  compensating it.

Capability discovery stays unadvertised for each chain until source review and these fixtures pass
in the integrated sweep.

## Round-4 carry-forward

Provider-lane implementation, the Ethereum permit/escrow decision and contract review, the rollup fee
quote, and a nonce journal shared with lane C. The Hold and exchange coordinators are already
chain-generic through the module seams. The only lane-B work is the `Cancelling` source state and
cancel step in §1, which will be added alongside the Ethereum module.
