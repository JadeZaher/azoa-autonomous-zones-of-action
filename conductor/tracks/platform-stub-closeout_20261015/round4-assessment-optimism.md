---
type: assessment
round: 4
stance: optimism-challenge
---

# Round 4 assessment: optimism and sycophancy challenge

The round-3 records use "source-complete", "APPROVED", "verified sound" and "CLOSED". Every one of
those verdicts comes from reading code. Nothing in the 984 changed production `.cs` files (+59.5k / −22.1k
lines since `bb512cf`) has compiled, and none of the 427 changed test files has run. This assessment
sorts which claims are supported, which would collapse, and which deferrals can be done now.

## Evidence boundary

- **Read:** `plan.md`, `deferred.md`, `round3-closeout.md`, `round3-join-ledger.md`, the five
  `round3-lane-*-resume.md`, `round3-transaction-scope-review.md`, `round3-assessment-optimism.md`, the
  compile list `.omc/research/round3-join-compile-errors-2026-10-16.txt`, and the `.omc/research/identomat-*`
  captures.
- **Analysed:** all 247 compile errors, grouped by cause. Each missing symbol was looked up in the AZOA tree
  and in the SurrealForge repo (tag `v1.2.0` and the 1.2.1 candidate `3aacd25`).
- **Executed (environment facts only, no AZOA code built or run):**
  - P1: read-only queries against `azoa-dev-surrealdb` (SurrealDB 3.1.4, :8020).
  - P2: write probes against a throwaway in-memory 3.1.4 container `r4-optimism-probe`, removed afterwards.
  - P3: Ganache 7.9.2 vs Hardhat 2.26.3 JSON-RPC probe in the scratchpad, deleted afterwards.
  - P4: SurrealForge source reading.
- **Not done:** no build, no test, no vendor call. Nothing was written except this file.

## Headline: what one hour of probes changed

