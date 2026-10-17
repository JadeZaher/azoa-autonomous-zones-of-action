---
type: handoff
---
# Round 2 lane C ? authored source, runtime verification outstanding

## Source delivered

- `Models/Deployments`, `Interfaces/Managers/IDeploymentService`, `Interfaces/INativeOperationModule`, `Interfaces/Stores/IDeploymentStore`, deployment persistence POCOs/stores: immutable artifacts, quotes, action-fenced admission, preparation/submission authorization, exact-wire recovery, scoped discovery and request tombstones.
- `Services/Deployments`: executable immutable runtime artifact/compiler, caller invocation and exact replay, native orchestration, economics and stage receipts, action recovery resolver and worker.
- `Core/Surreal/DeploymentAuthorityFence`, `Models/Deployments/DeploymentAuthorityWitness`: atomic root/nested bundle and full Holon authority witnesses, integrated by lane B into workflow admission. Root immutable deployed versions survive later generation; nested references remain explicitly version pinned.
- `Helpers/Deployments`: stable IDs, canonical native authorization, token-aware predicate binding rewrite and guaranteed-output analysis. Ordered occurrences use actual completion boundaries, not synthetic success or unconditional leaf joins.
- Native provider partials under Ethereum/Algorand/Solana: actual typed deployment/call wires, quote and custody boundaries, durable pre-sign claims, canonical reconstruction, authoritative chain identity and execution-position code evidence. Solana account artifacts implement metadata/ATA/close operations. Uploaded artifact provenance is caller declared unless independently established.
- Bundle/series managers/controllers and SDK deployment types/methods: explicit target and request key, pending receipts and status projection. Frontend deployment console exposes quote disclosure, exact saved retry, server discovery, continuation/cancellation and immutable runtime invocation.
- Old synchronous provider deployment/call mutation wrappers removed. `IReadOnlyContractModule` preserves actual EVM read-only calls. `BlockchainOperationManager.Native.cs` routes typed native requests through the journal and refreshes the operation projection from authoritative receipts. Action effects must use frozen domain admission; generic manager deliberately refuses `ActionAuthority` rather than bypass its fence.

## Focused authored regressions

All remain UNRUN by the session test policy. Core slices include DeploymentActionAdmissionTests, DeploymentRequestRecoveryTests, DeploymentArtifactCompilerTests, PredicateBindingRewriterTests, EthereumNativeOperationTests, AlgorandNativeOperationTests, AlgorandNativeHistoryTests, SolanaNativeOperationTests, SurrealDeploymentAuthorityTests, SurrealDeploymentResourceFenceTests and SurrealNativePreparationStoreTests. SDK client.deployments tests cover exact lost-response retry and typed old-route parity; frontend deployment-console tests cover saved retry/key cancellation and stale account responses. New BlockchainNativeOperationTests covers Submitted remaining nonterminal, denied foreign-owner read, and authoritative Confirmed projection without another start. Old fake synchronous Deploy/Call manager tests were replaced by that durable-boundary coverage.

Real AVM/Solana protocol fixture authoring was delegated to lane A and independently inspected by C; actual .NET EVM protocol fixture is owned by root. These are authored protocol gates, not passing integration evidence.

## Bounded review evidence

Root separately reviewed and source-closed the preparation authorization boundary, custody recheck, scoped paging, persisted action quote/fence, native EVM slice, AVM direct update history, runtime witness/replay and compiler occurrence/lexer corrections. Full production readiness is not claimed. Lane C cannot approve its own source. Local diff-check for the generic manager slice found no whitespace errors; no compile/test/typecheck/schema/container gate ran.

## REQUIRED Round 3 work (not external deferrals)

1. Solana partial loader failure cleanup: failed later stages may retain funded buffer rent; current terminal Failed handling is not a complete recovery journey. Authoritative original generation outcome must precede exact buffer close, with bound authority/refund destination and observed returned amount. Preserve already deployed/upgraded truth if authority removal fails. Original fee-cap exhaustion needs an explicitly separately quoted linked recovery operation, never silently larger caps or ambiguous fresh generations.
2. AVM foreign application/asset IDs need precise decimal-string wire representation or enforced safe-integer bounds at both SDK/.NET boundaries. Remote TEAL compilation must record verified/configured actual compiler build/protocol identity rather than echo caller provenance.
3. Finish action BlockchainExecute adapter coverage with lane B's immutable pending/recovery contract; generic native manager refusal is not closure of an advertised action feature.
4. Complete protocol setup pinning and execute the final integrated gates only after all planned rounds. Missing local infrastructure is a setup task, not evidence of implementation correctness.
5. Revisit nested series resource bindings and public runtime loader/invocation consumer parity during the next full-goal assessment; current source must not be described as all-surface complete without that assessment.

## Integration seams

Program registrations remain lane A-owned. B owns ActionManager/frozen admission/occurrence scheduler. SDK shared files were changed additively; deployment section owns only deployment methods/types. Runtime read-only receipt observation never grants first submission authority. Actual AVM/Solana fixture execution and Ethereum fixture are separate final-gate evidence.
