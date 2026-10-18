# Round-4 interleaving gate

Real-SurrealDB race fixtures for the write-skew seams named in
`conductor/tracks/platform-stub-closeout_20261015/round3-transaction-scope-review.md`. Each fixture starts the
contending operations through the public store seam at the same versions, awaits them together, then asserts:

- exactly one winner of the contended decision (never relaxed to "at most one" or "eventually one");
- the loser's observable outcome by **re-reading the winner** (store read or a replay of the losing call);
- both branches, because the race order is not controlled. Swap fixtures run three rounds per test to visit both.

Filter: `FullyQualifiedName~Round4Gates.Interleaving|FullyQualifiedName~.Interleaved`.

## Layout

- `SurrealSwapAdmissionTests.Interleaving.cs`, `SurrealHoldCommandTests.Interleaving.cs` are **partial extensions** of
  the existing rigs (namespace `…Persistence.Surreal`). The swap and Hold seeds (quotes, consent, API keys, reserve,
  mandates) are hundreds of lines; duplicating them would drift. Interleaved tests are prefixed `Interleaved…`.
- `KycBeginInterleavingTests.cs`, `OnRampLotInterleavingTests.cs` are standalone on `InterleavingDatabase` (per-class
  namespace + real generated goldens).

## Seams

| Fixture | Contenders | Serialization row | Winner / loser outcome |
|---|---|---|---|
| allowance claim vs old-op authorize/cancel | `AuthorizeStageAsync` ∥ `CancelUnsentStageAsync` (old op) ∥ `ClaimAsync` (new op) | old `swap_operation` (claim's no-op lock vs `CommitStageAsync`) | authorized ⇒ old op keeps the lane, re-claim refused; cancelled ⇒ re-claim hands over |
| exposure vs withdrawal | `AuthorizeStageAsync` ∥ `WithdrawSubmissionAsync` on an exposed stage | operation + stage versions | authorized ⇒ later withdrawal only stops replay; withdrawn ⇒ authorization refused forever |
| Hold source consumption vs revocation | `CommitExchangeOpeningAsync` ∥ grant / API-key `revoked_at` CAS | grant / API-key no-op lock in `ExchangeAuthorityFence` | committed ⇒ one liability, receipt replay survives; revoked ⇒ source stays Admitted, Held 0, replay refused |
| Hold mandate first egress vs revocation | `AuthorizeSendAsync` ∥ `RevokeAsync` | mandate row (fence no-op vs revoke CAS) | revoke always lands; authorize either recorded its third use under the pre-revocation mandate or stays refused |
| concurrent KYC begin | 4× `CreateSubmissionAsync`, one subject scope | `kyc_authority_guard` attempt row | one attempt, losers `Conflict`, only the winner's documents persist |
| on-ramp lot double claim | 4× `ClaimAsync` one intent; 4× `UpdateIfVersionAsync` one version | deterministic lot id; `version` CAS | one lot / one draw; losers see the winner's row and watermark |

The revocation contender in the exchange fixture is the typed `revoked_at` CAS that every revocation path ends in,
retried with `SurrealTransientConflict` like a real writer.
