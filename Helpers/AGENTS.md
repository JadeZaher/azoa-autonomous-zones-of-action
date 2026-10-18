# Shared helpers

`UnsignedIntegerText` parses canonical nonnegative decimal strings into BigInteger
without narrowing; signs, whitespace, leading zeroes, fractional/exponent forms
and non-ASCII numerals are invalid. Providers separately impose uint64/uint256
limits. NativeOperationAuthorization.Amount delegates to this shared grammar.

`CanonicalJson` defines application intent encoding v1: recursively ordinal-sorted object keys, preserved array order and numeric tokens, JSON-escaped decoded strings, UTF-8 without whitespace. Duplicate keys and undefined values are rejected. Distinct numeric spellings intentionally produce distinct hashes. `HashCanonical` hashes persisted exact bytes and must not be used as a substitute for validating an input was canonical. This is an application contract, not an assertion of RFC 8785 compatibility.

Helpers in this directory are pure, reusable application-boundary conversions;
domain policy stays in its owning manager or service. `SurrealRecordGuid`
normalizes either a bare GUID/hex id or a `table:id` link so manager response
mappers do not maintain private copies of transport parsing. `BareId` also
normalizes SurrealDB's backtick/double-quoted textual-id rendering before it is
used in a typed record comparison or GUID conversion.

`NftHolonFactory` is the shared pure mapping for guarded NFT metadata writes.
It contains no KYC, governance, fee, persistence, or operation policy; callers
must make those decisions before persisting its result.

`WalletBootstrapIdentity` is the shared canonical chain normalizer and
SHA-256-derived wallet id used by both wallet creation and tenant status reads.
Changing its v1 input is an identity migration, not a formatting refactor.

## BCL names

Never give a helper type the short name of a BCL type (`Encoding`, `Encoder`,
`Convert`, `Path`, `File`, `Random`, …). The byte/hex helper was once called
`Encoding`; every file importing both `AZOA.WebAPI.Helpers` and `System.Text`
then failed with CS0104, and inside this namespace `Encoding.UTF8` silently bound
to the helper (CS0117). It is `ByteEncoding` since round 4. Third-party clashes
(`Algorand.Utils.Encoder` vs `System.Text.Encoder`) use a file-local alias
(`using AlgorandEncoder = Algorand.Utils.Encoder;`), never a renamed BCL import.

## §rail-amount-json

`RailAmountBaseUnitStringJsonConverter` is the HTTP/replay boundary converter for
base-unit amounts. It always writes a decimal JSON string so JavaScript clients
never round values above `Number.MAX_SAFE_INTEGER`; numeric reads remain accepted
only for compatibility with already-materialized internal payloads, and only up to
64 bits — a producer that emitted a larger JSON *number* has already lost
precision in its own number type, so the string form stays the contract.

It replaced `UlongDecimalStringJsonConverter` in the D13 widening (`Core/AGENTS.md`
§rail-amount). The wire form is byte-identical, so no client saw a change; only the
in-process type widened from `ulong` to `RailAmount`. The old converter was deleted
rather than kept, because a converter for the retired type invites a new amount
field to be typed `ulong` again.

## Development provider aliases

`DevelopmentProviderSecrets` loads only explicit allowlisted names from the ignored content-root
`.env`, and only under Development. Existing nonblank configuration wins. The Stripe DEV alias
must contain an sk_test_ key; live aliases and provider enablement/environment flags are never
read. Canonical optional workflow/callback settings are allowlisted individually. No values are
logged. `TenantAuthority` is the shared active-owner/provisioning-credential check used by the
KYC control plane and KYB services; unexpected storage exceptions reach the host boundary.

## Hosted lease transaction guard

`HostedSessionLeaseFence` contributes a write-lock plus assertion to a caller's
multi-record transaction. It must always run between BEGIN and COMMIT together
with the protected domain mutation. Its raw statement is retained for that atomic
invariant, not ordinary CRUD: a lease read followed by a separate domain write
would allow an expired owner to approve after takeover. The database evaluates
expiry using its own time and the assertion aborts the whole transaction on loss.

## Hosted create and callback alias fences

HostedKycCreationFence runs within the send-start transaction. It write-locks the
unexpired pending attempt and checks tenant selection/provider trust revisions.
Shared authority guard writes serialize it with authority retirement. Node-default
configuration is process configuration, checked by KycManager's resolved policy.
HostedSessionAliasFence reserves deterministic provider/flow/alias records in the
same transaction as attachment, including legacy-row collision checks. Parent and
child bind distinct parameters; Combine is flat and merges parameter maps without
renaming. These raw statements implement cross-record atomic invariants.

## NFT authorization and identity

`NftLifecycleIdentity` partitions application request keys by authenticated tenant, actor and operation; it never invents blockchain IDs. `NftFeeQuoteIdentity` hashes the complete immutable native-fee terms, and `NftTransactionAuthorization` checks logical actor/issuer, stage, preparation generation, final-effect fee placement and explicit zero quotes before provider signing. `NftBindingPermissions` treats explicit map values as authoritative, including false denial, and named read/write/admin levels as exact permissions rather than an implicit privilege hierarchy.

## Chain address identity

NFT ownership and wallet matching use ChainAddressIdentity: Ethereum 20-byte hexadecimal addresses compare independently of checksum casing; all other encodings remain ordinal. Returned ownership must name the expected address; asset existence alone does not grant authority. Stored signed intents and provider-derived canonical asset identifiers remain immutable.

## Identity evidence serialization