| # | Finding | Evidence | Consequence | Cheapest fix |
|---|---|---|---|---|
| F5 | **New tree-wide fail-closed class: collateral error slots.** When a statement in a `BEGIN…COMMIT` batch fails, SurrealDB 3.1.4 rewrites every *earlier successful* statement to `ERR "The query was not executed due to a failed transaction"`. | P1 `BEGIN; 'ok'; LET $y=2; THROW 'boom'; …` returned slots 1–2 as `NotExecuted`, the THROW at slot 3. P2: a real unique violation behind a `LET` landed at slot 2, with slot 1 rewritten. | `SurrealResponse.EnsureAllOk()` throws on the **first** ERR (`SurrealResponse.cs:102-113`, same in `v1.2.0`), so `SurrealStatementException.Message` quotes the placeholder. All 149 `exception.Message.Contains(<marker>)` checks and all 81 `IsUniqueViolation(exception.Message)` checks miss whenever any successful statement precedes the failure. Every fence puts `LET` captures before its `THROW` (e.g. `SigningAuthorityFence.cs:52-65`). Typed 409s, "losers re-read the winner" (transaction-scope rule 4) and duplicate replay therefore never trigger; losers surface as unhandled exceptions. Safety holds because the transaction rolls back. Liveness and HTTP semantics do not. | SurrealForge already skips collateral slots in its transaction commit path (`SurrealDbNetConnection.cs:1354-1366`, `IsCollateralTransactionError` at `:1391`). Apply the same culprit selection in `EnsureAllOk` (or add `EnsureAllOkBlameCulprit`) **before publishing 1.2.1**. AZOA builds raw `BEGIN` batches in 81 files and never uses the transaction object, so the existing fix never reaches it. |
| F6 | `IsUniqueViolation` is `private static` in SurrealForge 1.2.0 **and in the 1.2.1 candidate** (`SurrealIdempotencyLedger.cs:344` at `3aacd25`). | P4 | The 81 classifier errors (33% of the list) survive the planned 1.2.1 bump, so "publish 1.2.1, then bump" does not unblock the build. | Add a public classifier in 1.2.1, ideally on the structured `kind` that 3.1.4 now returns (`AlreadyExists`, `Thrown`, `Internal`; seen in P1/P2) rather than substring text. |
| F7 | **Ganache 7.9.2 rejects every EIP-1898 parameter.** | P3: `finalized`/`safe` work; `{blockHash}` and `{blockHash, requireCanonical}` fail on `eth_getBalance`, `eth_call`, `eth_getCode`, `eth_getStorageAt` and `eth_getTransactionCount` with `Cannot wrap a "object" as a json-rpc type`. Hardhat 2.26.3: all pass, and an unknown hash is rejected. Ganache's uWS native binary also fails to load on Node 24 and falls back. | Every Ethereum DEX and WTT protocol fixture fails in the sweep (lane C/A shared risk, ledger §Round-4 carry-forward). The ledger calls this "unverified"; it is now known false. | Move `Fixtures/Ethereum/chain.mjs` and `dex-chain.mjs` to the Hardhat network. |
| F8 | The stale unique index `ethereum_preparation_nonce` **will not** be dropped on a kept database. | P4: `docker-entrypoint.sh:98-106` runs `surrealforge up` without `--allow-destructive`. `ReconcileRunner.cs:197-201` marks `REMOVE INDEX` destructive and skips it. | Deploy-blocking item answered: Railway refills fail closed as "busy". | Add a migration `REMOVE INDEX IF EXISTS ethereum_preparation_nonce ON TABLE ethereum_preparation;` under `Persistence/SurrealDb/Migrations`. |
| F9 | **D2 is broader than recorded.** A `record<t>` field rejects the bare string `'a'` **and** the string `'t:a'`. Only a real record value coerces, and `record = 't:a'` (string) compares `false`. | P2 | Any raw `WithParam` binding of a string id into, or compared against, a `record<>` column fails closed. D2 (`StripeWebhookInbox.cs:467-468`, still unfixed) is one instance. Typed writers that promote `table:id` strings are probably fine. | Inventory raw bindings against `[References]` fields; bind with `type::record(...)` / `SurrealRecordParam`. |
| ✓ | **Confirmed sound:** BEGIN, COMMIT and LET each occupy a result slot on 3.1.4. KYC callers' `GetValues(2)` after `BEGIN; UPSERT; mutation; COMMIT` and the `Providers/Stores/Surreal/AGENTS.md:461` convention are correct. An early `RETURN` inside a transaction **commits** but silently skips every later statement and drops it from the result array. All 12 `RETURN $x` sites sit directly before `COMMIT`, so they are safe. | P1, P2 | Two load-bearing assumptions now hold on evidence. | Add "no statement after a top-level RETURN inside BEGIN…COMMIT" to the transaction-scope gate. |

F5 is the fifth fail-closed class. The round-3 closeout's "four classes fixed tree-wide" were all found by
reading; this one was invisible to every round-3 static review and fell out of a one-line query. That is
the measured miss rate of static review on runtime semantics. Treat every remaining "verified sound" as a
hypothesis.

## 1. Claims audit

