---
type: triage
track: final-hardening-cutover
phase: H
blocker: H7
acceptance: H-AC7
created: 2026-07-05
---

# H7 — Bucket-D value-path integration triage

Root-cause of the un-root-caused value-path integration failures from the
documented ~37-failure integration tail (Bucket D in
`tests/AZOA.WebAPI.IntegrationTests/INTEGRATION-TEST-PASSOFF.md`). Each failure
below was **reproduced** against a live SurrealDB 3.1.4 container, root-caused,
and then **fixed** (genuine product bug) or **corrected at the test** (test-design
issue where the endpoint behaves correctly). No silent acceptance.

Repro: single-test `dotnet test --filter` runs against SurrealDB on
`127.0.0.1:8000` (root/root). Real 400 bodies were captured via a temporary
body-dump in the test (since reverted).

## Verdicts

| Failure | Verdict | Root cause |
|---|---|---|
| `Holon.Interact` (remove peers/metadata) | **FIXED — product bug** | Empty-collection upsert omission in `SurrealHolonStore` |
| `Holon.Mint` | **FIXED — product bug** | `operation_log.parameters` was SCHEMAFULL non-FLEXIBLE |
| `Holon.Exchange` | **FIXED — product bug** | Same schema bug as Mint |
| `STARODK.Deploy` | **FIXED — test design** | Seed can't inject `GeneratedCode`; must generate first |
| `Holon.MoveSubtree_CyclePrevention` (adjacent, unmasked) | **FIXED — test design** | Assertion string drift; endpoint is correct |

---

## 1. `Holon.Interact_ShouldRemovePeersAndMetadata` — PRODUCT BUG (fixed)

**Symptom (reproduced):** `PeerHolonIds` still contained the removed peer after a
successful (200) interact-remove: *"Expected PeerHolonIds to be empty, but found
{…}"*.

