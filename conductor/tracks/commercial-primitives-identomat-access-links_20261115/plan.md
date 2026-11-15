---
type: plan
track_id: commercial-primitives-identomat-access-links_20261115
loop_template: plan-execute-review-critique-futurist
---

# Implementation Plan: AZOA Commercial Primitives & Identomat Access Links

## Scope Partitions
- Partition A (`azoa-backend-contracts-migrations`):
  - `contracts/commercial-primitives.v1.json`
  - `Models/{CommercialPrimitives,MemberHandoff}.cs`
  - `Services/Compliance/ProviderDeferralLedger.cs`, `Services/Auth/{AccessLinkMintingService,MemberHandoffService}.cs`, `Services/Commercial/CommercialPrimitivesService.cs`
  - `Persistence/SurrealDb/Models/CommercialPrimitivesRecords.cs`, `.surql` schemas + `.flowchart.mermaid` goldens
  - `Controllers/CommercialPrimitivesController.cs`, `Program.cs`, `appsettings*.json`
  - `tests/AZOA.WebAPI.Tests/Commercial/CommercialPrimitivesTests.cs` & route auth inventory `commercial-primitives.json`
- Partition B (`azoa-sdk-and-node-frontend`):
  - `sdk/azoa-wallet/src/money/commercial.ts`, `src/react/commercial-components.tsx`, `src/react/{context,account-components}.tsx`, `src/api/{identity,client}.ts`, `src/money/{direct-transport,presentation}.ts`, `tests/money/commercial-primitives.test.ts`
  - `frontend/src/components/money/{commercial-slots,ride-along-view,member-money}.tsx`, `frontend/src/components/auth/member-handoff.tsx`, `frontend/src/components/layout/sidebar.tsx`, `frontend/src/app/(dashboard)/documents/page.tsx`, `frontend/src/app/ride/{page,[linkId]/page}.tsx`, `frontend/src/app/(dashboard)/{verify-identity,kyc,settings}/page.tsx`, `frontend/src/app/operator/(console)/member-deliveries/page.tsx`, `frontend/src/components/money/commercial.test.tsx`

## Phase Checklist
- [x] Phase 1: Contract schema design & partition assignment
- [x] Phase 2: Backend, SDK, and Node frontend implementation across disjoint partitions
- [x] Phase 3: Adversarial security & UX review (`REVIEW.md`, `CRITIQUE.md`)
- [x] Phase 4: Final post-edit verification suite (`dotnet test`, SDK `typecheck/test/build`, frontend `typecheck/vitest/build`) & commit on `main`
  - `sdk/azoa-wallet`: `npm run typecheck` (passed), `npm test` (46 files / 657 tests passed), `npm run build` (passed)
  - `frontend`: `npm run typecheck` (passed), `npx vitest run --project unit --project slices` (64 files / 347 tests passed; `slices-browser` Playwright deferred in Windows sandbox due to `EACCES` on `::1:63315`), `npm run build` (passed)
  - `AZOA.WebAPI.Tests`: `dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj --no-restore --verbosity minimal` (5,709 passed, 0 failed, 0 skipped)
