---
type: review
track_id: commercial-primitives-identomat-access-links_20261115
reviewer: adversarial-loop
---

# Adversarial Review: AZOA Commercial Primitives

## Checked Boundaries
1. **Access Link Signing & URL Safety**:
   - Server minting uses HMAC-SHA256 signatures with expiration checks in `AccessLinkMintingService.cs` and verifies signature, expiry, scope, and subject before granting ride-along state.
   - Frontend `safeCommercialHref` in `sdk/azoa-wallet` and `safeReturnUrl` in `member-handoff.tsx` restrict protocols to safe HTTP(S) / relative paths, blocking `javascript:` / `data:` injection.
2. **Route Authorization Inventory**:
   - Added `tests/AZOA.WebAPI.Tests/Architecture/Inventories/RouteAuthorization/commercial-primitives.json` so every new `/api/commercial/*` endpoint has explicit authorization coverage verified by architecture tests.
3. **Graceful Identomat Deferral**:
   - Absence of Identomat vendor credentials returns `PendingVendorCredentials` and logs an entry in `ProviderDeferralLedger` rather than stubbing an `"Approved"` KYC record or throwing unhandled 500s.
4. **Schema & Flowchart Goldens**:
   - `.surql` table definitions and `.flowchart.mermaid` goldens updated in lockstep with `CommercialPrimitivesRecords.cs`.

## Verification Record
- `sdk/azoa-wallet`: `npm run typecheck`, `npm test` (46 test files, 657 tests passed), `npm run build` passed.
- `frontend`: `npm run typecheck`, `npx vitest run --project unit --project slices` (64 test files, 347 tests passed), `npm run build` passed (`slices-browser` deferred in Windows sandbox due to `EACCES` binding Vite on `::1:63315`).
- `AZOA.WebAPI.Tests`: `dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj --no-restore --verbosity minimal` passed (`5,709` passed, `0` failed, `0` skipped).
