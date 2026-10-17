# Interfaces — contract documentation

Interfaces are the authoritative home for XML API contracts. Document parameter
identity, idempotency/concurrency semantics, result meaning, and cancellation on
the interface member. Implementations use `/// <inheritdoc/>`; do not duplicate
contract prose that will drift.

Use `<inheritdoc cref="..."/>` only when a member intentionally implements or
adapts a differently named contract. Public implementations may add one terse
local note for implementation-specific behavior, with broader rationale in the
nearest directory `AGENTS.md`.

Reusable pure transformations belong in `Helpers/` or the owning reusable
package. A private static helper may remain beside one implementation only when
it is tiny, single-purpose, and has no second call site. On the second use, move
it instead of copying it.

## Tenant custodial account boundary

`ITenantCustodialAccountManager` accepts the tenant id only from the
authenticated controller claim and an opaque external subject. Its account and
KYC DTOs are deliberate allowlists: no signing material, wallet ciphertext,
document reference, reviewer field, or provider payload is part of the contract.
The underlying `ITenantManager`, `IWalletManager`, and `IKycManager` remain the
authoritative aggregates. The idempotency key is tenant-partitioned and hashed
before persistence; reusing it for another external subject is a conflict.

## Real-value KYC gate

`IRealValueKycGate` is deliberately stricter than the general profile-status
projection: a real-value authorization requires the latest ledger approval to
carry an explicit future expiry. This isolates the economic boundary from legacy
or non-value flows that may still represent an approval with no expiry.

## NFT transfer reservation contract

`IHolonStore` reservation methods form a conditional ownership workflow:

- reserve succeeds for one source-owned NFT and is idempotent only for the same
  settlement key and target;
- finalize moves ownership only for the matching active reservation and replays
  the same completed settlement as success;
- release clears only the matching source reservation and never moves ownership;
- `Result == false` is an expected contention/fingerprint conflict, while an
  unexpected storage failure is an exception observed at the boundary.

## Node fee settlement contract

`INodeFeeSettlementStore` persists an immutable intent plus an inert recovery
lease protocol. The deterministic identity is a hash derived from the parent
idempotency key and fee operation; exact immutable replays return the existing
row, while a changed economic decision conflicts. A recognized concurrent
duplicate create must be reread and compared, so identical decisions normalize
to replay rather than an arbitrary duplicate-key error.

Recovery is a bounded candidate scan followed by per-record conditional CAS. A claim
requires the scanned `StateVersion` and a due-or-expired lease predicate; it
issues a fresh opaque token and expiry. An exact live lease may either park a
mixed observation containing `Unknown`/`Failed` effects in
`AwaitingReconciliation`, leaving the parent `InProgress`, or supply two
distinct confirmed effect references to atomically
settle both rows. The latter is a persistence protocol only: it is not a
provider submission or worker activation seam. Stale leases, illegal state
transitions, a mismatched parent, and any terminal reversal return `false`
without mutating either paired row.
## §value-rail — IValueRailProvider and why IBlockchainProvider extends it

`IValueRailProvider` carries the rail-generic surface: move value (`TransferAsync`), read a
balance, ask what became of an operation (`GetTransactionStatusAsync` /
`GetTransactionConfirmationAsync`), declare a `SettlementProfile`, and bind to one
environment (`InitializeRail`). `IBlockchainProvider : IValueRailProvider` adds everything
chain-shaped — `Initialize`, `TryGetModule`, contract deploy/call, the faucet, and token
metadata. It deliberately does NOT carry the bridge primitives: those live on the
`IBridgeSourceLeg` / `IBridgeTargetLeg` capability modules (see
`Providers/Blockchain/AGENTS.md` §bridge).

**Depend on the narrower one where you can.** Taking `IBlockchainProvider` asserts the
counterparty is a chain, which a fiat rail is not, and that assertion is what made a fiat
rail impossible to introduce before (see `Providers/Blockchain/AGENTS.md`
§value-rail-factory).

Two parameter names read chain-flavoured and are kept verbatim so no named-argument call
site breaks. Read them rail-generically: `tokenId` is "which asset", null meaning the rail's
own unit of account; `txHash` is "the reference this rail gave us for the movement".
`ChainConfirmation` is likewise a normalized landed / failed / in-flight / unknown verdict
that is rail-generic despite its name. Renaming all three is a mechanical follow-up with no
behaviour change; it was left out of the seam change to keep the diff reviewable.

`SettlementProfile` and `RailAmount` live in `Core` — see `Core/AGENTS.md`
§settlement-profile and §rail-amount.

`IActionNodeHandler.ResolveSettlementRailId` declares which rail a node settles on, read from
the node's DECLARED config. See `Services/Actions/AGENTS.md` §rail-capability.