| # | Claim (where) | Evidence that exists | Evidence the goal requires | What collapses | Honest label |
|---|---|---|---|---|---|
| 1 | **"KYC/KYB set up", Identomat first** (`deferred.md:15`; round-3 optimism §reuse 1) | Source adapters; vendor captures; lane V KYB/custodial-KYC HTTP flows authored but never run on this tree; KYB/tenant-KYC MSW slices ran against a mocked API | Compile; real-DB persistence; HTTP flows green; at least one live Identomat session per flow, including an abandoned and a rejected one | The KYC persistence layer does not compile: `SurrealHostedProviderAttemptStore.cs` has 8 errors (6 builder, 2 classifier); classifier errors also in `SurrealHostedSessionStore`, `SurrealKycIdentityStore`, `SurrealKycScreeningStore`, `SurrealKybVerificationStore`. D3 throws in KYC identity ×3, screening and control until 1.2.1. F5 defeats KYC fence and duplicate classification. | Authored, uncompiled, vendor-unexercised |
| 2 | **"Stripe: absolutely no stubs"** (goal); "durable refund/replay/reconciliation" (round-3 optimism) | Test-key `/v1/account` read; CLI listen secret; Stripe replay/refusal HTTP flows ran green **on `bb512cf` with local D2/D3 patches**, not on this code | Test-mode checkout → signed webhook → credit; refund; dispute → reversal → clawback; Connect transfer; bank payout success and failure | **D1** (`FiatRailProvider.cs:610-617`, `PayoutAccountService.cs:246`): compile error, and attach never claims creation. **D2** still writes bare hex (checked today). **D8** (`FiatRailProvider.cs:569,587`) still recomputes `expires_at` under a stable idempotency key. **New:** `OffRampService.cs:452` reads an undeclared `requestedQuantity` (half-applied edit in cash-out step 1; intent unknown). Classifier errors in `StripeWebhookInbox.cs:248`, `OnRampLotLedger`, `FiatPayoutLedger`. Two clawback MEDIUMs still open. | **Not done** |
| 3 | **"Exactly-once"** (bridge, DEX, Hold, NFT; lane verdicts) | Static traces; real-DB fixtures authored but unrun | Interleaving tests against real SurrealDB that assert the winner rows | Duplicate detection relies on a private API (F6) and the wrong error slot (F5). The loser-resolves-to-winner path is dead code at runtime. | At-most-once by static argument; exactly-once unproven |
| 4 | **Lane N APPROVED / CLOSED** | Two static review cycles | NFT protocol fixtures on three chains | `NftLifecycleOperationProjection.cs:32` uses a nonexistent `OperationStatus.Confirmed` (projection status needs a decision). Solana metadata/NFT files have 12 errors. **`SolanaProvider.NativeAccounts.cs:81` passes arguments 3 and 4 swapped**: a real argument-order bug the compiler caught. F5 hits `SurrealNftLifecycleStore.cs:69,88,93,98,151`. | Approved at source review |
| 5 | **Lane A APPROVED / CLOSED** | Two static cycles; guardian proof verification traced | Six-route protocol fixtures | Every WTT quote/proof entry on **all three chains** fails to compile (`SigningContext` null, 6 sites). The Algorand WTT observation reads a nonexistent `ApplicationCallTransaction.ApplicationId` (`WttObservation.cs:50`, `WttTargetObservation.cs:50`), so the parser was written against a guessed SDK shape. Solana WTT instructions assign init-only `Data` (3 sites). Ethereum WTT reads use EIP-1898 (F7). 7 WTT stores use the private classifier. | Approved at source review; protocol-unverified |
| 6 | **Lane B APPROVED / CLOSED** | Two static cycles; money arithmetic traced | Real-DB reversal and clawback runs; Stripe dispute drill | `HoldReleaseWorkflowFence.cs` (`SurrealLink` ×4); 18 Hold store files use the private classifier; `SurrealHoldCommandStore.Mandate.cs:34` is missing a type; F5 hits every Hold marker; Algorand exchange files have Encoder ambiguity; two clawback MEDIUMs open. The exchange module is unadvertised, so the sweep can only prove refusals. | Approved at source review |
| 7 | **Lane C APPROVED / CLOSED; DEX capability matrix** | Two static cycles | Activated modules plus protocol fixtures | `SolanaProvider.DexQuote.cs` has 14 errors, including 9 unassigned locals (`rentCap`, `networkCap`, `cleanupCap`, `platformFee`, `gross`): **the Jupiter fee-cap arithmetic has never type-checked**. `TinymanSwapReceipt.cs:109` reads the nonexistent `ApplicationId` (Tinyman settlement grammar guessed). `JupiterCleanupWire.cs:32` has a `CloseAccount` signature mismatch. Ethereum fixtures hit F7. The modules are undeclared. | Approved at source review. Any DEX "closed" claim is empty until activation. |
| 8 | **Lane V "harness verified"** | MSW slices 8/8 jsdom and 8/8 Chromium; `npm test` 179 pass / 4 fail; .NET egress probe ran in a container; Stripe HTTP replay ran at `bb512cf`; e2e shell spec on `next dev` | Backend HTTP flows and Playwright journeys green on this tree; CI jobs | No backend HTTP flow and none of the four journeys has ever run against this code | Frontend harness smoke-verified; backend and E2E layers authored only |
| 9 | **"Four fail-closed classes fixed tree-wide"** (closeout §bug classes) | Static | Real-DB fixture per class | F5 (above) was missed by the same method | Partial |
| 10 | **"SurrealForge 1.2.1 READY TO PUBLISH"** (ledger) | Its own suites: unit 1162/1162, integration 41/41. This is real executed evidence. | AZOA builds and runs against it | Lacks F5 culprit selection and the public classifier (F6). Has never been consumed by an AZOA build. | Ready for its own scope, **not** for AZOA. Hold the publish. |
| 11 | **Wallet auth: Ethereum + Solana verification** | `Services/Signing/WalletSignatureVerifier.cs`: Ed25519 plus EIP-191 recovery with a low-s check | Browser-wallet encoding vectors per chain | `Program.cs:1162-1163` registers a nonexistent `Ed25519SignatureVerifier` (leftover from a rename) | Authored, unverified |
| 12 | **Solana input validation** in DEX, native artifact and WTT paths | 17 call sites | — | `ValidateAddressFormat` exists only as Algorand's `private static` (`AlgorandProvider.cs:388`). Solana has only `TryParsePublicKey` (`SolanaProvider.cs:1035`). The validation intent is present; the Solana implementation was never written. The wrapper is one line, but each site needs a decision on whether an on-curve owner key is required or an off-curve PDA account is acceptable. | Intent present, implementation absent |
| 13 | **Identomat recovery: operator path implemented** (`deferred.md:30-38`) | Static | Fixture proving a callback for an unattached session id is refused; live lost-response drill | Unexercised | Authored |
| 14 | **Plan vs closeout** | `plan.md:28-30` leaves "Implement Identomat / Stripe gaps / close remaining stubs" unchecked; `round3-closeout.md:3` says "closed at source" | One vocabulary | Readers will take "closed" to mean "works" | Use the labels in §6 |

