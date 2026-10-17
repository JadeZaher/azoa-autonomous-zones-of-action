---
type: lane-checkpoint
round: 4
wave: W2
lane: L3
helper: money-hold
branch: r4/money-hold
base: beae947
status: ready
---

# Round 4 · L3 helper `r4/money-hold` — Hold / exchange

Scope: plan §4 L3 Helper bullet + SQL-sweep SQL-04 + captured-null negatives
(`SurrealHoldCommandStore.cs:55`, `HoldReleaseWorkflowFence.cs:35`). The L3 lead merges this branch
(`git merge --no-ff r4/money-hold`) once the head subject starts `r4(money-hold): ready`.
Vocabulary: *Built* = compiled + unit-covered and run; *Authored* = compiled, real-DB test not run
(no SurrealDB in W2).

## Scope closed

| Item | Where | Status |
|---|---|---|
| **SQL-04** bare ids → `record<user>` | `Services/Reserve/ProjectTreasuryService.cs:81` (`TenantId`), `:376` (`ClosedByUserId`) → `SurrealLink.ToLink("user", …)`. Read-path audit: reads go through the record id (hash of the bare hex, unchanged); `ParseTenant` already accepts link + bare forms, so `GuardRowOwnership` compares GUIDs; `ListByTenantAsync` binds `SurrealRecordParam`; `SurrealHoldCommandStore.ExchangeUnits.cs:236-239` uses the bare-hex record id + `SurrealRecordGuid.ParseOptional`. Stale `Encoding` alias removed (Helpers rename landed in W0) | Built (`ProjectTreasuryTests` assert the link shape) |
| **Captured-null negatives** | `tests/…/SurrealHoldCommandTests.CapturedNull.cs` (`FindOwnedHoldAsync`: null tenant never reaches a tenant-held liability and vice versa; owner pin); `…WorkflowMandate.cs` `[InlineData(false, "TenantedRun")]` (a tenant-less mandate's run guard never matches a tenant-driven run; claim succeeds again once the run is tenant-less) | Authored |
| **Direct hold admission endpoints (liability reads)** | `Controllers/HoldCommandsController.cs:19,28,37` → `Services/Hold/HoldLiabilityReadService.cs` (`IHoldLiabilityReadStore`), `Providers/Stores/Surreal/SurrealHoldCommandStore.Liabilities.cs`; registered `Services/Hold/HoldRegistration.cs` | Authored (`SurrealHoldCommandTests.Liabilities.cs`) |
| **Durable recipient readiness for direct buys/sales** | Readiness is folded into the exact exchange group instead of a separate stage: `Managers/ReserveExchangeManager.cs:207,422` reads readiness only (no synchronous `ICustodialRecipientPreparer`; dependency removed from the manager and from `AddExchangeRail`); frozen as `ExchangeUnitsCommitment.RecipientFundingBaseUnits`; `Helpers/ExchangeHoldWirePlan.cs:42` → `ExchangeWireIntent.Readiness`; `Providers/Blockchain/Algorand/AlgorandProvider.Exchange.cs:109` `ExchangeMembers` prepends `[reserve→participant funding, participant asset accept]`, `:153` `ValidExchangeReadiness` (participant-only, same signing context, ceiling `:163`); validation/sender checks generalized in `AlgorandProvider.ExchangeValidation.cs`; store validation `SurrealHoldCommandStore.Exchange.cs` (`RecipientFundingBaseUnits` parse) | Authored (`ExchangeFlow` theory: folded / over-cap refused / frozen refused) |
| **Buy-back top-up on the durable transfer path** | `Services/Reserve/ReserveToTreasuryBuyBackBackstop.cs:175-245` — `IValueTransferTransactionModule` + `IValueMoveReceiptStore` exact saved wire (off-ramp pattern); in-progress claim resumes the same bytes; `:305` give-back resumes an in-flight move first and refuses while any move is `InProgress` | Built (`BuyBackQueueTests` 9/9 incl. new resume-exactly-once test) |
| **Retired exchange rows operator path** | `GET /api/exchange/operator/retired-rows`, `POST …/{operationId}/resolve` (`Controllers/ExchangeController.cs:63,78`, Operator policy) → `Services/Reserve/ExchangeRetiredRowService.cs`; new create-only table `exchange_retired_resolution` (`Persistence/SurrealDb/Models/ExchangeRetiredResolution.cs`, schema regenerated); `ExchangeSettlementReconciler` returns true only when reconciliation's chain verdict equals the attestation and the `TxHash` matches | Built (`RetiredExchangeRowTests` 6/6); store Authored (`SurrealHoldCommandTests.ExchangeRetired.cs`) |
| **Preview consent oracle** | `Controllers/ExchangeController.cs:126` — the live consent check now runs on every request incl. plain previews (the preview reads identity readiness) | Built (`ExchangeRefusalStatusTests`) |
| **Hold participant stage on original-login expiry** | Verified, no stall: workflow witnesses are fenced on revocation, not expiry (`SigningAuthorityFence.Credential` checks `ExpiresAt` only when `direct`); participant stages refuse an expired login and accept a renewed one without restarting quote/stage/generation. Recorded in `Services/Hold/AGENTS.md` §participant-renewal | Authored (`SurrealHoldCommandTests.LoginExpiry.cs`) |
| **Reserve partitions under non-default policies** | `Services/Reserve/ReserveDrawAuthority.cs:156` `ReservePartitionScope.RefuseDeclaration`; used by `PlatformReserveService.cs:324` (declare) and `PlatformReserveController.cs:120` (read). `DeclaredTenants`/`AnyAdmittedTenant` widen DRAW only; the platform unit's budget stays operator-only | Built (`PlatformReserveTests` theory) |
| **D-7 exchange semantics behind L5's declaration** | `Managers/ReserveExchangeManager.cs:169,444` — quotes and exchanges refuse with `RAIL_UNAVAILABLE` before any journal row unless the provider advertises `IExchangeTransactionModule` on `DefaultNetwork` | Authored (`ExchangeFlow` theory) |

## Published contracts (for L7 / §4.9)

| Route | Shape |
|---|---|
| `GET /api/hold-commands/liabilities?offset&limit` | `AZOAResult<HoldLiabilityPage{ items: HoldLiabilityView[], nextOffset }>`; `HoldLiabilityView{ holdRecordId, holdId, asset{chainType,network,protocolIdentity,assetId,holdingAddress}, quantityBaseUnits, state, ownerUserId, beneficiaryUserId, funderUserId?, custodyTenantId?, fundingSource, reservePartition, releaseNotBefore?, autoRelease, activeReleaseMandateId?, purchased, openingCommandId?, createdAt, updatedAt }` |
| `GET /api/hold-commands/liabilities/{holdRecordId}` | `AZOAResult<HoldLiabilityView>`; 404 when missing or not visible (indistinguishable) |
| `GET /api/hold-commands/reserve-assets` | `AZOAResult<HoldReserveAssetView[]>`; `{ asset, unitName, decimals, partitions[{ name, declaredBaseUnits, availableBaseUnits }] }` — exactly what `POST /api/hold-commands` `intent.asset` must name |
| `GET /api/exchange/operator/retired-rows?limit` (Operator) | `AZOAResult<RetiredExchangeRowView[]>`; `{ operationId, status, transactionHash?, chainType?, createdAt, resolvedOutcome?, resolvedReason?, resolvedAt? }` |
| `POST /api/exchange/operator/retired-rows/{operationId}/resolve` (Operator) | body `{ outcome: "Confirmed"|"Failed", reason }` → 200 view; 400 invalid; 404 no open row with a `TxHash`; 409 different prior resolution |
| `POST /api/exchange/{userId}` | previews without live consent now 403 (`FORBIDDEN`); an unready participant whose funding exceeds the cap → `RECIPIENT_NOT_READY`; frozen holding → `NOT_PERMITTED`; undeclared network → `RAIL_UNAVAILABLE` (503) |

## POCO deltas

- New table `exchange_retired_resolution` (`ExchangeRetiredResolution`): new table, no old rows; record id = operation id is the single-winner key (unique-index audit: record id covers it). Generated `Generated/Schemas/exchange_retired_resolution.surql` + flowcharts (`domain`, `identity`) via the golden write mode; second run clean.
- No field changes on existing tables. `ExchangeUnitsCommitment.RecipientFundingBaseUnits` / `ExchangeWireIntent.Readiness` live inside JSON columns (`intent_json`, `wire_intent_json`) and are `JsonIgnore(WhenWritingNull)`, so existing intent hashes are unchanged.

## Hunks

```hunk path=appsettings.json
    "MaximumRecipientFundingBaseUnits": "201000",
```

No `Program.cs` hunks (registrations went into `HoldRegistration.AddPlatformHold` and `ExchangeRailRegistration.AddExchangeRail`).

## Edits outside the helper globs (declared hot-file keys; hold/exchange-only consumers)

`Models/Blockchain/ExchangeHoldSourceIntent.cs` (one optional field), `Models/Blockchain/ExchangeWireContracts.cs`
(`ExchangeWireReadiness` + optional field), `Helpers/ExchangeHoldWirePlan.cs`, `Persistence/SurrealDb/Models/ExchangeRetiredResolution.cs`
+ generated schema/flowcharts, `appsettings.json` `Exchange:MaximumRecipientFundingBaseUnits` (L3 key), tests under
`tests/AZOA.WebAPI.Tests/{Exchange,Services/Reserve,Controllers}/` and `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealHoldCommandTests*.cs`,
inventories `Architecture/Inventories/{RouteAuthorization,CrossTenant}/L3-hold.json` (separate file so the lead's `L3.json` never conflicts).

## Inventories

`tests/AZOA.WebAPI.Tests/Architecture/Inventories/RouteAuthorization/L3-hold.json` — 21 mutating routes across
`HoldCommands`, `HoldReleaseMandates`, `Exchange`, `PlatformReserve`, `ProjectTreasuryTenant` (value-moving rows name
`transfer:sign` / `swap:sign`). `…/CrossTenant/L3-hold.json` — 45 rows: every owned method the L4 gate reflects on
`IHoldCommandStore`, `IExchangeHoldSourceStore`, `IHoldReleaseMandateStore`, `SurrealProjectTreasuryLedgerStore`, plus
the new `SurrealHoldCommandStore` read methods. Format checked against `r4/security-gates` `GateInventory`.

## Compile / tests run

- `dotnet build azoa.sln -c Debug` (full `--no-incremental`): **0 errors, 22 warnings** (≤ 25; none in touched files).
- Filtered unit runs (owned/new): `BuyBackQueueTests`, `ExchangeRefusalStatusTests`, `PlatformReserve*`, `ProjectTreasury*`, `RetiredExchangeRowTests` — 131 passed, 0 failed.
- Schema goldens (`AttributePocoByteEquivalenceTests|FlowchartRegenerationTests`) regenerated, then 133/133 green.
- Not run (need SurrealDB): every `SurrealHoldCommandTests.*` file above.
- Seen red, not mine (pre-existing on base): `AlgorandExchangeWireTests` (fixture zero-field defect — L5 fixed the fixture on `r4/chains`; `LostResponse…` still needs the receipt fix below), `RiskHoldDispatchSeamTests`, `Tinyman*`, `WttAlgorandHoldingWireTests`, `ActionNodeOutputSchemaGoldenTests(CredentialRevoke)`.

## Self-review (2 iterations)

- **Transaction scope** (`round3-transaction-scope-review.md`): no new multi-statement batches. New writes: `RecordRetiredResolutionAsync` single create-only on the record key, loser re-reads the winner (`// lock:` comment); backstop uses the existing idempotency claim + first-writer-wins prepared receipt (concurrent resumes sign once via the provider preparation journal keyed `value:{key}:{generation}`, draws/credits replay-keyed). Exchange readiness adds no statements; it rides the existing source/wire CAS.
- **Security**: liability reads scoped by actor + acting tenant from claims, same rule in query and `Visible`, uniform not-found; reserve-asset authority derived exactly like `CommitLocalAsync`; operator routes under the `Operator` policy, operator id from claims, outcome enum + reason length validated, `ResolvedByUserId` plain string (no link column); previews consent-gated; partition budgeting stricter; readiness funding bounded twice (option + provider ceiling) and only for the leg-signing participant; no bare ids into record links (SQL-04 fixed).
- **Fail-closed classes (§7)**: no raw SurrealQL added; typed `null` literals only in explicit branches (no captured-null guard on owner/tenant columns); typed enum compare on `OperationLog.Status` (1.3.0 binds as written); no `;` in literals; no ordinal casts; converter-less enums none added.
- **Flow tests**: assertions are counters/rows/codes, not mock calls; `ExchangeFlow` drives the public manager seam with real stores; buy-back test proves one prepared transfer and one credit across retries.
- **Pruning gate**: IDE0005 clean; stale `Encoding` alias and the now-unused pending-marker helper removed; interfaces carry the contract docs; rationale moved to `Services/Reserve/AGENTS.md` (§three-outcomes rewritten, §d7-activation, §recipient-readiness, §retired-rows, §declaration-authority, §treasury-links) and `Services/Hold/AGENTS.md` (§participant-renewal note, §liability-reads). Retained duplicate: `ProjectTreasuryService.ParseTenant` (more tolerant than `ReservePartitionScope.ParseRowUserId`: dashed GUIDs and `⟨⟩`) — left as is.

## Needs other lane

- **L5 (blocking for real-chain exchange settlement):** `Providers/Blockchain/Algorand/AlgorandGroupReceipt.cs:64` compares `amount == transfer.AssetAmount`; a zero-amount member (the folded participant acceptance, and any 0-amount axfer) decodes with `AssetAmount == null`, so the confirmed group never matches and the exchange stays `Unknown`. Change to `amount == (transfer.AssetAmount ?? 0UL)`, same as the fee/payment fixes already on `r4/chains` (`65e704c5`). Also: declare `IExchangeTransactionModule` only on networks whose fixture passed — `TryGetModule` returning null on an unpinned network is the whole D-7 pin for the exchange (§d7-activation). Do not edit `AlgorandProvider.Exchange*.cs` (L3 helper; `ExchangeMembers` changed shape).
- **L1 (unlisted file):** `Controllers/Admin/ProjectTreasuryController.cs:242` echoes `row.TenantId`, which is now the link form `user:<hex>`; map it through a GUID parse (e.g. `SurrealRecordGuid.BareId`) so the operator wire keeps the bare tenant id.
- **L7:** SDK/console methods for the five routes above; the hold admission UI can now read liabilities and drawable reserve assets; previews need live consent; operator console page for retired exchange rows.
- **L6:** the captured-null negatives for `SurrealHoldCommandStore.cs:55` and `HoldReleaseWorkflowFence.cs:35` are authored here — run them in the sweep rather than duplicating; `SurrealHoldCommandTests.cs` bootstrap now also creates `operation_log` and `exchange_retired_resolution`.
- **L3 lead:** `AllocationManager` still uses the synchronous `ICustodialRecipientPreparer` (allocation issuance, out of this helper's scope); the exchange no longer registers it — `AllocationIssuanceRegistration` still does.