`KycEvidenceFence.Create` belongs inside a flat multi-record transaction before
reading the latest subject evidence. It writes the existing
`kyc_authority_guard:attempt-{user}-{tenant|direct}` record, shared with submission
creation, approval changes, identity binding/erasure and AML append/erasure.
Governance takes the same fence before selecting current proof, preventing a new
HIT or rejected submission from appearing between latest-proof selection and vote
commit. Multiple subjects use one `CreateScopes` call with sorted guard IDs;
combining multiple calls would collide on its parameter name. Typed record
mutations remain typed; the raw guard is retained only as part of the atomic
evidence/decision invariant. Null-subject screening records cannot authorize a
user and contribute no subject guard.

The guard is a single `UPSERT $_evidence_guards` over an array of
`SurrealRecordParam.Of("kyc_authority_guard", id)` links, sorted by guard ID so
concurrent writers lock in the same order. Two constraints shape it: (1) it must
stay **exactly one statement** — about fifteen callers read later results by
position (`GetValues(2)`, `statements.Count - 2`), so splitting it into one
statement per guard would silently shift every index; (2) it must not declare a
`FOR $var` (or closure `|$var|`) — SurrealForge's strict `Validate` exempts only
`LET $x =` targets, so a loop variable is reported as an unbound parameter and
the whole batch throws before reaching the database (fixed 2026-10-16).


## Person-operation claim mutation fence

PersonOperationClaimFence joins a typed claim create/update and a database-time cutoff guard in one flat transaction. A preceding typed snapshot only constructs the writer: exact existence/version/holder/release/cutoff and immutable domain are checked again inside the transaction, and the typed CAS repeats those predicates. Missing Version means legacy NONE only; literal NULL in authority fields is corrupt and cannot become an unowned claim. The guard rejects closed windows and exhausted/corrupt versions. Generic store callers must treat zero affected rows as conflict, never as successful acquisition or release. An unchanged incumbent is returned through an in-transaction typed read. Governance continues using its dedicated command store and generic APIs reject its namespace.

## Unsigned chain identifiers

UInt64Text validates canonical decimal text without signs, whitespace or leading zeroes, preserving the full uint64 range across JSON/JavaScript. Public AVM application/asset identities use strings; convert to ulong only at the provider wire boundary. Schema counts and box foreign-array offsets are bounded counts, not asset identities.
# Consent lifecycle identity

`ConsentLifecycleIdentity` normalizes immutable grant intent before hashing. Scope
order and duplicate spelling do not change the command; comma/control characters
cannot smuggle multiple CSV scopes. Participation retains the inbound credit floor
and rejects value-signing authority. Command keys bind the authenticated grantor and
original request ID; intent hashes must be compared on replay. Transition IDs bind
grant and transition kind, while the body hash additionally binds the exact persisted
transition time and original grant receipt. A different timestamp at the same ID is
a conflict, not another lifecycle event. Store transactions enforce that distinction.

ConsentNotificationEvidence compares immutable consent transition evidence with the full original notification identity, canonical body, business time and destination. Notification delivery never grants authority or reconstructs an accepted transition from current mutable grants.

## NFT protocol cost receipts

NftProtocolCostContract binds an immutable server-issued receipt, including original expiry, actor/tenant, physical network, payer wallet/address, lifecycle/draft, stage/generation, standard and canonical provider evidence. Network fee, retained rent and spent levy are independent limits; surplus in one cannot subsidize another. IsAuthorized checks exact historical acceptance only: fresh lifecycle admission must separately fence receipt expiry at database time. Never regenerate expiry when comparing an accepted quote.

NftLifecycleIntentFactory is the shared pure construction path for NFT protocol cost quotation and preparation. It preserves signer/fee-payer separation, recipient stages, tenant scopes and original authorization fields; the cost receipt is attached only to Create.

NftWorkflowCostAcceptanceIdentity partitions client acceptance keys by actor/tenant/exact execution ID and hashes the expected version, original frozen intent and exact cost authorization. Its structural binding checks do not replace current database authority fences. Original workflow and actual accepting credential witnesses are retained separately.

## Role-bound dApp policies

`HasDappDevelopAccess` / `HasDappManageAccess` (`ClaimsPrincipalExtensions`) are the bodies of the
`DappDevelop` / `DappManage` policies in `Program.cs`. The caller's dApp role is always required; a
`dapp:develop` or `dapp:manage` scope alone never admits (RV-L4-01). Per principal:

- *Login JWT:* the `dapp_role` claim stamped at login (its dApp scopes are implied by that role).
- *API key:* the owner's live role (re-read per request by `ApiKeyAuthenticationHandler`) and, for a
  scoped key, the explicit scope; an all-forbidden CSV (`ScopesRestricted`) is denied.
- *Delegated (child credential or `act_as_tenant`):* the delegated scope AND the role read from the
  `azoa-live-role` identity that `DelegatedChildCredentialGate` adds after loading the user from the store.
  A `dapp_role` claim inside the token itself is ignored, so neither a tenant nor a forged claim can
  confer the role.

`AzoaDappRoles.GrantsScope` is the single role-to-scope rule; API-key self-issuance, child-credential
issuance and the gate all use it.

## Username rules

`UsernameRules` is the single username alphabet: 3-50 characters of letters, digits, `_` and `-`.
Node-generated handles (`wallet-…`, `oauth-…`, `onboard-…`) always used hyphens while the validators
allowed only `[a-zA-Z0-9_]`, so a wallet user could never save a profile (QA H4). Widening the alphabet
would have let anyone register a generated handle ahead of its owner — the wallet and OAuth handles are
deterministic from public inputs — and break that person's first sign-in at the `user_username` UNIQUE
index. So the generated prefixes are reserved: registration, tenant provisioning and a profile RENAME
refuse them, while a user who already holds a generated handle may keep resending it.