## 2. What the 247 errors hide

| Cluster | Count | Kind | Fix | Semantic risk behind it |
|---|---|---|---|---|
| `Helpers/Encoding.cs:10` deliberately shadows `System.Text.Encoding`, plus `Algorand.Utils.Encoder` vs `System.Text.Encoder` | **76** (67 CS0104, 3 `UTF8`, 5 Nethereum `ABIValue`, 1 `StringContent`) | Cascade | Rename one class and add one alias | None. The 5 Nethereum "ABIType" errors are cascades (`EthereumPayloadPermit.cs:35-40`), not API misuse. |
| Private `SurrealIdempotencyLedger.IsUniqueViolation` | **81** | Private library API | Public classifier (F6) | F5: even once public, it receives the placeholder text |
| Typed builder passed where `SurrealQuery` is expected | 17 | Mechanical | `.Build()` | Low |
| `SurrealLink` missing `using` | 11 | Mechanical | `using SurrealForge.Client;` | F9 comparisons at those sites |
| Solana `ValidateAddressFormat` | 17 | Missing helper | Wrapper over `TryParsePublicKey` | On-curve decision per site |
| CS0165 unassigned locals | 12 | Control flow never type-checked | Restructure | Jupiter quote fee caps (`SolanaProvider.DexQuote.cs:45-117`) |
| `SigningContext` passed as null | 6 | Signature drift | Supply the intended context | Which signing context a WTT quote binds to |
| Guessed Algorand SDK members (`ApplicationId` ×4, `AssetCloseTo`) | 5 | API guess | Real member names | Parse correctness of WTT observation, Tinyman receipt and value acceptance. Only protocol fixtures can prove it. |
| Solnet drift (init-only `Data` ×4, `CloseAccount`) | 5 | API version | Initialisers / extra parameter | Fixtures |
| D1 attach | 2 | Logic gap | Claim creation, then attach | Checkout and payout-account onboarding |
| Swapped arguments | 2 | **Real bug** | Swap them | Solana native metadata account preparation |
| CS1628, enum/vocabulary drift, misc | 13 | Mixed | — | `OperationStatus.Confirmed`, `ActionNodeType.Gate`, `requestedQuantity` need intent decisions |

