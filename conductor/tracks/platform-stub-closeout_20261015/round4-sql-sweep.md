---
type: audit
round: 4
stance: sql-resweep
base: feat/platform-stub-closeout @ f968955 (SurrealForge 1.3.0)
status: final
---

# Round 4 W1 SurrealQL re-sweep

Read-only, static. Library behaviour is checked against `surrealforge/src/SurrealForge.Client` at `586e316` (1.3.0):
- `SurrealQuery.Validate` (`Query/SurrealQuery.cs:524-558`): strict, textual over the whole combined query; leading `LET`s are excluded.
- `SurrealRecordLink.Promote`/`PromoteText` (`Query/SurrealRecordLink.cs:130-158`): bare text is **never** promoted.
- `ExpressionTranslator` (`:302-335`): captured null → `col = NONE`.
- `TypedPredicateCompiler` (`:20`): typed mutation builders bind the enum name.
- `MarshalEnumAsWritten` (`Cbor/SurrealCborMarshaller.cs:440-490`).
- The read path strips the table prefix from `[Id]` (`Json/SurrealJsonOptions.cs:141`).

Evidence for the RETURN slot: OPT probe P1 (`round4-assessment-optimism.md:38`), plus helper B's fix `84edf4c`.

## Findings

| id | sev | class | file:line | what fails | lane | fix sketch | status |
|---|---|---|---|---|---|---|---|
| SQL-01 | HIGH | positional offset after top-level RETURN | `Providers/Stores/Surreal/SurrealUserStore.cs:257` (batch `:233-249`) | Claim-ownership path reads `Statements.Count - 2`. A top-level `RETURN` drops `COMMIT` from the result array, so the read lands on the last `ClaimRevocationsAsync` statement (`SurrealConsentLifecycleStore.Claim.cs:42-44`). With zero grants that slot is an `IF`: the claim **commits** (invite consumed, ownership moved, grants revoked) but the API returns Conflict, and a retry is impossible. With ≥1 grant it is the `claim_proof_N` CREATE row, deserialized as `GeneratedUser` (a made-up user, or `FromPoco` throws). **Allow path refused after commit.** | L4 | `var resultIndex = statements.Count;` before adding RETURN; read `GetValues(resultIndex)` (correct under either slot semantics) | CONFIRMED |
| SQL-02 | HIGH | positional offset after top-level RETURN | `Providers/Stores/Surreal/SurrealWalletStore.TenantBootstrap.cs:111` (batch `:64-103`) | `Statements.Count - 2` lands on the `LET $_bootstrap_wallet` (create path) or the `IF` (update path). `.Single()` throws an uncaught `InvalidOperationException` **after the wallet commits**. The retry takes the update path and throws again, so tenant child-wallet provisioning always returns 500. | L4 | Same as SQL-01 | CONFIRMED |
| SQL-03 | HIGH | bare id → `[References]` | `Services/Fiat/TenantPriceScheduleStore.cs:135` (`TenantId`), `:146` (`RegisteredByUserId`); write `:280-284` | `CREATE … CONTENT $_body` with the POCO. Bare `ToString("N")` stays text and is rejected by `record<user>` / `option<record<user>>` (`Generated/Schemas/tenant_price_schedule.surql`). **Every price-schedule registration is refused**, which blocks D-6/D-8 payout pricing. | L3 lead | `SurrealLink.ToLink("user", …)` at both sites, **and** change the read projection `:346` `Guid.TryParseExact(row.TenantId, "N")` → `SurrealRecordGuid.ParseOptional`. Otherwise, once the write is fixed, tenant schedules project as tenant-less (node-level), which **widens** access. | CONFIRMED |
| SQL-04 | HIGH | bare id → `[References]` | `Services/Reserve/ProjectTreasuryService.cs:83` (`TenantId = reference.TenantIdText`), `:378` (`ClosedByUserId`); writes `ProjectTreasuryService.cs:772-781` (store `CreateIfAbsentAsync`/update, `CONTENT $_body`) | `project_treasury_ledger.tenant_id TYPE record<user>` rejects bare text. **Project treasury open and close always fail**; exchange-units / buy-back treasury paths are dead. | L3 helper | `ToLink("user", …)`; audit the treasury read paths for bare-id comparisons when fixing | CONFIRMED |
| SQL-05 | HIGH | bare id → `[References]` | `Services/Users/NftLifecycleService.cs:529-531` (`NftOwnership.UserId = authority.HolderUserId`, bare from `:517`) → `Providers/Stores/Surreal/SurrealNftLifecycleStore.cs:277` `SurrealWriter.Upsert(ownership)` | `nft_ownership.user_id TYPE record<user>` rejects it. This is not `NFT_LIFECYCLE_CHANGED`, so the exception propagates. **Every confirmed lifecycle (Mint/Transfer/Refund) fails to finalize after the chain effect**; the lifecycle stays `Confirmed`. | L5 | `UserId = SurrealLink.ToLink("user", authority.HolderUserId)!` | CONFIRMED |
| SQL-06 | HIGH | bare id → `[References]` (D2 extension) | `Services/Fiat/OffRamp/OffRampService.cs:343` and `Services/Fiat/OnRamp/OnRampSettlementHandler.cs:352` (`PriceScheduleId` = bare `RegisteredPriceSchedule.RecordId` = stripped `row.Id`, `TenantPriceScheduleStore.cs:343-344`); `Services/Fiat/OffRamp/PayoutReversalEventHandler.cs:174` (`ReversalId = recorded.Result.Id`) → `FiatPayoutLedger.cs:174` typed `.Set` | `record<tenant_price_schedule>` / `record<fiat_reversal>` reject bare text. Priced payouts and lots fail on write; payout reversal recording fails. These sit inside the hops D2 already breaks, so they **stay masked until D2 is fixed**. | L3 lead | `ToLink(TenantPriceSchedule.SchemaNameConst, …)` / `ToLink(FiatReversal.SchemaNameConst, …)` | CONFIRMED |
| SQL-07 | MEDIUM | bare id → `[References]` | `Services/Fiat/Reversal/FiatReversalOperatorController.cs:184` (`FlagClearedBy`) → `FiatReversalLedger.cs:266` `CONTENT $_body` | `option<record<user>>` rejects it, so the operator "clear account flag" action always fails. | L3 lead | `ToLink("user", …)` | CONFIRMED |
| SQL-08 | (plan) HIGH | bare id → `[References]` (planned D2) | `StripeWebhookInbox.cs:467-468`; `OnRampSettlementHandler.cs:545-547`; `OffRampService.cs:335-337` | Re-verified: `FiatPayoutLedger.cs:112` and `OnRampLotLedger.cs:107` write the candidate verbatim with `SurrealWriter.Create`. **Correction to the plan list:** `FiatReversalEventHandler.cs:239-242` and `PayoutReversalEventHandler.cs:226-227` copy links from rows read back from the DB, so they are masked only, not bare. | L3 lead | as planned | CONFIRMED |
| SQL-09 | MEDIUM | empty record key (ADV §4 #8 extension) | `Providers/Stores/Surreal/SurrealKycStore.cs:199-201` (`type::record($_selection_table, '')` in a FROM target when `!hasTenant`) | Same exposure as `HostedKycCreationFence.cs:25`. A FROM target is evaluated even under `WHERE false`, so an empty key is created for every first-party KYC submission. If 3.x rejects empty keys, **first-party KYC submission fails closed**. The `:209` profile load is safe (provider key never empty). AND/OR-guarded uses (`ActionExecutionFence.cs:16-17`, `SurrealActionSigningAuthorizationStore.cs:45-46`, `HoldReleaseWorkflowFence.cs:56-57`) are only safe if 3.x short-circuits; verify with the same probe. | L2 | Emit the load/assert statements only when `hasTenant`, or wrap them in `IF $_has_tenant { … }`. Add a probe to the sweep. | PLAUSIBLE |
| SQL-10 | LOW | ordinal enum casts | `Providers/Stores/Surreal/SurrealActionNodeExecutionStore.cs:106,227,239` (`(Row.ActionNodeState)(int)…`) | Member orders are identical today (`Models/Actions/ActionEnums.cs:142-150` vs `ActionNodeExecution.cs:39-47`). A reorder silently maps states wrongly, including inside the `:106` CAS guard. W0 fixed `SurrealActionRunStore` but missed this file, and L6's `SurrealQlSourceRulesTests` will fail on it. | L4 | Name mapping (`Enum.Parse`/switch) as in `SurrealActionRunStore.ToKind/FromKind` | CONFIRMED (latent) |
| SQL-11 | LOW | strict validator (extra param) | `Providers/Stores/Surreal/SurrealSwapOperationStore.cs:50-62` | The cancel branch binds `_swap_operation` but its SQL never references it. On an absent row, `Validate` throws `SurrealQueryValidationException`, which the `SurrealStatementException` catch does not handle. Unreachable: `SwapOperationService.Commands.cs:75-89` reads the row first and nothing deletes operations. Carries ADV #7. | L5 | Bind `_swap_operation` only in the admit branch | CONFIRMED (unreachable) |
| SQL-12 | LOW | explicit-NULL raw write | `Services/Sagas/SurrealSagaStore.cs:441,495,531,644` (`_error`/`_reason`), `:664` (`_output`) | Raw `SET last_error/output = $_x` binds a possibly null value (`Truncate` returns null). If the SDK encodes C# null as NULL rather than NONE, the SCHEMAFULL `option<string>` columns (`saga_steps.surql`) reject the write, and a step completing with a null output fails. Behaviour depends on the SDK wire encoding, which I could not verify statically. | L4 | `IF $_has_output { … }` / unset form, or a probe in `Round4Gates` | PLAUSIBLE |
| SQL-13 | LOW | non-literal `Of` outside the template (A-1 convention) | `Providers/Stores/Surreal/SurrealBridgeStore.cs:351,468,507` (`Of(sql)` from a `StringBuilder` of literal SET parts, `:531-543`); `Providers/Stores/Surreal/SurrealKycScreeningStore.cs:298` (`EraseSubjectQuery.Split(';').Select(SurrealQuery.Of)`) | Not fail-closed today: the text is code-only and the const has no quoted `;`. A `var sql` indirection gets past SRDB0001 and the "SRDB0001 only in the template" rule. A future quoted `;` in the erase const would split mid-literal. | L4 (Bridge), L2 (KycScreening) | Bridge → `SurrealIdentifierTemplate` fragments or typed `UpdateOnly`; KycScreening → explicit `Of(...)` per statement. L6 rule: no `SurrealQuery.Of(<non-literal>)` outside `Core/Surreal/{SurrealIdentifierTemplate,SurrealGeneratedStatementScope,SigningAuthorityFence}.cs` | CONFIRMED (convention) |

## ADV §4 unread set: verdicts

- **`SurrealHoldCommandStore.*`** (24 files), `HoldRelease{Exchange,Mandate,Workflow}Fence`, `SurrealHoldReleaseMandateStore.*`: **no findings.**
  - Every single-record read is captured with `AssignRecord` (`HoldReleaseWorkflowFence.cs:44`, `HoldReleaseExchangeFence.cs:31-34,53`, `SurrealHoldCommandStore.cs:158`). Every other capture is an `UPDATE ONLY`.
  - Raw guards compare plain-string columns (`HoldCommandReceipt`, `HoldReleaseMandate`, `ExchangeHoldSource` carry no `[References]`).
  - The `HoldLedger` ref columns are written as links (`Services/Hold/HoldAggregateProjection.cs:18-23`).
  - The only positional read: `ExchangeWire.cs:118-129` (absolute index).
- **WTT/swap `results[guards.Count + k]`**: **no findings.**
  - `SurrealSwapOperationStore.cs:74,108`: `+1` = the mutation after BEGIN + guards.
  - `.Stages.cs:169`: `+5` = the RETURN slot (BEGIN, guards, LET, IF, LET, IF, RETURN).
  - WTT uses an absolute `statements.Count` captured before RETURN (`SurrealWttOperationStore.cs:102,135`, `.Stages.cs:283`).
- **`SurrealNodeFeeSettlementStore`**: **no findings.**
  - Offsets verified: admission `4/5/6` (`:63-101`, `:158-162`); atomic group `4` (`:379-427`, `:438`); terminal `4/5/6` (`:627-672`, `:681-687`).
  - Every state parameter uses `.ToString()`, matching the converters added in W0 (`NodeFeeSettlement.cs:112-125`, `NodeFeeAtomicGroup.cs:74`) and the ledger's `"InProgress"`.
- **`SurrealSagaStore`**: offsets `GetValues(2)` (`:271-293`) and `createdStatementIndex: 3` (`:402-412`, `:498-508`) verified. Status tokens are PascalCase consts matching `StepStatus` (`:43-49`). Only SQL-12 remains.
- **`SurrealGovernanceStore.*`**: **no findings.**
  - Bare `proposal.Id`/`TenantId` into `type::record` is correct (`[Id]` reads back stripped).
  - The template tokens `_option{n}`/`_count{n}` are bound (`Finalization.cs:45-49`).
  - `status = 'APPROVED'` and `decision = 'NO_HIT'` match the as-declared converter names.
- **`SurrealConsentLifecycleStore.*`**: **no findings.** Tenants come from a `Guid` (never null). Reusing the same `LET` variable inside the loop (`_claim_revoked`, `_consent_offboard_grant`) passes `Validate`.
- **`TenantPriceScheduleStore`**: SQL-03. Its raw reads (`:69-77`) compare plain string columns.
- **Non-literal `SurrealQuery.Of`**: 16 at ADV time, **9 now** (the rest moved to `SurrealIdentifierTemplate`):
  - `SigningAuthorityFence.cs:172`: every `$_sa_*` token in the 31 template tokens is a LET target or bound as `prefix|nested + "_x"` (`:55-165`).
  - `SurrealGeneratedStatementScope.cs:16,31,45,94`: validated tokens.
  - `SurrealIdentifierTemplate.cs:16`: 15 callers checked; every `{token}_x` is bound (`DeploymentAuthorityFence`, `ActionWorkflowAdmissionStore`, `ActionLocalMutationStore.Witnesses`, `GovernanceStore.Finalization`, `WalletAuthIdentityMigrationService`).
  - `SurrealSwapOperationStore.cs:50`: SQL-11.
  - `OnRampLotLookup.cs:126`: const statements.
  - `SurrealBridgeStore.cs:351,468,507` and `SurrealKycScreeningStore.cs:298`: SQL-13.

## Classes checked with zero findings

- **Binding-name mismatch.** Checks done:
  - per-file token-vs-binding cross-check;
  - prefix-template check on every fence;
  - every `$` in an `Apply`/`Assign` statement must come from its own params (`SurrealGeneratedStatementScope.cs:87-88`), which all hold;
  - variable names contain no digits (`ValidateVariable`);
  - no `Combine` merges two different values under one parameter name (last write wins).
- **Single-record SELECT captured as an array:** 0. Only 3 `Assign(SELECT)` remain (`ConsentNotificationPlan.cs:83`, `SurrealKycStore.cs:361`, `SurrealWalletStore.TenantBootstrap.cs:88`), all consumed by `array::len`. Raw `LET $x = (SELECT …)` sites always index `[0]`.
- **Strict validator rejecting SQL-declared variables:** 0. The W0 `Assign` LET targets are excluded as leading LETs.
- **Lowercase-enum raw writes / reads:** 0.
  - All 35 enum-typed record columns carry `JsonStringEnumConverter` with no naming policy, so `SurrealWriter`, CONTENT marshalling, typed reads and typed mutations all agree on the member name.
  - No raw SQL compares an enum column with a lowercase literal.
  - No raw enum-valued `WithParam`.
- **Converter-less enums:** 0 (closed in W0).
- **Explicit-NULL raw writes:** 0 confirmed. SQL-12 depends on the SDK's null encoding.
- **Captured-null guard widening:** 0 widenings across 177 typed owner/tenant equality predicates. Every site that can now match a null caller value also pins the actor/user/owner. These intentional fail-closed→allow flips need L6 negatives:
  - `SurrealHoldCommandStore.cs:55`
  - `HoldReleaseWorkflowFence.cs:35`
  - `SurrealFungibleCreationStore.cs:298`
  - `SurrealHoldReleaseMandateStore.Discovery.cs:16`
  - `SurrealKycStore.cs:44,58`

  Self-read pins (`SurrealHoldCommandStore.cs:146,168`, `.Admission.cs:74,92`, `.Completion.cs:70,81`, `.ExchangeUnits.cs:198,217`, `HoldReleaseMandateFence.cs:44`) used to THROW on every platform/tenant-less row and now succeed. That is a correctness fix, not a widening. No typed `!=` compares against a captured value.
- **Bare ids in typed `Where` on a `[References]` column** (silent miss): 0.
- **`;` inside `Of` literals:** 0 (F-H13 closed in W0).
- **Statements after a top-level RETURN:** 0.
- **A DB transaction spanning an external/chain call:** 0. Every transaction is one `BEGIN…COMMIT` request; there are no `http::` calls and no client-held transaction objects.

## RETURN-site reconciliation (round-3 dispute 12 vs 15)

**15 sites in 14 files. ADV was correct; OPT undercounted by 3.** All 15 are directly before `COMMIT`, inside `BEGIN`:
- `SurrealAdminBootstrapStateStore.cs:94` (idx 7)
- `SurrealDeploymentRecoveryAuditStore.cs:88`
- `SurrealDeploymentStore.RecoveryWithdrawal.cs:42` (idx 5)
- `SurrealEthereumDexAllowanceStore.cs:134`
- `SurrealEthereumPreparationStore.cs:65`
- `SurrealHoldCommandStore.ExchangeWire.cs:119`
- `SurrealHostedSessionStore.cs:384`
- `SurrealNftLifecycleStore.cs:232`
- `SurrealNodeFeeScheduleStore.cs:67` (idx 4)
- `SurrealSwapOperationStore.Stages.cs:158` (g+5)
- `SurrealUserStore.cs:249`
- `SurrealWalletStore.TenantBootstrap.cs:102`
- `SurrealWttOperationStore.cs:103,136`
- `SurrealWttOperationStore.Stages.cs:284`

13 are read by absolute index. **2 are read end-relative and are defects: SQL-01 and SQL-02.** Two `RETURN 1` health probes sit outside transactions and are not counted. L6 gate rule: after a top-level RETURN, never index with `Statements.Count - k`.

## `[References]` triage (D2/F9)

- 126 `[References]`/`record<>` properties on 59 POCO classes.
- After namespace resolution: 194 typed sites (object initializers, `.Set`, `Where`) plus 8 setter-style assignments. The ~216 earlier candidates included ~72 domain-model initializers in `Managers/`.

Patterns:
1. **Store normalizes** in `ToStorage` or a private wire POCO: KYC, KYC screening/control, hosted session, ecosystem, OAuth code, platform token, op log, action, holon, wallet. Safe; domain callers may pass bare ids.
2. **Link-built** via `SurrealLink.ToLink`, `SurrealRecordParam` or a `"table:"` literal. Safe.
3. **Self-pins** from rows read from the DB (link text). Safe.
4. **Direct POCO write outside a normalizing store**, with `.ToString("N")` or a stripped `row.Id`. **Defect: 16 sites.**
   - 7 planned D2 sites (SQL-08).
   - 9 new: SQL-03 ×2, SQL-04 ×2, SQL-05 ×1, SQL-06 ×3, SQL-07 ×1.

## New fail-closed class / re-plan trigger

**No new class.** All findings fall in existing §7 classes or ADV §4 items, so **no re-plan trigger.**

Note on scale: the existing D2 class grows from 7 to **16 confirmed sites** across L3 lead (13), L3 helper (2) and L5 (1). L5 is not in the plan's D2 routing. Recommendation for L6's `SurrealQlSourceRulesTests` bare-id rule:
- cover `CONTENT $_body` POCO writes;
- cover `SurrealWriter.Create/Upsert/UpdateOnly.Set` on POCOs filled outside a normalizing `ToStorage`;
- add the end-relative-after-RETURN rule.
