# Platform-unit genesis — the one-time act that gives a node its unit

**Audience:** The node operator, once, per network.
**Scope:** How this node's platform unit is created, what is frozen the moment it is,
how to prove it on devnet, and how to promote the identical definition to mainnet.
**Track:** W3.2 (D49 / N-X2).
**Last updated:** 2026-09-08

---

## 0. Why this is first

Until genesis runs, **every platform-unit balance, every rate and every cash-out reads
zero** — not because the code is wrong, but because there is no asset for them to read.
Genesis is the first thing on the critical path and it is the cheapest thing on it: the
mint path already existed end to end (D49), so this is configuration and operations, not
a chain integration.

What W3.2 added on top of that path is only what a one-time irreversible act needs and a
general-purpose mint does not:

| Need | Where it lives |
| --- | --- |
| A second genesis must be refusable | `platform_token` single-winner claim, unique on `(chain, network)` |
| A retry must not create a different asset | The definition is **frozen** at claim time |
| The asset id must be readable later | `platform_token.asset_id` — `appsettings.json` cannot hold it, because the id does not exist until the mint returns and the node cannot write its own configuration at runtime |
| Circulating supply must not be invented | Derived from the reserve ledger at read time, never stored |

---

## 1. The definition — decide once

> **The single most important sentence in this document:** an Algorand ASA's total
> supply, decimals and unit name **cannot be changed after creation**. Changing any of
> them means creating a **second** asset, with a second id, which every downstream system
> would then have to be re-pointed at. Decide once, prove it on devnet, then promote the
> identical definition.

The N-X2 parameters ship as constants in
[`Services/Reserve/PlatformTokenGenesis.cs`](../Services/Reserve/PlatformTokenGenesis.cs)
(`PlatformTokenGenesisDefaults`), not as configuration, so the devnet proof and the
mainnet promotion run the same numbers:

| Parameter | Value | Why |
| --- | --- | --- |
| Chain | `Algorand` | The ASA capability module is Algorand-specific |
| Unit name | `ARDA` | 4 bytes, inside Algorand's 8-byte limit |
| Asset name | `Arda` | Inside Algorand's 32-byte limit; never shown to a sponsor |
| Decimals | `2` | Every balance is a whole number of cents |
| Total supply | `2_000_000_000` base units = **20,000,000.00 units** | See §1.1 — a ceiling, not a circulating supply; it is a CHOICE now, not a limit |
| Holder | The platform reserve | Creator **and** manager / reserve / freeze / clawback (D48) |

**Total supply is a ceiling on issuance, not a circulating supply.** Circulating supply
starts at zero and grows only as the reserve issues; see §4.

### 1.1 The supply ceiling — decide the number before mainnet

**Resolved (OI-26).** The old `int.MaxValue` ceiling is gone. `IAlgorandASAModule.CreateASAAsync`
now takes the total as a `ulong` — the chain's own ASA supply field — so the only remaining
ceiling is Algorand's `uint64`, which nobody can widen. `FungibleTokenManager` enforces that
boundary and `PLATFORM_TOKEN_SUPPLY_CEILING` now names the CHAIN field rather than a surface
somebody could go and change.

N-X2 names 10^15 base units. That total is now **reachable and mintable**; it is inside the
rail ceiling and `PlatformTokenGenesisDefaults.IntendedTotalBaseUnits` records the intent.

**What is still open is the CHOICE, not the capability.** The total is fixed forever at
genesis — widening anything afterwards does not raise an existing asset's supply, it would
require a second asset. So decide deliberately which total to mint (the 2_000_000_000 in the
table above, or `IntendedTotalBaseUnits`) and record the decision before promotion.

> A request cannot express an over-rail total through this path at all: `PlatformTokenGenesisRequest.Total`
> is a `ulong?`. The refusal side of the boundary is proved in
> `tests/AZOA.WebAPI.Tests/Managers/FungibleTokenSupplyCeilingTests.cs`.

---

## 2. Preconditions

Genesis fails closed on every one of these, before anything is claimed or broadcast:

