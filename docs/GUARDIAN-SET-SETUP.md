# Wormhole Guardian Set — Operator Setup & Verification

**Audience:** Operators/SREs enabling Wormhole VAA value flow on a network.
**Status:** Ops/config gate. This is the procedure behind
`RESIDUAL-RISK-RUNBOOK.md` §4 "real testnet/mainnet Guardian sets".
**Last updated:** 2026-05-18 (api-safety-hardening track)

---

## 0. Why this exists (one paragraph, no crypto re-explanation)

The Guardian set is a **trust root**. A VAA is accepted only if a Byzantine
quorum of the configured Guardian addresses cryptographically signed it. The
crypto is already implemented and unit-proven — `Secp256k1VaaSignatureVerifier`
(SEC1 §4.1.6 secp256k1 ECDSA public-key recovery over the canonical Wormhole
VAA digest `keccak256(keccak256(body))`, 65-byte `r||s||v`, Guardian address =
last 20 bytes of `keccak256(uncompressedPubKey[1..])`) plus `WormholeAdapter`
which enforces quorum `⌈2n/3⌉ = floor(2n/3)+1` (e.g. **13 of 19**) and
ascending-unique Guardian indices. See `RESIDUAL-RISK-RUNBOOK.md` §1 / §4 for
the spec confirmation. **The only thing the operator supplies is the list of
Guardian addresses for the network — and that list is security-critical: a
wrong/forged entry silently moves the trust root.** Hence the mandatory
two-source verification below. This document deliberately contains **no
authoritative address list** (except the test-verified devnet one); any address
shown is a labelled sanity fingerprint, never a source of truth.

---

## 1. Fail-closed guarantee (what happens when a set is absent)

`Blockchain:Wormhole:GuardianSets` is a map: key = Guardian-set index string
(the VAA header `GuardianSetIndex`), value = **ordered** list of `0x` 20-byte
addresses where **list position == Guardian index**.

If the set for a network's index is **absent or empty**, the verifier returns
`false` for every signature against it ⇒ **every VAA is rejected**. This is
intentional and safe — it is the default posture, not a bug. No Wormhole value
can flow on a network until its real, verified set is configured. There is no
placeholder/guessed set anywhere in the repo; do not add one.

---

## 2. Per-network requirements

### 2.1 Devnet — DONE, nothing for the operator to do

- Guardian set **index `0`**, **single guardian**:
  `0xbeFA429d57cD18b7F8A4d91A2da9AB4AF05d0FBe`
- This is the public address of the documented Wormhole devnet/"tilt"
  deterministic Guardian private key.
- **Shipped + test-verified** in `appsettings.Development.json`
  (`Blockchain:Wormhole:GuardianSets: { "0": [ "0xbeFA42…0FBe" ] }`). Unit test
  `Secp256k1VaaSignatureVerifierTests.DevnetTiltGuardianAddress_IsDerivedFromTheKnownDevnetPrivateKey`
  independently re-derives this address from the documented devnet key with
  Bouncy Castle.
- **Operator action: none.** Devnet works out of the box.

### 2.2 Mainnet — NOT shipped; operator must supply + verify (trust root)

- Guardian set **index `4`**, **19 guardians**.
- **NOT in the repo** and will **never** be guessed/partially listed here. The
  operator retrieves and independently verifies the full ordered 19-address
  list before mainnet Wormhole value flow.
- Authoritative retrieval (use **at least two independent** of these and require
  byte-for-byte agreement):
  - **A — on-chain Core Contract (canonical):** call `getGuardianSet(index)` on
    the Wormhole Core Bridge contract on Ethereum mainnet
    (`0x98f3c9e6E3fAce36bAAd05FE09d375Ef1464288B`) via a **trusted** Ethereum
    mainnet RPC. First read the current index via the contract's
    `getCurrentGuardianSetIndex()` and confirm it equals the index your VAAs
    carry (expected `4`); then read that set's addresses in order.
  - **B — Wormhole monorepo / governance source:** the guardian-set definition
    in the official `wormhole-foundation/wormhole` repository (governance
    guardian-set artifacts) for the same index.
  - **C — independent explorer cross-check:** Wormholescan
    `…/guardianset/current` (or the equivalent public Guardian-set API). Treat
    as a *second opinion*, not primary truth.
- **Sanity fingerprint only — VERIFY, DO NOT TRUST THIS DOC AS SOURCE.** The
  well-known leading addresses of mainnet GS index 4 are commonly:
  - `GS4[0] = 0x5893b5a76c3f739645648885bdccc06cd70a3cd3`
  - `GS4[1] = 0xfF6CB952589BDE862c25Ef4392132fb9D4A42157`
  - `GS4[2] = 0x114De8460193bdF3A2fCf81f86a09765F4762fD1`
  - `GS4[3] = 0x107A0086b32d7A0977926a205131d8731d39cBEb`
  These four are a **fingerprint to catch a gross mistake only**. The operator
  obtains **all 19** (including re-confirming these four) from source A and
  cross-checks against B and/or C. If any of these four do not match the
  on-chain contract, **stop** — your retrieval path is compromised or stale.