## Person-uniqueness gate and its claim ledger (D3/D23)

`IPersonUniquenessGate` is the per-operation one-human-one-claim opt-in. Three properties
are contract, not implementation, and every implementor and caller must preserve them:

- **Opt-in by declaration.** An operation opts in by passing a
  `PersonUniquenessRequirement`; no requirement means no check runs at all. A gate that
  ran by default would silently one-per-person every operation in the system.
- **Refusal is never an error.** Every denial is the SAME two-valued answer carrying the
  SAME sentence: `Success(KycEligibilityDecision.Ineligible(cause))`. Unavailability is a
  denial too. A 503 that appears only for already-claimed people is the correlation leak
  wearing a hat, so `IsError` must never be used to mean "refused".
- **`ReleaseAsync` is the compensation.** A claim taken for a side effect that then failed
  must be released, or that human is locked out of the operation forever.

The interface is marked FROZEN because a sibling lane codes against these signatures
verbatim; extend it by widening `PersonUniquenessRequirement`, not by adding a method.

`IPersonOperationClaimStore` is **node-operator-only persistence** — no consumer path may
reach it. Two shapes carry its safety:

- `TryClaimAsync` decides the winner INSIDE one statement on a deterministic record id,
  and the caller decides refusal by comparing the returned `UserId` to its own. A
  preceding read lets two concurrent claims both pass; this follows the `BindUserAsync`
  precedent for the same reason.
- Reads are keyed by `(scope, operation)` and nothing else, so a caller cannot ask this
  store about a person it names and the read cannot be turned into a person-key oracle.
  A store fault is an error result, never an empty list — an empty list is a published
  tally of zero.

Person keys live only in this store. See `Services/Kyc/AGENTS.md` §person-uniqueness and
`Persistence/SurrealDb/Models/AGENTS.md` §person-operation-claim.

## Credential revocation contracts (D19)

`ICredentialManager` states the rule the whole rail turns on in the contract itself:
**authorization is the recorded ISSUER, never the holder.** The two tempting sources are
both wrong and named as such — `CurrentOwner` is the party the asset is taken FROM, and
a mint's `operation_log.user_id` is the tenant that proxied it. A row with no recorded
issuer is unrevocable and fails closed.

`CredentialRevokeCodes.PendingConfirmation` lives beside that contract deliberately: the
manager and every transport that maps it (HTTP status, SDK branching) name the same
constant instead of two string literals that drift. A pending clawback carries
`IsError = true` because `IsError` is what AZOA client helpers branch on.

`ICredentialIssuerStore` is a second, deliberately NARROW face on the store that
implements `INftStore`. It exists because `IUserNFT` has no issuer property and its only
writable carrier is the caller-supplied attributes bag — routing the issuer through that
bag would make the authorization decision forgeable by the holder. `SetIssuerUserIdAsync`
is write-once (re-pointing an issuer would hand revocation authority to another party;
re-stamping the same one succeeds so an idempotent mint retry does not fail), and it is
the writer that makes revocation possible at all: a mint that skips it produces a
credential nobody on earth can revoke.

**The node type is currently blocked** by `Services/Actions/NotEnabledActionNodeTypes`;
these contracts are implemented and gated, not live. See `Services/Actions/AGENTS.md`
§not-enabled-node-types.

## Analytics read surface (D16)

`IAnalyticsReadService` is a read projection over `operation_log` that **writes nothing
and prices nothing**. If a change here needs a write, the change is wrong.

Its request type, `AnalyticsQueryRequest`, deliberately has no user-id field: both routes
read the subject from the credential, so a cross-user view cannot be smuggled in through
model binding. A node-operator cross-user view belongs on its own policy-gated route —
never as an optional parameter here, because then the safe call and the privileged call
are one request apart. Rationale for the three-way `Pricing` / `AmountStatus`
representations is in `Services/Analytics/AGENTS.md`.

## Detached signatures

`IPartialTransactionSigner` is an optional capability on a registered signer, not a
second custody route. It accepts canonical provider-owned message bytes and a
declared required public key. Implementations verify key/message membership and the
resulting signature. Providers compose only their exact expected signer set through
consent-aware custody, then verify the complete transaction. Existing `Sign` keeps
its complete-wire return contract. Solana requires this for issuer/platform/fee-payer
combinations without replacing the logical actor with platform authority.

DEX observation continuation is provider-owned evidence in the existing persisted stage observation. The coordinator supplies it directly to both signed and externally exposed observation methods; no API accepts a history cursor or evidence override. Providers validate the cursor against the original plan/genesis before advancing bounded history work. Algorand retains its direct indexer proof and ignores this optional continuation. See the closeout track's round3-dex-solana-exposure-amendment.md for finalized block coverage.