1. **A platform key for the chain.** The reserve derives its address inside the custody
   door; no key means no address (`Blockchain:PlatformKeys` / the platform mnemonic — see
   `NODE-HOST.md` §8.11).
2. **The chain is listed under `Reserve:Chains`** (or the list is empty = no restriction).
3. **`Reserve:ExpectedAddresses:<Chain>` matches the derived address**, if you have set it.
   This is the check that catches a swapped mnemonic before value moves.
4. **The node operator's user passes the value-access gate.** The mint runs through
   `FungibleTokenManager`, whose KYC/readiness choke point is fail-closed and is evaluated
   against the operator running genesis. A `KYC_FORBIDDEN:` message here means the
   operator's own participant record is not approved.
5. **Fees.** The reserve must hold enough native units for the create fee plus the
   min-balance bump the new asset adds to the creating account. On a **dev/test** network
   genesis tops the reserve up from the provider faucet automatically (§3); on **mainnet**
   it never does — fund the reserve yourself first.

---

## 3. Devnet: prove the definition

```bash
# 1. Read the current state. Before genesis this is 404 PLATFORM_TOKEN_NOT_GENESISED.
curl -sS -H "Authorization: Bearer $OPERATOR_JWT" \
  "$NODE/api/admin/platform-token"

# 2. Run genesis. Both gates apply: Operator AND a node-operator session under 10 minutes old.
curl -sS -X POST -H "Authorization: Bearer $OPERATOR_JWT" \
  -H "Content-Type: application/json" \
  -d '{"expectedNetwork":"Devnet","confirmIrreversible":true}' \
  "$NODE/api/admin/platform-token/genesis"

# 3. Read it back. definition.assetId is now the platform unit; supply.circulating is 0.
curl -sS -H "Authorization: Bearer $OPERATOR_JWT" \
  "$NODE/api/admin/platform-token"
```

An empty body (`{"confirmIrreversible":true}`) uses the defaults in §1 — that is the
intended shape. Supply/decimals/unit name are only worth stating explicitly when you are
deliberately departing from the constants, and then you must change the constants too so
the mainnet promotion matches.

**The faucet step.** On a non-mainnet network with a faucet-capable provider
(`AlgorandProvider.SupportsFaucet => true`), genesis reads the reserve's native balance and,
if it is below 1 ALGO, dispenses 5 test ALGO before minting. The outcome is reported as
`operationsNote`; it never fails the run on its own, because the mint reports the real
consequence of an unfunded reserve.

**Verify the definition on chain, not just in the response** — the response is this node
telling you what it did; the chain is what happened:

```bash
# Algorand indexer / algod, devnet
curl -sS "$ALGOD/v2/assets/<assetId>" | jq '.params
  | {total, decimals, "unit-name", name, creator, manager, reserve, freeze, clawback}'
```

Check all of: `total`, `decimals`, `unit-name`, and that **creator, manager, reserve,
freeze and clawback are all the platform reserve address** (`definition.reserveAddress`).

---

## 4. What circulating supply means here

`GET /api/admin/platform-token` returns supply in base units as decimal strings:

| Field | Meaning |
| --- | --- |
| `total` | The genesis ceiling |
| `circulating` | **Reserve outflow**: `issued − returned` summed over every reserve-ledger partition |
| `held` | Committed to holds — promised, but not yet out of the reserve |
| `inReserve` | `total − circulating` |

Circulating supply is **derived from `platform_reserve_ledger` at read time, never stored**,
so it cannot drift from what the reserve actually issued.

**It is never fabricated.** If the reserve ledger row for the asset cannot be read, the
endpoint returns **503 `PLATFORM_TOKEN_SUPPLY_UNAVAILABLE`** rather than `0`. That matters
because the rate is backing-derived and divides by circulating supply: a zero invented from
an unreadable ledger would price the unit off a supply nobody knows.

---

## 5. Promotion to mainnet — the checklist

Run this **in order**. Steps 1–3 are the ones that are expensive to get wrong.