### 2.3 Testnet — NOT shipped; operator must obtain current index + address(es)

- Wormhole testnet historically uses a **single-guardian** set, but the **index
  and address are network state, not a constant** — they can change.
- The operator must obtain the **current** Guardian set index and its
  address(es) from the authoritative testnet source the same way as mainnet:
  - on-chain `getCurrentGuardianSetIndex()` + `getGuardianSet(index)` on the
    **Wormhole testnet Core contract** (testnet Core Bridge address per the
    official Wormhole testnet contract addresses doc) via a trusted testnet RPC;
  - cross-checked against the Wormhole testnet config / Wormholescan testnet
    Guardian-set API.
- **This document deliberately asserts no specific testnet address or index** —
  obtain both from the authoritative testnet source at setup time and record
  them on the checklist below.

---

## 3. Exact appsettings shape (where the verified set goes)

Drop the verified set into the **per-environment** appsettings file for the
target network (NOT the base `appsettings.json`, which intentionally carries no
testnet/mainnet sets and stays fail-closed):

- Mainnet → `appsettings.Production.json` (or your mainnet env file)
- Testnet → `appsettings.Staging.json` / `appsettings.Testnet.json` (your
  testnet env file)
- Devnet → already in `appsettings.Development.json` (do not touch)

Shape (same key in every environment; **order = Guardian index**):

```jsonc
{
  "Blockchain": {
    "Wormhole": {
      "GuardianSets": {
        // "<setIndex>": [ "0x<addr-0>", "0x<addr-1>", ... ] — list order IS the Guardian index
        "4": [
          "0x<gs4-guardian-0-verified>",
          "0x<gs4-guardian-1-verified>",
          "0x<gs4-guardian-2-verified>",
          "0x<… all 19, in on-chain order, verified across two sources …>"
        ]
      }
    }
  }
}
```

Notes:
- The map key MUST equal the VAA header `GuardianSetIndex` for the network
  (mainnet expected `4`; testnet = whatever the current testnet index is).
- Addresses are `0x`-prefixed, 20 bytes (40 hex chars); case-insensitive.
- A `__comment` string key alongside the numeric indices is tolerated by the
  binder (the verifier only `TryGetValue`s numeric index keys) — used in the
  shipped files for documentation; not required.
- Absent/empty ⇒ fail-closed (all VAAs rejected). This is the intended safe
  default and the reason the base file ships with none.

---

## 4. Operator verification checklist (sign-off required before value flow)

Complete and record per network before enabling Wormhole value flow. All boxes
must be checked by the operator; this checklist is the §4 ops-gate sign-off.

```
Network: ____________________   Date: __________   Operator: ____________________

[ ] Source A (on-chain Core Contract getGuardianSet) — RPC endpoint used: ____________
[ ] Source A: getCurrentGuardianSetIndex() value = ______  (matches VAA header GuardianSetIndex: ______)
[ ] Source B (Wormhole monorepo/governance OR Wormholescan guardianset/current) — ref/URL: ____________
[ ] Address list from A and B match BYTE-FOR-BYTE, in order (no reordering, no case-only diff)
[ ] Guardian COUNT matches expected (mainnet = 19; testnet = ____)
[ ] Mainnet only: GS4[0..3] match the sanity fingerprint in §2.2 AND the on-chain contract
[ ] Set written to the correct per-environment appsettings file under Blockchain:Wormhole:GuardianSets
[ ] Map key == GuardianSetIndex; list order == Guardian index (position 0 = Guardian 0)
[ ] Fail-closed tested: with the set REMOVED/empty, a real VAA is REJECTED (verifier returns false)
[ ] Live-network validation: a genuine signed VAA from the live Guardian network
    on this network verifies end-to-end (RESIDUAL-RISK-RUNBOOK §4 item c)
[ ] Sign-off: operator name + date recorded above; runbook §4 row updated to DONE for this network
```

Until every box is checked for a network, leave that network's set **absent**
(fail-closed). Do not "temporarily" insert a partial or unverified set.

The `RequireFullSignatureVerification=false` escape hatch exists for
devnet/testnet **dry runs only** and MUST NEVER be set where real value moves —
it skips crypto entirely. It is not a substitute for this procedure.

---

## 5. Cross-links

- **`RESIDUAL-RISK-RUNBOOK.md` §4** — this document IS the ops procedure for the
  "real testnet/mainnet Guardian sets" gate (gate item *a*).
- **`scripts/passoff.ps1`** — the **code** sign-off gate (build + full unit
  suite + safety-critical test assertions). It passes the code gate and prints
  "OPS SIGN-OFF REQUIRED" for the ops/config gates including this one; it does
  NOT and cannot verify the real Guardian-set values — that is this checklist.
- **`Services/Wormhole/Secp256k1VaaSignatureVerifier.cs`** — the implementation
  consuming `Blockchain:Wormhole:GuardianSets` (config-driven, fail-closed).
- **`Core/Blockchain/Wormhole/WormholeConfig.cs`** — `GuardianSets` binding
  (`Dictionary<string, List<string>>`) + `RequireFullSignatureVerification`.
