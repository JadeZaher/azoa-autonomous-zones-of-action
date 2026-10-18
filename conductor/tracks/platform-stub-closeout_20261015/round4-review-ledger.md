---
type: review-ledger
round: 4
wave: W3
---

# Round 4 · W3 review ledger

Seven independent opus reviewers (separate contexts, prompted to refute), static review of every merged
lane on `feat/platform-stub-closeout` @ `21da6183` (L6 not yet merged — reviewed after it lands). Full
reports (local, gitignored): `.omc/research/round4-review/<id>.md`.

| Reviewer | Range | Verdict | C/H/M/L | Fixer |
|---|---|---|---|---|
| RV-L1 | merges #1–#3 (W0, W1, L1 platform) | PASS-WITH-FIXES | 0/1/1/5 | money (01), identity (03), fm11 (05,06), L6 (02,04,07) |
| RV-L2 | merge #4 (KYC/KYB) | PASS-WITH-FIXES | 0/0/4/5 | r4/fix-identity |
| RV-L3 | merges #5 + #5b (Stripe, D-8) | PASS-WITH-FIXES | 0/2/5/3 | r4/fix-money |
| RV-L4 | merge #6 (security) | **FAIL** | 0/2/2/3 | r4/fix-security (01,03,04,LOWs), fm11 (02) |
| RV-L5 | merge #7 (chains, WTT) | PASS-WITH-FIXES | 0/2/5/6 | r4/fix-chains (09 → fm11; 12 = F-M11) |
| RV-L7 | merge #9 (frontend/SDK) | PASS-WITH-FIXES | 0/2/5/4 | r4/fix-frontend |
| RV-X | seams `1912e865..HEAD` | PASS-WITH-FIXES | 0/4/4/3 | security (X-1,3,4,5), identity + chains (X-2), chains (X-6,8), money (X-7,11), L6 (X-9) |

## HIGH findings (each gets a bounded re-check by its reviewer before merge)

- **RV-L1-01** payout-account setup permanently locked after a lost Stripe create past 23 h (no operator exit).
- **RV-L3-H1** ambiguous Algorand broadcast recorded as plain Failed → attempt-suffixed clawback can take units twice (also collect leg, M2).
- **RV-L3-H2** D-8c arbitrage cap bypassed by moving units on chain to a lot-less account. Fix applies a conservative rule (lot-less units capped at the lowest sold price in the maturity window) — **owner to confirm**.
- **RV-L4-01** `DappManage` policy trusts the `dapp:manage` scope alone; child credentials can carry it → tenant can mint DApp managers.
- **RV-L4-02** NFT recipient resolved by `SingleOrDefault` over address after M2 allowed duplicate unproven wallets → transfers 500, or attacker recorded as owner.
- **RV-L5-01** identical client-signed Ethereum plans (shared nonce, no intent binding) → one tx settles two operations.
- **RV-L5-02** Solana "never landed" concluded from a null status / short history → Failed + retry pays twice.
- **RV-L7-1/2** operator Ethereum-recovery and wallet-identities pages silently empty (unwrapped responses vs allowlist envelope).
- **RV-X X-1** WTT Bridge/Back re-authorization requests too few scopes → node can never be authorized (stranded value).
- **RV-X X-2** `IKycGateService` registered bare → hold/exchange/NFT/fungible/deployment and direct WTT/DEX/submit-signed skip the reversal account pause.
- **RV-X X-3/X-4** cross-tenant and route inventories mis-keyed / incomplete → gates red; value-moving routes unclassified.

## Corrections recorded

- **SQL-01/02 were false positives** (RV-L4 probe): on SurrealDB 3.1.4 both BEGIN and COMMIT keep result
  slots, so the old end-relative index was correct; the absolute-index change is harmless and kept. New
  rule from the same probe: a statement after a top-level RETURN (other than COMMIT) is silently not
  executed → L6 source rule.
- The fixtures helper's "CRITICAL tenant-less KYC begin" was tested on a pre-L2 base; already fixed at
  `SurrealKycStore.cs:199`.

## Recurring root cause

"Unknown treated as absent": RV-L3-H1/M2, RV-L5-02 and D-8 critic rule 1 all concluded "no money moved"
from an ambiguous failure. Rule for every rail: ambiguous is its own state and parks until chain/Stripe
evidence settles it.

## Fix batch merged (2026-10-17) and bounded re-checks

Merged by the orchestrator (agent git was denied by the permission classifier mid-session; owner chose
orchestrator-run merges with per-command approval): L6 `ab6c600d`, fix-fm11 `afaa46dc`, fix-money `3d1cac67`,
fix-chains `6352da84`, fix-frontend `c94f9945`, fix-security `53c6473c`, fix-identity `75b23986`.
Inventory merge losses RV-X M-1..M-4 restored at merge; gates green on the merged lane trees.

| HIGH | Re-check |
|---|---|
| RV-L1-01 | CLOSED (LOW R-1..R-3 → fix-money2) |
| RV-L3-H1 / M1 / M2 | CLOSED |
| RV-L3-H2 | PARTIAL → owner D-10 (provenance on transfer) → fix-money2 |
| RV-L4-01 / -02 / -03 / -04 | CLOSED (new MEDIUM RV-L4-09 → fix-gates2; LOW RV-L4-08 fixed in fm11) |
| RV-L5-01 / -02 / -06 / -07 / -09, F-M11 | CLOSED (MEDIUM R1 nonce pinning → fix-gates2) |
| RV-L7-1 / -2 / -3 / -4 | CLOSED |
| RV-X X-1..X-5 | CLOSED (new MEDIUM R-2, R-3, LOW R-4 → fix-gates2) |

New from L6 (not in the original review): CRITICAL host-startup DI deadlock, HIGH SurrealForge 1.3.0
`UpdateOnly`/`DeleteOnly` without `Where` (9 prod sites), HIGH reversal lot link, 2 MEDIUM → fix-core2 /
fix-money2. Browser-slice regression (2 files) → fix-frontend2.