- [ ] **1. The genesis total (§1.1) is chosen and recorded** — the provider surface is already
      widened to the chain's `uint64` (OI-26), so this is now a decision about WHICH total to
      mint, not about whether a larger one is reachable. *After genesis it is not changeable.*
- [ ] **2. The devnet asset was verified against the chain** (§3), not just against this
      node's own response, and all five ASA roles are the reserve.
- [ ] **3. The definition being promoted is byte-identical to the devnet one** — same
      chain, unit name, asset name, decimals and total. `PlatformTokenGenesisDefaults` is
      the single source; do not retype the numbers into the request body.
- [ ] **4. The mainnet platform key is configured and `Reserve:ExpectedAddresses:Algorand`
      is set to the derived mainnet address**, so a mistyped mnemonic fails closed.
- [ ] **5. The mainnet reserve is funded** with native units for the create fee and the
      min-balance bump. There is no faucet on mainnet, by design.
- [ ] **6. Run genesis with `"expectedNetwork":"Mainnet"`.** If the node is still wired to
      devnet the call is refused with `PLATFORM_TOKEN_NETWORK_MISMATCH` instead of minting
      the wrong asset on the wrong network.
- [ ] **7. Read the state back and verify on chain** exactly as in §3.
- [ ] **8. Record the mainnet asset id** in the deployment's own records. It is in
      `platform_token`, but an operator should not have to query the database to know it.

---

## 6. What the controls actually refuse

Every one of these is a code path, not a convention:

| Situation | Response |
| --- | --- |
| `confirmIrreversible` missing | `PLATFORM_TOKEN_NOT_CONFIRMED` — nothing claimed, nothing minted |
| `expectedNetwork` ≠ the node's network | `PLATFORM_TOKEN_NETWORK_MISMATCH` |
| Total above the chain's `uint64` ASA supply field | `PLATFORM_TOKEN_SUPPLY_CEILING`. Since OI-26 this names the CHAIN's limit, which cannot be widened by anyone |
| Unit name > 8 bytes / asset name > 32 bytes / decimals outside 0..19 | Rejected before any claim |
| A platform unit already exists and the request **matches** it | 200, `created: false` — a replay, no second mint |
| A platform unit already exists and the request **differs** | 409 `PLATFORM_TOKEN_ALREADY_EXISTS` |
| A genesis was claimed and the request differs from the claim | 409 `PLATFORM_TOKEN_DEFINITION_FROZEN` |
| The `platform_token` state cannot be read | Refused — minting over an unreadable state is how a second unit gets created |
| No reserve account resolves for the chain | Refused, and **nothing is claimed** |

---

## 7. Recovery

**A genesis attempt failed and the row is `Pending`.**
That is the designed state, not a fault. A mint that reported an error may still have
broadcast, so genesis never marks a claim terminally failed — it records
`last_failure_reason` and leaves the claim open. **Re-run genesis with the same
definition.** It resumes under the same `genesis_operation_key`, so the fungible-token
idempotency store replays the original mint rather than creating a second asset.

**`PLATFORM_TOKEN_UNRECORDED`.**
The asset **was** created on chain but the `platform_token` row was not updated. The
message carries the asset id. Re-run genesis with the same definition: the mint replays and
the row is recorded. Do not create a new asset.

**The definition really was wrong and nothing was ever minted.**
Only then, and only after confirming on chain that no asset exists for that
`genesis_operation_key`, delete the `Pending` `platform_token` row deliberately and re-run.
There is no endpoint for this on purpose: an accidental "reset genesis" button is exactly
how a second platform unit gets created.

---

## 8. Cross-links

- **`NODE-HOST.md` §8.11** — the platform reserve: key resolution, `Reserve:*` settings,
  `ExpectedAddresses`, and the named-partition oversubscription check.
- **`Services/Reserve/PlatformTokenGenesis.cs`** — the parameters, the claim discipline,
  and the faucet path.
- **`Persistence/SurrealDb/Models/PlatformToken.cs`** — the node-state record and why it
  has no `Failed` status.
