---
type: lane-checkpoint
lane: L1-helperA
branch: r4/core-chains
base: e075b6d
---

# Round 4 W0 — Helper A (chain providers)

Scope: `Providers/Blockchain/**` plus owned chain-named tests and `tests/AZOA.WebAPI.Tests/Managers/Dex/**` (plan §4 L1 Helper A). Status vocabulary: production is **Built** (compiles); owned tests are **Authored** (see Compile result).

## ERR groups closed

| ERR | Site | Decision |
|---|---|---|
| 81-119 `ValidateAddressFormat` ×17 | new `Providers/Blockchain/Solana/SolanaAddress.cs` | `TryParse` (canonical base58, exactly 32 bytes, re-encode equality) + `IsSigner` (on-curve) / `IsAccount` (any key, PDAs valid). `SolanaProvider.TryParsePublicKey` now delegates to it (duplicate base58 logic and two constants removed). Per-site table in `Solana/AGENTS.md` §Address roles: signer = swap owner, native payer/authority/authorities, WTT source and target authority; account = treasury, cost-guard program, mint, token account, refund destination, instruction program/accounts, artifact mint/owner, rent recipient, WTT recipient |
| 78-91 Jupiter CS0165 (`SolanaProvider.DexQuote.cs:45-117`) | — | Knock-on errors from the missing method, not missing values. `networkCap`/`rentCap`/`cleanupCap` are parsed from `requested.Budget` in the admission condition (`:51-53`) and the quote refuses when any is absent or non-canonical, matching `round3-dex-contract.md` §Identity ("maximum network cost, maximum prerequisite rent/minimum balance and cleanup budget"; "Network fees and rent are separate explicit caps"). No defaults were introduced; an unparsable cap fails closed with `InvalidRequest` |
| 98-107 `NativeArtifact.cs:30-35` | `SolanaProvider.NativeArtifact.cs:30-35,70-71,80` | `IsAccount` for artifact program/accounts/mint/owner; `IsSigner` for payer/authority/authorities |
| 108-109 CS1628 (Solana) | `SolanaProvider.NativeArtifact.cs:79-85` | `out input` copied into locals `(payer, authorityAddress)` before the lambdas |
| 28 CS1628 (Algorand) | `AlgorandProvider.NativeArtifact.cs` `TryAvmArtifact` | validates a local `parsed`; assigns the `out` only on success |
| 46,48,62,63,114,117 `SigningContext` null ×6 | Algorand/Ethereum/Solana `ValidWtt{Source,Target}Intent` | `SigningContext` is a struct, so `Authority.Context: not null` cannot compile. The pattern now requires the `WttSigningAuthority` record (`SourceAuthority: not null` / `TargetAuthority: not null`); every validator still checks the real bound context: resolvable non-platform user = actor, chain, acting tenant = intent tenant, grantor = actor when tenant-driven, and the exact `wtt:source:sign` / `wtt:target:sign` scope. No placeholder context was introduced |
| 9,45,47,49 `ApplicationId` ×4 | new `Algorand/AlgorandApplicationCall.cs` | Algorand2 declares `ApplicationId` per on-completion subclass (reflected from the 2.0.0.2024051911 assembly). `Id()` switches over the six subclasses; create or unknown returns null. Receipts compare `is not { } expected || actual != expected` (mismatch); WTT observations return the existing "Unknown application" error |
| 29 `AssetCloseTo` | `AlgorandProvider.ValueAcceptance.cs:124` | Dead check removed: `AssetAcceptTransaction` has no close-to field. `DecodeCanonical` rejects any `aclose` wire (other subclass, or byte-exact re-encode mismatch). Recorded in `Algorand/AGENTS.md` §Asset acceptance wire |
| 32 `long→int` | `AlgorandProvider.ValueAcceptance.cs:31,56` | `Cost.Generation` outside `0..int.MaxValue` refused at admission; the claim then narrows an in-range value. Nothing is truncated |
| 64 `CloseAccount` | `JupiterCleanupWire.cs:32` | explicit `TokenProgram.ProgramIdKey` (same bytes as the old Solnet default; matches `SolanaSeededNativeAccount.cs:43`) |
| 93,113,116,127 init-only `Data` ×4 | new `Solana/SolanaAssociatedTokenAccount.cs`; `WttSolanaCostGuard.cs:62-71` | `CreateIdempotent(payer, owner, mint)` rebuilds Solnet's six-account create with opcode 1 (same wire as before, matches `JupiterSetupInstructions.CreateOwnerAta`); used by NativeAccounts CreateAta and both WTT ATA creates. `BuildRegistration` builds a new instruction from the baseline guard: opcode 2, data + metadata hash, keys + metadata program + program data |
| 96-97 swapped arguments | `SolanaProvider.NativeAccounts.cs:80` | real bug: `Update(mint, authority, artifact, existing)`; the requested values override the snapshot, not the reverse |
| 50-60 Nethereum `ABIType` ×5 | — | already gone after the prelude's `ByteEncoding` rename (cascade from the `Encoding` ambiguity); no edit |
| SRDB0001 / IDE0005 | — | none reported in `Providers/Blockchain/**` |