**Root cause (`Providers/Stores/Surreal/SurrealHolonStore.cs`, `ToPoco`):** peer
ids and metadata were serialised to `JsonElement?` **only when the collection was
non-empty** (`if (h.PeerHolonIds is { Count: > 0 })`), leaving the POCO field
`null` when the caller emptied the collection. The store writes via
`SurrealWriter.Upsert` (a `SET`-based UPSERT), and SurrealForge **omits null
`option<>` fields from the SET clause** (confirmed in the package XML docs:
*"option<T> columns are OMITTED rather than set to null … an absent field is the
NONE the schema wants"*). So emptying a collection produced an UPSERT that never
touched `peer_holon_ids` / `metadata`, leaving the previously-stored non-empty
value intact. **Removing the last peer (or the last metadata key) silently did
nothing** — a real data-integrity bug on the holon value path, not test-only.

**Fix:** `ToPoco` now **always** serialises `PeerHolonIds` and `Metadata` (both are
non-null on the domain model, initialised to empty), emitting `[]` / `{}` for the
empty case so the SET clause **replaces** the column and the emptied state
persists. Added a small `SerializeToJsonElement<T>` helper. Empty array/object is
a valid value for the `option<array<string>>` / `option<object>` columns
(`option` = "or NONE", not "or empty"); the prior "no `table:id`-shaped element"
concern is unaffected because an empty array carries no elements.

Add-path (`Interact_ShouldAddPeersAndMetadata`) and all other Holon tests remain
green — the change only affects the empty-collection replace case.

## 2. `Holon.Mint` + 3. `Holon.Exchange` — PRODUCT BUG (fixed)

**Symptom (reproduced, captured body):**
```
400 SurrealBlockchainOperationStore.UpsertAsync failed: SurrealDB statement 1/1
returned ERR: Found field 'parameters.Amount', but no such field exists for
table 'operation_log'
```

**Root cause:** `operation_log` is `SCHEMAFULL` and its `parameters` field was
`TYPE option<object>` **without `FLEXIBLE`**. In a SCHEMAFULL table a plain
`object` field rejects any undeclared nested key — but `parameters` is exactly an
**opaque arbitrary-key bag** (`Dictionary<string,string>` carrying `TokenUri`,
`Amount`, `AssetType`, `WalletAddress`, `TxHash`, …). Every value operation that
persists parameters (Mint, Exchange, and any op with a non-trivial parameter set)
faulted at the store, surfacing as a 400. The controller returned 400 because the
store `UpsertAsync` error propagates out of `BlockchainOperationManager.ExecuteAsync`
before the chain call — i.e. **no value op could be recorded at all**.

**Fix:** the C#-first schema source POCO
`Persistence/SurrealDb/Models/OperationLog.cs` now decorates `Parameters` with
`[Column(Flexible = true)]` (mirroring `Quest.Metadata`). Regenerated the golden
`operation_log.surql` via `AZOA_REGENERATE_GOLDENS=1` (never hand-edited) — the
emitted field is now `TYPE option<object> FLEXIBLE`. The byte-equivalence golden
test passes (39/39). Fresh per-class test namespaces apply the regenerated schema,
so Mint/Exchange now persist and return 200.

*Note (not owned here):* `AvatarNFTController.Mint → 500` in the passoff Bucket-D
list is the **same** `operation_log.parameters` schema bug and is expected to clear
with this fix; it lives in another lane's test file so it is not asserted here.

## 4. `STARODK.Deploy_ShouldSetDeploymentConfig` — TEST DESIGN (fixed at test)

**Symptom (reproduced):** deploy returned 400 *"Dapp must be generated before
deployment."*

**Root cause:** the test seeded with `STARODKBuilder.WithGeneratedCode("some
code")`, but `STARODKCreateModel` carries only `Name/Description/PublicKey/
AvatarId` — there is **no `GeneratedCode` field on the create model**, so the
builder's value is silently dropped on `POST /api/starodk`. The seeded record has
empty `GeneratedCode`; `STARManager.DeployAsync` correctly rejects a deploy of an
ungenerated dapp. This is the intended production flow: **create → generate →
deploy** (`GeneratedCode` is set only by the generate endpoint). The endpoint is
correct; the test skipped `generate`.

**Fix (test-only):** the test now calls `POST /api/starodk/{id}/generate` before
`deploy`, matching the real flow. No product change — the guard is correct.

## 5. `Holon.MoveSubtree_CyclePrevention_ShouldReturnError` — TEST DESIGN (adjacent)

Not in the original Bucket-D list; **unmasked** by the shared-per-class namespace
fix and surfaced while verifying the owned Holon class. **Not caused by any change
in this lane** (the cycle path `EnsureNotDescendantAsync` reads neither
`peer_holon_ids` nor `metadata`); it fails identically in isolation on an
unmodified store.

**Root cause:** assertion string drift. The endpoint correctly returns `400` with
`"Cannot set a descendant holon as parent (cycle detected)."`, but the test
asserted the message contains `"Cannot move"`. Endpoint behaviour is correct.

**Fix (test-only):** assertion aligned to `"cycle detected"` (the real, more
descriptive message).

---

## ConnectWalletAsync signature-trust — VERIFICATION (H §H-followups; NOT fixed)

`Managers/WalletManager.cs:406-411` — `ConnectWalletAsync` no-ops signature
verification (*"For now, trust the address if they provide it"*). **Verified: this
does NOT grant any value-bearing capability outside the real WalletAuth
challenge-signature flow. Verdict: SAFE for alpha; the fix stays a §H-followup.**

Evidence:

1. **Not an auth/login path.** `ConnectWalletAsync` requires an **already
   authenticated** caller — `avatarId` comes from the JWT, not from the connect
   payload. It cannot mint a session or elevate privilege.
2. **Identity-granting is a separate, cryptographically-verified pipeline.**
   Login-with-wallet runs through `WalletAuthManager` (`CreateChallengeAsync` →
   `VerifyAsync` with a no-TOCTOU challenge consume + chain-specific signature
   verify → claim token; sets `avatar.AuthWalletAddress`). None of that flows
   through `ConnectWalletAsync`.
3. **The connected wallet is unsignable.** `ConnectWalletAsync` creates a
   `WalletType.External` row with **no** `EncryptedPrivateKey`. The single custody
   chokepoint `Services/Custody/KeyCustodyService.cs:122,229` signs **only**
   `WalletType.Platform` wallets (*"only WalletType.Platform wallets are
   signable"*) and requires a non-empty `EncryptedPrivateKey`. The platform can
   therefore never sign a value-bearing transaction for an address a caller merely
   "connected" — a spoofed address yields nothing signable.

**Residual risk (integrity/attribution only, not value):** an authenticated avatar
can associate an address it does not provably own (a display/label concern), and
because the uniqueness check (`WalletManager.cs:425`) blocks another avatar from
later registering that same address, an unverified connect is a mild
address-squat vector. Neither is value-bearing. Implementing the chain-specific
signature verify is worth doing for attribution correctness but is correctly
deferred — it is not an alpha blocker.

---

## Resulting integration-tail count

Bucket D is fully resolved: **4 root-caused failures fixed** (3 genuine product
bugs — one holon-store data-integrity bug + one schema bug hitting Mint & Exchange
— and 1 test-design correction), plus **1 adjacent test-design fix**
(`MoveSubtree_CyclePrevention`) unmasked during verification.

- Owned classes now fully green: `HolonControllerIntegrationTests` +
  `STARODKControllerIntegrationTests` = **41/41 passed, 0 failed**.
- Integration tail: **37 → 32** accounted-for remaining failures. The 32 are the
  previously-documented Buckets A–C (IDOR test-design, environment/repo-layout
  gates, store-layer socket/perf races) plus the two Bucket-D factory
  re-registration items (`G2_IdempotencyTocTou`, `McpAuthScoping` scheme collision)
  — none of which are value-path product bugs. `AvatarNFTController.Mint → 500`
  (another lane's file) is expected to clear via the shared `operation_log`
  FLEXIBLE fix, which would drop the tail further at the coordinator's final sweep.

## Files changed (Lane 6)

Product code:
- `Providers/Stores/Surreal/SurrealHolonStore.cs` — always-serialise
  peer/metadata collections (empty → `[]`/`{}`) so upsert replaces the column;
  added `SerializeToJsonElement<T>` helper.
- `Persistence/SurrealDb/Models/OperationLog.cs` — `Parameters` marked
  `[Column(Flexible = true)]`.
- `Persistence/SurrealDb/Generated/Schemas/operation_log.surql` — regenerated
  golden (`parameters … TYPE option<object> FLEXIBLE`).

Tests (owned failing tests):
- `tests/.../Controllers/STARODKControllerIntegrationTests.cs` — Deploy test now
  generates before deploying.
- `tests/.../Controllers/HolonControllerIntegrationTests.cs` — MoveSubtree cycle
  assertion aligned to the real message.

Build: `dotnet build AZOA.WebAPI.csproj` → 0 errors; full-rebuild warnings (17)
are all pre-existing reserved crypto/value files — **zero new warnings** in the
changed files vs the 28-warning baseline.
