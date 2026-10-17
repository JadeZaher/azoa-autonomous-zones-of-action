---
type: verification-plan
---

# Final integrated verification

Status: planned, not executed. Static reviews and authored regression tests are not passing-test evidence. Complete the four assessment/delivery/review rounds before the integrated sweep, then batch any failures and rerun affected gates as needed.

## Preparation and ordering

1. Freeze authored model/interface changes, inventory tracked and untracked files, and collect independent review findings. Confirm local `.env` remains ignored and only development Stripe credentials are selected.
2. Build `AZOA.WebAPI.csproj` to produce the final model assembly. Run the repository-pinned SurrealForge generator for schemas and flowcharts from that assembly. Inspect generated changes for dropped fields/indexes and unintended schema changes.
3. Build `azoa.sln` with generated artifacts present. Run the unit project, including schema/flowchart golden tests, and integration project against the preserved local SurrealDB on port 8020. Do not reset the database volume.
4. Build/typecheck/test the wallet SDK before frontend typechecking/build because the frontend resolves SDK distribution exports. SDK lint is currently the same command as typecheck, so record that equivalence rather than duplicate the same gate.
5. Run frontend typecheck, lint, tests, build and dependency audit. Capture UTF-8 logs under `.omc/research` and retain exit codes. Classify failures individually; historical baseline failures are not automatically waived.
6. Run authorized development-provider acceptance only after local correctness gates, with suitable account/test fixture setup and explicit test-mode enforcement. No real identity data or live-key financial transactions are needed. Missing Identomat workflows remain in the external ledger; HTTP fixtures must exercise the actual adapter and shared flow.
7. Apply all collected remediation as a batch, independently review it, regenerate artifacts if model changes require it, then rerun failed and affected gates. Record exact results and residual external limits.

## Required behavior evidence

| Area | Evidence required |
| --- | --- |
| Hosted identity | One external create owner; no blind ambiguous retry; callback-before-attachment remains recoverable; expired/stale lease cannot bind or approve; invalid tenant/provider/subject fails closed. |
| AML | Current approval uses its own evidence lineage; older clean evidence cannot replace a current hit; monitor failures retry durably; polling watermark advances only after persistence. |
| KYB | Company/beneficiary/review evidence contract, real optional-field persistence, tenant authority, callback routing, expiry/config drift and public status parity. |
| On-ramp | Issuance/refund mutual exclusion under the exact allocation key; structured pre-effect failure recovery; response-loss idempotency; authoritative payment/refund matching; inbox replay. |
| Off-ramp | Funding transfer distinct from bank Paid; authoritative account-scoped payout state; partial/reordered reversals; cumulative restitution cap; no cash plus returned-unit double benefit. |
| NFT/credentials | Confirmed chain operations and immutable issuance provenance; arbitrary existing asset references cannot authorize platform clawback; pending/replay/crash reconciliation; authenticated current-owner access and disclosure. |
| Wallet/DEX | Chain-specific signature vectors, canonical identities and nonce consumption; actual transaction bytes bound to sender/network/quote/slippage and reviewed provider contracts. |
| Chain/bridge/governance | Each later-round package adds its concrete accepted invariants here before closure; no capability flag substitutes for transaction or authorization evidence. |
| Git | Final reviewed changes committed; main push succeeds; remote topic deletion only after merged ancestry proof; only intended worktree/branch remain; no secrets or omitted source files. |

## Evidence ledger

Additional local protocol gates: compile and test `contracts/ethereum` against its pinned compiler and local EVM, run its local deployment recipe, and exercise the .NET adapter against the resulting verified contract. Solana protocol setup is recorded in `tests/AZOA.WebAPI.IntegrationTests/Fixtures/Solana/AGENTS.md`: a checksum-pinned Agave validator image with the public Metaplex program cloned into a local ledger. Protocol fixtures must execute real instructions; a missing validator is a gate failure, not proof from HTTP fixtures. Builds, deployment and validator execution remain unrun until the integrated sweep.

No gate has been executed for the current combined changes. Populate command, tool/runtime version, timestamp, exit status, result counts and log path during execution. Do not pre-fill success.

## Round 2 acceptance additions

- Wallet authentication: actual Ethereum personal-sign and Algorand/Solana Ed25519 vectors; concurrent challenge cap/consume and canonical wallet binding; explicit network pin refusal; ownership claim revokes both previous password and wallet credentials. Include positive Surreal tests for nullable/unset authentication CAS fields, not only rejection cases.
- Hosted providers: Veriff authenticated session/result correlation and the opted-in AZOA generic protocol. The generic fixture must prove original-key automatic recovery after response loss, exact immutable create bytes, key rotation, terminal evidence contradiction quarantine, and provider-neutral person uniqueness. Provider fixtures are not evidence that a real external deployment has accepted a user.
- Workflows: receipt/effect crash windows for every handler category, durable waiting without retry exhaustion, missing legacy operation evidence, domain-verified operator resolution and once-only successor enqueue. NFT worker must never sign a new generation or participant stage, including concurrent interactive changes.
- Governance: current actor-to-tenant authority at both direct and action boundaries, frozen electorate/close policy, server cutoff and immutable finalization across vote/release/membership races.
- Deployment: execute generated loader against the real registered invocation route; repeated source-action occurrences remain distinct; use current caller authority. Exercise actual AVM create/call/update/delete, Solana ELF upload/finalize/call/upgrade/immutable policy and standalone metadata/ATA/close, and actual .NET Ethereum native/NFT/value adapter journeys against local EVM. Solidity-only tests do not substitute for adapter execution.

Prepare required local protocol services before the integration invocation so missing services do not create an avoidable first sweep failure. Existing SurrealDB stays on port 8020 with its volume preserved. Local validator/chain fixtures use isolated ledgers and loopback ports; port 3000 belongs to another application. The integration run must report skipped protocol cases explicitly and treat missing required protocol coverage as unresolved.