## Owned test adaptations

- `tests/AZOA.WebAPI.Tests/Managers/Dex/JupiterCleanupEvidenceTests.cs:52`, `JupiterProviderCleanupTests.cs:59`: `CloseAccount` with explicit program id (same bytes as production).
- `tests/AZOA.WebAPI.Tests/Managers/Dex/TinymanSwapReceiptTests.cs:161`: the fake receipt branch matches `ApplicationNoopTransaction` (it already emits `on-completion = noop`).
- `tests/AZOA.WebAPI.Tests/Services/WttAlgorandGroupReceiptTests.cs:73-79`: subclass switch for the application id; removed `.Value` on non-nullable `AssetAmount`/`XferAsset`.
- `tests/AZOA.WebAPI.IntegrationTests/Deployments/SolanaRentGuardProtocolTests.cs:48-49`: the short/trailing mutations build a new instruction instead of assigning init-only `Data`.

Test deletions: none. No assertion was weakened.

## Needs outside subtree

None found. No interface, model or caller outside `Providers/Blockchain/**` had to change.

## Compile result

- `dotnet build AZOA.WebAPI.csproj -c Debug` at the ready commit: **0 errors in `Providers/Blockchain/**`**, no new warnings there. 66 errors remain, all in the lead's paths (SRDB0001 stores/fences, `TypedUpdateOnlyBuilder` args, `SurrealLink`, fiat D1, vocabulary drift, `Program.cs:1163`), identical to the baseline set minus this lane's 51.
- **Owned tests not compiled.** Both test projects reference the app project, which does not build until the lead's 66 errors close. The test edits above come from static inspection against the SDK assemblies (Algorand2 reflection, Solnet 8.7.0 XML docs). The lead's first green test-project build is the real check for these files. I'm handing that check to the lead, not claiming it.

## Self-review (2 passes)

- **Security:** signing scope checks unchanged and still exact. The new signer predicate is stricter than anything before it (nothing existed), so a PDA can no longer be offered as a custody signer. No bare ids into record links, no stores or SurrealQL touched, no RPC URL or key logging added.
- **Fail-closed classes (§7):** no SurrealQL touched in this lane, so most classes do not apply. On chain-side fail-closed: missing caps refuse, unknown application calls refuse, out-of-range generation refuses, and non-canonical addresses refuse.
- **Transaction-scope checklist:** no store or fence touched.
- **Pruning gate:** duplicate base58 parser removed; terse doc-comments; rationale in `Solana/AGENTS.md` §Address roles and `Algorand/AGENTS.md` §Application call identity / §Asset acceptance wire; no unused usings introduced.