About 185 of the 247 (75%) fall to four moves: rename `Encoding`, call `.Build()`, add the `SurrealLink`
using, and expose the classifier. Roughly 40 are guesses against three chain SDKs (Algorand2, Solnet,
Nethereum), and about 10 need someone who knows the intent. Behind this list sit:

- the SRDB0001 layer (~35 known sites; the analyzer stage never ran);
- IDE0005 unused-using errors;
- binding errors currently masked by the cascades;
- both test projects, never compiled since `3d08ace`;
- SDK and frontend typecheck (`FiatPayoutView`, swap re-exports; join-ledger §Final-sweep prerequisites 3).

## 3. Deferral challenges (`deferred.md`)

### IDENTOMAT-WORKFLOWS: an owner action, not an external blocker

What is true: no configuration IDs exist, and both adapters refuse without one
(`IdentomatKycProviderService.cs:136`, `IdentomatKybProvider.cs:37`).

Challenges:

1. **The API documents a default session that needs only the company key.** See
   `identomat-access-setup-2026-10-15.md`, doc lines L65-L70 ("Start default session: `{company_key}`") and
   L94-L98 (result). The begin → result → callback contract can be exercised live today with the key already
   in `.env`, for KYC. KYB needs a configuration with KYB steps.
2. **Creating both configurations takes the owner about 15 minutes.** Exact steps:
   1. Dashboard → No-code builder → new configuration "AZOA KYC". Settings tab: a unique Purpose and an
      https return URL. Steps tab: document plus liveness, and screening if AML is wanted. Save and copy the
      config ID.
   2. New configuration "AZOA KYB" → KYB steps tab → save and copy the config ID.
   3. **Company settings** → Callback URL = the tunnel URL of the shared Identomat callback route; Callback API
      key = the existing `Kyc__Identomat__CallbackApiKey` from `.env`. One company URL serves both flows,
      because `sessionType: "kyb"` distinguishes them (`identomat-callback-contract-2026-10-15.md` L140-L153).
   4. Put both IDs in `.env` (`Kyc__Identomat__ConfigId`, `Kyb__ConfigId`) and make `Kyb__ReturnUrl` https,
      which also closes D7.
   5. Run a tunnel (cloudflared or ngrok) for the drill only. Callbacks are triggers and `/result` is the
      authority, so reconciliation polling still works without the tunnel.
3. **Drill, about 8 sessions.** The captures record no sandbox (`identomat-kyc-api-2026-08-20.md:79`), so
   these are live and probably billable:
   - KYC: approve with the owner's own document; abandon (an incomplete session must not open the gate);
     manual reject from the dashboard (REJECTED callback); replay a captured callback.
   - KYB: approve with the owner's own company; manual reject.
   - One lost-response drill: kill the API after `/begin`.
   - D4 confirmation: the standard configuration returns `{id, url}`.

Verdict: rename the entry **OWNER-ACTION (pending)**. Total cost is about 30 minutes of owner time plus about
8 sessions. Keep it open until the drill evidence lands.

### IDENTOMAT-RECOVERY: the vendor gap is real, the deferral is not needed

- **Re-reading the captures confirms the gap.** The complete endpoint list:
  - `begin` (legacy and new) and `result` with its seven media endpoints;
  - `delete`, `export-session-as-pdf`, `get-session-main-document-face-photo`,
    `get-session-statistical-summary`, `get-visual-markers`, `list-session-activities`;
  - four screening endpoints.

  `list-session-activities` (`identomat-recovery-lookup-2026-10-15.md` L1433-L1508) requires `sessionId`, as
  does every other session endpoint. Nothing looks up a session by `clientUserId`.
- **The deferral protects an invariant the system does not need.** The deferral assumes "exactly one vendor
  session per attempt". The safety property is narrower: "evidence is accepted only from the session
  attached to the attempt". An orphan from a lost `/begin` response is inert:
  - in standard mode its id *is* its token (`url: ?session_token=<id>`; lane V H1), and nobody received it;
  - authenticated callback routing ignores session ids that are not attached.
