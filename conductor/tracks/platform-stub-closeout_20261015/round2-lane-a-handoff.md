---
type: implementation
---

# Round 2 lane A source handoff

Status: authored, independent source review in progress, runtime gates not run.

## Wallet authentication

WalletSignatureVerifier dispatches Ethereum EIP-191 and raw Ed25519 for Algorand
and Solana. WalletAuthIdentity validates and canonicalizes chain identity without
lowercasing Solana. Typed nonce/claim persistence and a durable five-slot challenge
budget replace scan-style raw queries. Account claims revoke both old credential
forms; dedicated conditional auth/role/activation mutations preserve authority
across stale profile saves. Logout watermark advancement is monotonic.

The resumable canonical identity migration gates wallet-binding writes until Ready,
renews its lease during bounded collision scans and exposes every conflicting owner
for deliberate operator repair. Repair uses exact identity fingerprints and migration
version. Root's separate source review closed the credential-retention, nullable
CAS, stale-profile and collision-order findings. Real Surreal fixtures are authored.

## Ethereum network identity

Each concrete Ethereum network configuration now supplies ChainId. A positive
decimal pin is required before RPC/signing; chain-level MetadataChainId no longer
provides fallback authority. Local deployment tooling writes Devnet.ChainId 31337;
base testnet/mainnet pins remain disabled with their networks. Pipeline tests were
updated and new no-egress missing/invalid pin coverage authored. Provider instances
reject cached identity changes, including the lock-acquisition race.

## Hosted identity providers

PreparedHostedKycProvider, HostedIdentityHttp and HostedProviderAttempt persist exact
request bytes before egress. Generic v1 has original-key attempt lookup/replay,
strict evidence schema, bounded webhook-key rotation and explicit policy/subject/
tenant correlation. Veriff implements documented create/decision HMAC contracts;
ambiguous creation is never repeated. Terminal candidate attachment requires
authoritative vendorData/endUserId correlation. A lost provider redirect is not
invented: recovered terminal sessions point to the original application return URL
with expired resume lifetime.

HostedIdentityRecovery and its worker advance retry scheduling before attempting
recovery, check current registry policy and use the database attempt/tenant fence.
Callbacks authenticate raw bounded bytes and persist minimized event/body-digest
receipts. Terminal identity/digest pinning precedes the shared KYC handler; a
conflict fences new domain completion without reversing an accepted outcome.
Provider-neutral person uniqueness and AML gates are unchanged.

The operator providers page now includes generalized hosted recovery. Its BFF has
an explicit path/body allowlist; mutations require recent node-operator auth and
audited evidence references. SDK methods cover wallet proofs/claims, hosted recovery
and Stripe inbox listing/replay. Existing Stripe replay UI/BFF was already present
from Round 1 and was retained.

## Evidence concurrency correction

See round2-evidence-fence.md. KYC, identity and AML mutators now share the existing
subject/tenant attempt guard with governance. Exact DB payloads use bounded
SurrealTransientConflict retry. Null-subject operator screenings remain ordinary
typed writes and cannot authorize a user. Identity membership erasure is conditional;
bounded retention erasure uses typed scoped mutations under all affected guards.
Root's concurrent AML/vote test was independently reviewed: it asserts coherent
receipt/effect behavior for either serial order and refusal after the HIT commits.

## Validation inventory

Authored coverage includes real EIP-191/Ed25519 signatures, retained credentials,
migration collisions/leases/nulls, nonce concurrency, no-egress Ethereum pin errors,
generic response loss with exact-key/byte replay, malformed/misattributed/expired
approval evidence, pending-field strictness, terminal contradiction, callback
authentication/key rotation, Veriff ambiguous create/foreign subject/unsigned
decision, real Surreal send lease recovery, callback digest binding, more-than-50
queue fairness, evidence guard concurrency and SDK transport parity.

No tests, build, typecheck, lint or schema generation have run. The final integrated
sweep must generate all new POCO schemas, migrate isolated fixture dependencies,
exercise the concrete provider contracts and validate UI/SDK compilation. Vendor
dashboard activation and real Identomat verification proof remain the precise
external setup items already recorded in deferred.md; synthetic fixtures do not
claim vendor activation. No new implementation-size deferral was introduced.

## Central registrations

Program registers prepared hosted recovery, same-instance NFT lifecycle/reconciler,
same-instance ActionManager/frozen runtime, action admission/local mutation stores,
deployment compiler/service/store/recovery/native preparation store, and the
deployment/governance/local mutation external-operation resolvers. Registration is
wiring only; peer source packages retain their separate review gates.

Coordinator independently accepted the final authenticated-callback retry correction by source: unprocessed authenticated triggers return 503; invalid opaque callbacks and completed duplicates return 202. The controller-level Veriff response-loss/retry regression remains unrun until the integrated sweep.
