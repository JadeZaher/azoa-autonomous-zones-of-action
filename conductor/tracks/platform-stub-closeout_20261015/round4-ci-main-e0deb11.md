---
type: evidence
round: 4
source: GitHub Actions CI run 39051444396 on main @ e0deb11 (2026-10-17 10:58 MDT)
---

# First CI run on the round-4 tree (main @ `e0deb11`)

Owner asked for merge-to-main + push after W0 (2026-10-17). This is the **first execution** of tests,
schema gates and frontend gates on this tree; everything before was compile-only. Railway did **not**
deploy (its services wait for green CI) — production stays on `c6c55dd`. Raw failed-job log:
`.omc/research/ci-main-e0deb11-2026-10-17.log` (gitignored, local).

| Job | Result | First evidence | Owner |
|---|---|---|---|
| .NET build + tests | ❌ | unit **4713 pass / 204 fail** of 4917; by area: Persistence 84, Actions 32, Signing 23, Managers 19 (Dex), Fiat 9, Services 8, Governance 8, Kyc 4, Auth 4, Controllers 3, Conformance 3, Users 2 | sweep batch 1 (classified by root cause); lanes fix in scope as they go |
| Schema job (apply + converge) | ❌ | migration `20260821_120000__widen_operation_amount_and_stamp_value.surql` → HTTP 400 on SurrealDB 3.1.4 | **W1 schema lane** (sent) |
| E2E journeys | ❌ | `azoa-e2e-api` exited (3) at startup — likely the same schema apply | W1, then L6 |
| SurrealDB perf budgets | ❌ | `WalletGetById`, `SagaSteps_DueScan`, `BridgeTxInsert` over ceiling (likely missing indexes / schema) | W1 index audit, then L6 |
| SDK build + vitest | ❌ | `tests/api/client.wtt.test.ts` 2 fail, `src/api/nft-lifecycle.test.ts` 3 fail | L7 |
| Frontend audit + lint + typecheck + vitest + build | ❌ | exit 1 (detail in log) | L7 |
| Secret scan (gitleaks) | ❌ | 80 findings across full history; the 8 in the pushed range `c6c55dd..e0deb11` are all **false positives** — published test vectors (e.g. Mastering Ethereum worked-example key), test JWT in a malicious-input fixture, audit prose. No real credential (root `.env` value scan: no hits) | **L6** (owns `ci.yml`): add a reviewed `.gitleaks.toml` allowlist for test vectors/fixtures, never a blanket path ignore of `src` |
| Frontend slices (Vitest browser) | ✅ | | |
| Railway blueprint contract | ✅ | | |

**Rule for W2 lanes:** a failing test in your owned area is in scope — fix production or the test per
the flow-tests rules, never weaken an exactly-once assertion. Tests remain run-once in the sweep; lanes
may run *their own* failing unit tests by filter to confirm a fix (owner's CI evidence supersedes the
compile-only default for already-failing tests).