- **Do it now:**
  - After the bounded wait, mark the possibly-sent admission `OrphanPossible` and allow a fresh attempt
    automatically, budgeted through `IdentomatRateBudget` (for example one per subject per 24 h).
  - If the original response arrives after supersession, delete that session through the documented
    `/external-api/delete/` endpoint (`identomat-pending-result-find-2026-10-15.md` L914-L944) and audit it.
  - Add a fixture: a callback for an unknown or superseded session id mutates nothing.
- **Residual:** possible billing for an unused session. Ask Identomat whether creation or completion is
  billed. That is a cost note, not a capability deferral.

Verdict: closeable in round 4 without the vendor. Vendor idempotency becomes optional hardening.

### Unrecorded deferrals

The plan requires every deferral to survive challenge in `deferred.md` (`plan.md:18`). The carry-forward
lists have become a way to defer without that challenge. The items below are deferrals in all but name and
are absent from `deferred.md`:

- Veriff and generic-hosted live verification (`plan.md:58`). This needs a Veriff account, so it is the only
  genuinely external item besides Identomat.
- Ethereum/Solana value-custody parity.
- WTT six-route fixtures.
- Tinyman and Jupiter fixtures.
- Recipient-stage Solana costs.
- Legacy Wormhole mode retirement.
- Operator console fee control.
- Exchange module advertisement.
- WTT stage-key readiness gate.
- Signature-verified External wallets.

Each must end round 4 as **done**, as a `deferred.md` entry with external evidence, or as an explicit owner
scope-out.

## 4. Carry-forward challenges

Size key: S ≤ half a day, M ≈ 1 day, L ≥ 2 days. None of these needs an external party except the two noted.

