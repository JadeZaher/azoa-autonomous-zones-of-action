# Authentication services

Wallet authentication accepts canonical Algorand, EIP-191 Ethereum and raw-message Solana proofs. Domain messages bind issuer, audience, canonical chain/address, nonce and exact millisecond UTC expiry. EVM signatures require canonical low-s secp256k1 values and recover to the requested identity; Solana base58 case is never folded.

`WalletAuthIdentityMigrationService` owns the monotonic persisted `v1` readiness gate. Missing is closed. Bounded ordered pages select auth-bound users with an absent/non-current `AuthIdentityVersion`, avoiding unsupported string record-ID comparisons. Each canonicalization and progress marker is committed with the live migration lease and exact original binding/tenant/watermark. A final transaction locks that lease and verifies the remaining workset is empty before publishing Ready. Ordinary binding writers cannot pass the gate before Ready, and Ready is never automatically reset.

Canonical collisions never grant access or choose an account. The private operator diagnosis includes fingerprints for every canonical-equivalent owner, collected in bounded pages while binding writes/deletes are gated. An operator may clear any diagnosed binding using the exact diagnostic fingerprint, migration version and an audit reason; repair atomically clears that binding, retains the audit and reopens migration. Stale diagnoses cannot clear newer identity authority. Unrelated profile data is preserved. Failed workers leave a durable lease which expires for bounded restart; infrastructure failures are logged only at the worker boundary.

The small raw transaction guards exist solely to atomically fence migration/user mutation, operator repair/audit and the readiness proof. Ordinary reads, admission and mutations use typed Surreal builders. Typed UPDATE ONLY returns one object or NONE, so its transactional assertion checks absent results rather than treating the object as an array.

Generic user profile saves preserve credentials, role and tenant authority. Wallet/claim changes use a dedicated observed-authority CAS; role assignment uses its own expected-role CAS; logout advances the watermark monotonically. A password claim clears any old wallet; a wallet claim replaces the prior password with an unguessable hash. This closes retained tenant credential access after ownership is severed.

## §claim-invites

`POST api/user/claim-invite` lets a tenant start the hand-over of a member account it provisioned. The
member, not the tenant, must end up holding the credential: a tenant that saw the raw token could
claim the account with a password it chose, log in as the person and grant itself `transfer:sign`.
Three rules close that (P5-19, track N1 of claim-and-consent):

- **Stored hashed.** `WalletAuthManager.CreateClaimInviteAsync` mints the token in memory and persists
  only `OpaqueTokenDigest.Compute(token)` (Helpers/AGENTS.md §opaque-token-digest). `ClaimAsync` looks the
  invite up by that digest, then `OpaqueTokenDigest.Matches` decides in constant time; legacy plaintext
  rows are refused. `SurrealWalletAuthClaimTokenStore` refuses to create or look up a non-digest value,
  and `SurrealUserStore.TryClaimAuthenticationAsync` refuses a non-digest invite, so no layer can
  quietly reintroduce plaintext. The Surreal column keeps its name `token` and its schema golden is
  unchanged (the stored value is still a non-empty string), which avoids a compatibility baseline +
  forward migration; its `[SurrealNote]` still says "short-TTL" and should be refreshed with the next
  real schema change to `wallet_auth_claim_token`.
- **Never returned to the tenant.** The response omits `claimToken` unless
  `Tenant:ClaimInvites:ReturnTokenToTenant` is true, which only `appsettings.IntegrationTest.json` sets
  (the HTTP flow tests drive the member side with it; `ClaimInviteOptionsTests` fails if any other
  committed appsettings file turns it on). Until member delivery (email link + hosted claim page) lands,
  a production invite is minted but undeliverable — that is intended: no delivery beats delivery to the
  wrong party.
- **Configurable lifetime.** `Tenant:ClaimInvites:Lifetime`, default 72 h (`3.00:00:00`), positive and at
  most 7 days. `ClaimInviteOptions` is a singleton bound from the final configuration and resolved once
  right after `builder.Build()`, so a bad value stops the boot with a message naming the key. Beware the
  .NET `TimeSpan` grammar: `72:00:00` parses as **72 days** (the first field overflows into days), so it
  is refused; write `3.00:00:00`.

Single-use and target binding are unchanged: `ClaimAsync` requires a live, unconsumed invite for the
exact `targetUserId`, and the claim transaction in `SurrealUserStore` consumes it (`consumed_at = NONE`
CAS) together with the ownership sever and consent revocations.