| Item | External? | Do-it-now path | Size |
|---|---|---|---|
| **Clawback MEDIUMs** ("must-fix before real cards") | No | Rung 0 subtracts uncompleted reservations and credits only on completion; cap the pending finish at `attributable`; use an attempt-suffixed claim key after a definitive failure (ledger §Round-4 carry-forward). Then run the dispute test card `4000000000000259` drill. "Before real cards" is the wrong bar: they are defects on the goal's Stripe path. | S |
| **Stale nonce index** | No | Answered by F8: ship the explicit migration | S |
| **Ganache EIP-1898 / `finalized`** | No | Answered by F7: switch both fixtures to Hardhat | S–M |
| **DEX activation** | No | Declare `IDexTransactionModule` on all three providers now. Gate networks by config: test config on, production config off until fixtures pass. Otherwise the sweep proves only `Unavailable`. | S |
| **Exchange module advertisement** | No | Same reasoning. Lane B's final review already returned APPROVE WITH CARRY-FORWARD. | S |
| **Tinyman LocalNet fixture** ("compiled programs not captured") | No | Read the deployed validator app's approval/clear bytes from testnet or mainnet algod `GET /v2/applications/{id}`; record app id, round and sha256 as the pin. Create the app on LocalNet. Take the pool logic-sig template from the pinned Tinyman SDK release and substitute the LocalNet app id. | M |
| **Jupiter/Whirlpool local fixture** | No | Reuse the technique the Solana fixture already uses for Metaplex (`Fixtures/Solana/AGENTS.md`, `--clone-upgradeable-program`): clone Whirlpool and Jupiter v6 plus one pool's accounts (pool, tick arrays, vaults, mints, oracle) from mainnet-beta. Build a single-hop route from the captured IDL. Alternative: surfpool 1.6.1 (`round3-surfpool-head.md`), a lazy mainnet fork, less hermetic. | M |
| **WTT six-route fixtures** | No | EVM: deploy pinned core and token bridge on Hardhat with one local guardian key and sign VAAs locally. Solana: load the pinned program binaries at their canonical ids into a fresh validator (`--bpf-program`, binaries from `solana program dump`), then `initialize` with the local guardian set (a fresh ledger has no config account). Algorand: deploy the pinned TEAL on LocalNet with the same guardian set. Plan rule: size is not a deferral. | L |
| **SDK DEX adapter retirement** | No; owner already decided | Delete the adapters, fix exports, update `AGENTS.md:86` | S |
| **Signature-verified External wallets** | No | `WalletSignatureVerifier` already covers all three chains. Persist an ownership-proof flag at link time and accept it in `OwnCustodialRecipientAsync`. | S |
| **RPC URL in chain info** | No | `EthereumProvider.cs:814`, `SolanaProvider.cs:601` (per ledger) | S |
| **Cross-tenant and transaction-scope gates** | No | Source-scanning architecture tests. Add the RETURN rule from the headline table. | S each |
| **D9: inject HttpClient into chain providers** | No | `AlgorandProvider.cs:377`, `EthereumProvider.cs:182`, `SolanaProvider.cs:161`. Prerequisite for fake-RPC flows. | S |
| **SRDB0001 layer** | No | One trusted identifier-template helper plus migration of all sites (ledger plan) | S |
| **SurrealForge 1.2.1** | Owner publishes | Hold the publish. Add F5 culprit selection and the public classifier; re-run its suites; publish once; bump `AZOA.WebAPI.csproj:109-113` and both test csprojs. Consuming the local `artifacts/1.2.1/` pack before publishing proves AZOA compiles against it. | S |
| **Recipient-stage Solana costs (G2)** | No | Lane C contract review first, then build | M |
| **Legacy Wormhole mode + action Bridge/Back cutover** | No | Register the `wtt` resolver | M |
| **Absence proof for exposed-but-unsigned participant messages** | No | Adapt the reviewed Jupiter observer | M |
| **WTT stage-key readiness gate** | Owner decision only | One choice: block startup or degrade | S once decided |
| **Ethereum/Solana value-custody parity** | No (needs a new settlement contract) | Owner scope decision. Build it in round 4, or record a scope-out. Truthfully undeclared capability is not a stub. | L |
| **Operator console fee control** | No | BFF route plus UI, or owner scope-out | M |
| **Pruning** (`swap_state`, `RefundNodeHandler`, Uniswap lockfile audit) | No | — | S |
| **D4, D5, D6, D7** | D4 needs the live drill | D5–D7 are small fixes | S |
| **New: F5** | No | Library fix. Then add real-DB fixtures with one marker case and one duplicate case per store family. | S + fixtures |
| **New: F9 / D2 class** | No | Inventory raw string bindings against `record<>` fields | S–M |
| **New: `OffRampService.cs:452`** | No | Recover the intent from the cash-out reversal lane, then fix | S |
| **Veriff / generic-hosted live** | **Yes**: needs a Veriff account | Owner supplies Veriff test credentials, or this becomes a real `deferred.md` entry | — |

## 5. Verification sequencing

**Is one sweep realistic?** Not literally. It can still fit the policy, under two conditions:

- compile convergence and environment probes are classed as fix work, not test runs;
- "the sweep" means one full collection pass plus bounded batch re-runs of the affected gates, which
  `plan.md:20` already allows.

Proposed sequence:

- **S0. Environment facts (< 1 h; partly done here).** Remaining probes:
  - a reserved-word `user` record parameter through the 1.2.1 pack on 3.1.4;
  - Agave clone of the Whirlpool, Jupiter and Wormhole program ids;
  - the Identomat default session (one live session, which also settles D4).
- **S1. Library.** SurrealForge 1.2.1 with F5 and F6 included → its own suites → publish → bump.
- **S2. Fix batch A (no gates).** Every S item in §4, plus:
  - D1, D2, D8, D5–D7 and the clawback fixes;
  - activation declarations;
  - the Hardhat switch and the stale-index migration;
  - the Identomat orphan policy.
- **S3. Compile convergence (a fix batch).** App → test projects → SDK → frontend typecheck.
  - Expected app waves: 3. W1 is the 247; W2 is unmasked binding errors plus SRDB0001 plus IDE0005; W3 is
    stragglers.
  - Expected test-project waves: 2–3. 427 test files were written against guessed signatures, with Moq arity
    churn.
  - Expected SDK/frontend waves: 1–2.
  - Total: **6–8 compile iterations**. Each takes minutes. Send the roughly 50 semantic errors (§2) to the
    owning lane, not to a build fixer.
- **S4. Environment.** Schema regeneration → `dev-up -Reset` → SDK dist build → Identomat config IDs in
  `.env` → Stripe CLI listener.
- **S5. The integrated sweep.**
  - .NET unit and integration tests (real SurrealDB) and HTTP flows.
  - Protocol fixtures: Hardhat, Agave, AlgoKit LocalNet.
  - SDK vitest; frontend unit, slices and browser; Playwright e2e.
  - Lint, typecheck, `npm audit`, schema goldens.
  - The Stripe test-mode drill: checkout `4242…`, refund, dispute `…0259`, Connect transfer, bank payout
    `000123456789` (success) and `000111111116` (failure).
  - The Identomat live drill.
  - Every failure goes into one ledger, classified by root cause.
- **S6. Failure batches.** Fix by class and re-run only the affected gates. Expect **3–4 batches**:
  1. The first is large and shaped by classes: schema drift, D3/F9-type coercion, positional slots, fixture
     chain setup.
  2. The second exposes failures the first masked (fences that never ran because an earlier statement
     threw).
  3. The third catches interleaving and journey-level issues.
  4. The fourth is contingency.

  **Stop rule:** if batch 3 is not green on the KYC, Stripe and exactly-once gates, stop and re-plan rather
  than loop.

Realistic total: **9–12 fix waves** (6–8 compile, 3–4 runtime), over about 1 day of compile convergence plus
2–4 days of sweep and batches. The WTT six-route fixture lane comes on top if it runs.

## 6. Scope honesty at the end of round 4

Vocabulary for the closing report:

- **Done** = executed evidence.
- **Built** = compiled, with unit coverage only.
- **Authored** = not compiled.
- **Approved at source review** is never written as "closed".

| Capability | Done: claimable only with this evidence | Built but unverified (the realistic label if R4 is not perfect) | Not done (must be stated) |
|---|---|---|---|
| Identomat KYC | Live dev drill: approve, abandon, reject, callback replay; gate refusal for incomplete or rejected evidence | Fakes and HTTP flows green, no live session | — |
| Identomat KYB | Live approve and reject with the owner's company | Same | — |
| Identomat lost-create recovery | Orphan policy, delete, fixtures | — | Vendor idempotent create (optional hardening) |
| Veriff / generic hosted | — | Adapters against documented contracts | Live verification, unless credentials arrive (external) |
| Stripe on-ramp, refund, reversal/clawback | Test-mode drill with CLI-forwarded signed events; clawback crash fixtures | — | Live mode (out of scope: dev only) |
| Stripe Connect transfer and bank payout | Test connected account; payout success and failure | — | Deployed webhook endpoint and secrets (Railway) |
| Wallet auth (Algorand/Ethereum/Solana) | Browser-encoding vectors per chain | — | — |
| NFT lifecycle | Hardhat, Agave plus Metaplex clone, LocalNet fixtures | Whichever chain's fixture does not run | — |
| DEX | Ethereum (Hardhat Uniswap) once activated | Tinyman and Jupiter, unless their local fixtures are built | — |
| WTT bridge | — | Flow and store tests green; production `Enabled=false` | Six-route protocol proof (unless the L lane runs); legacy Wormhole retirement; action Bridge/Back |
| Hold / exchange | Algorand, once advertised and fixtures run | Otherwise built | Ethereum/Solana custody parity (owner scope decision) |
| Recipient-stage Solana costs | — | — | Not done unless built in R4 |
| Operator console fee control | — | — | Not done, or scope-out |
| Platform hygiene (SDK adapter retirement, RPC URL leak, pruning, architecture gates, SRDB0001) | All are S items and should land | — | — |
| Release | — | — | Railway secrets, Identomat company callback for the deployed environment, remote push and merge to main (`plan.md:33,39`) |

Achievable truthful headline for round 4: **"Identomat KYC/KYB and Stripe (test mode) done with executed
evidence; chain capabilities built with local protocol proof for Ethereum and Solana NFT and Ethereum DEX;
WTT and non-EVM DEX built but protocol-unverified; three named scope decisions open."**

Anything stronger needs the S0–S6 sequence to finish, not another static review.
