# Algorand provider notes

## Wormhole WTT storage foundation

The new WTT provider work uses the protocol storage template and LocalBlob from
Wormhole commit `bb5957f321b0a9d168f52e906699ee77baa82a28`, captured in
`.omc/research/round3-wtt-algorand-{template,localblob}-2026-10-15.md`.
`WttAlgorandStorage` preserves all fifteen 127-byte pages, including zero pages;
guardian expiry occupies absolute offset 1000. Account evidence binds the exact
derived address, application rekey authority and requested round. Guardian keys
must be distinct and fit before the expiry field. Template integers use full-width
unsigned LEB128, and the address hashes the actual program-domain bytes.
This pure decoder does not establish genesis, application bytecode identity,
snapshot freshness, guardian signatures, transaction execution or redemption.
Full WTT source and target adapters remain required; the legacy bridge interfaces
described below are not substitutes for Wormhole publication or settlement.

`WttAlgorandGroup.Seal` assigns each member's exact pooled fee (`Transaction.Fee`),
never `SetFee`, which clamps to the 1000 µAlgo minimum: stateless verifier and
undelegated protocol members must stay at fee zero so the frozen plan, its txids
and the payer's pooled fee cap are exact. A decoded zero-fee member reads back as
`Fee == null` (MessagePack omits zero); compare built plans, not decoded ones.

An exposed but never-authorized recipient opt-in is observed through
`ObserveExposedWttParticipantAsync`: a landed txid recovers the recipient's exact
single signature from the indexer and re-enters the ordinary acceptance path; an
absent txid past `lastValid` with the finalized expiry block is the
`wormhole-algorand-exhausted-absence/1` proof (Services/Wormhole/AGENTS.md
§Operation exits).

The read-only WTT guardian adapter brackets application, storage and block reads
with one unchanged finalized Algod round. It validates both approval/clear hashes,
physical genesis, core reference, binary foreign-emitter registration and exact
guardian account; moving rounds require a fresh read. Freshness/cryptographic
verification remains in the common verifier. This does not advertise source or
target transaction capability.

`WttAlgorandSourceWire` follows the pinned token-bridge processor: message fee is
immediately before the asset transfer for `sendTransfer`, and immediately before
`attestToken` for attestation. Storage creation/ASA opt-in are separate proven
prerequisites. Both methods publish nonce zero; the pinned guardian watcher emits
consistency zero and the finalized block timestamp. Attestation caps ASA decimals
at eight, while transfer normalization uses actual ASA decimals. Wrapped ASA
returns transfer to canonical origin storage; they do not destroy the entire ASA.
Closed wire reconstruction and actual Ed25519 verification reject substituted
members, signatures, fees, rekeys and hidden trailing envelopes. This foundation
still requires quote/admission, prerequisite staging, custody, submission and
protocol receipt integration before activation.

ASA inspection preserves full-width IDs and raw binary metadata. The source
processor classifies a wrapped asset using its creator's current rekey authority;
the adapter additionally proves canonical origin storage, the exact recorded ASA,
registered origin emitter, fixed supply and bridge manager/freeze/clawback roles.
Original ASA attestation decimals are capped at eight while transfer normalization
retains actual ASA decimals. Original ASA freeze/clawback authority remains part of
its immutable behavior descriptor; it is not represented as an immutable token.
Every creator read is bracketed by the same finalized round as application state.

`WttAlgorandStorageWire` binds the protocol's 1,002,000 microALGO seed deposit to
one template-derived storage account and the exact application opt-in/rekey. The
payer signs only the funding member; the opt-in carries the exact undelegated
template program with no logic arguments or auxiliary signatures. Canonical
whole-envelope equality prevents a substituted logic program or signing mode.
The provider must separately establish absent storage, current funding/rent
readiness and durable first-send authorization; the wire helper grants none.

## Bridge capability — SOURCE only, and that asymmetry is deliberate

`AlgorandProvider` implements `IBridgeSourceLeg` and **not** `IBridgeTargetLeg`.

That is the honest shape of this chain today, and it is the shape a single `SupportsBridging`
boolean could not express: the boolean was false, which suppressed Algorand's genuine outbound
capability entirely. Capability is now structural — a chain that cannot do something simply
does not implement the module, so absence IS the fail-closed refusal. There is no boolean to
set true by mistake and no refusal body left that could regress into a fabricated `Ok`.

### What Algorand CAN do, and how each capability is PROVEN

`LockAsync` and `ReleaseAsync` broadcast real, signed transactions and return real transaction
ids. Every precondition is read from chain state before anything is signed; none is assumed,
and an unreadable fact refuses rather than guessing.

- **Lock** — proves the VAULT is opted into the ASA (Algod account read). Algorand rejects an
  asset transfer to an account that has not opted in; broadcasting anyway would burn a fee and
  let a caller read the submitted tx id as evidence the value moved.
- **Release, proof 1 — vault control.** The vault must BE the platform signing account, and
  its Algod `auth-addr` must be absent or equal to it. A rekeyed vault is signed by a
  *different* key than this provider holds, so same-address reasoning there would be false and
  the broadcast would be unauthorised.
- **Release, proof 2 — vault balance.** The vault's holding of the ASA is read and must cover
  the requested amount and must not be frozen.
- **Release, proof 3 — recipient opt-in.** The recipient must have opted in and be unfrozen.
  The platform *cannot* opt in on the recipient's behalf — the account must sign its own
  opt-in — so the only safe primitive is verify-and-refuse.
- **Pending ≠ settled.** A broadcast that does not confirm inside the poll window returns
  `BridgeLock.PendingConfirmation = true` with the real tx id. That is an explicit field rather
  than a message marker precisely so it cannot be lost to a reworded message, and so a slow but
  valid lock is never recorded as failed (which would invite a double broadcast).

`BurnAsync` (`AssetDestroy`) proves, before signing, that the platform is the asset **creator**
and holds **every single unit** — the exact precondition Algorand enforces. Previously this was
assumed; a destroy attempted against outstanding supply is rejected on chain, and reporting the
attempt as a burn would claim value was destroyed that still exists in other accounts.

### What Algorand CANNOT do, and why there is no code claiming otherwise

No canonical platform-controlled **wrapped ASA** lifecycle exists. A safe wrapped mint needs a
canonical wrapped asset resolved once per source asset (creating a fresh ASA per mint is the
broken model this provider previously had), then a recipient opt-in that the platform cannot
perform, then a transfer — and its paired destroy needs the full-supply proof above. Until that
lifecycle is designed and reviewed, `IBridgeTargetLeg` is **not implemented**, so there is no
wrapped mint or burn on this provider to fabricate a success with.

### Tests

`BridgePrimitivesTests` covers the four focused cases this file demands — confirmed submission,
pending-confirmation recovery, ownership/opt-in proof, and failure without fabricated success —
plus module presence/absence:

- `Algorand_lock_broadcasts_real_asset_transfer_to_an_opted_in_vault`
- `Algorand_lock_reports_a_broadcast_but_unconfirmed_transaction_as_pending`
- `Algorand_release_refuses_when_the_recipient_has_not_opted_in`
- `Algorand_release_refuses_when_the_vault_holds_less_than_the_requested_amount`
- `Algorand_release_refuses_when_the_vault_is_not_the_release_signer`
- `Algorand_release_refuses_when_the_vault_is_rekeyed_away_from_the_release_signer`
- `Algorand_release_broadcasts_a_real_transfer_once_every_precondition_is_proven`
- `Algorand_destroy_refuses_when_the_platform_does_not_hold_the_full_supply`
- `Algorand_destroy_refuses_when_the_platform_is_not_the_asset_creator`
- `Algorand_ships_a_source_leg_and_deliberately_no_target_leg`

Do not implement `IBridgeTargetLeg` until wrapped mint and burn have the same standard of
proof: confirmed submission, pending-confirmation recovery, ownership/opt-in, and failure
without fabricated success.

## Tinyman fixed-input arithmetic

Tinyman fixed-input arithmetic lives in `TinymanV2Math`, following validator
commit `3de3a35e1ce0ca96576d18c2728f6042d1a07f72`: current pool fee share and
protocol ratio, wide intermediate multiplication, and the deliberate extra unit
subtracted from output even on exact division. It refuses zero-fee/output cases
and uint64 reserve overflow. Callers still verify pool state, accumulated protocol
fee overflow, genesis and actual settlement. The helper is not discovery or
execution; see the closeout track's `round3-dex-contract.md` for the full contract.

## Prepared value transfers

`AlgorandProvider.ValueTransfer.cs` supports crash-safe restitution. Preparation
requires an indexer, binds asset/sender/recipient/quantity and network genesis,
and signs without sending. Callers persist the immutable wire before submission.
The module rejects clawback/close/rekey/group semantics. Confirmation requires
exact indexed transaction semantics and a same-genesis confirmed block; finalized
absence is accepted only beyond the signed validity window with caught-up indexer
evidence. Missing indexer configuration refuses preparation before broadcast.

## Durable NFT transactions

`INftTransactionModule` owns Algorand ASA creation, opt-in, delivery, transfer, return-for-burn, destruction and credential clawback. The application persists exact signed envelopes before submission. ASA contract identity is `ASA`; protocol identity includes genesis hash. Recovery compares exact transaction identity and decoded semantics, rejects rekey/close/clawback substitution, and requires canonical re-encoding of each envelope so trailing MessagePack objects cannot hide additional transfers.

Every NFT transaction note commits the full immutable intent, including logical issuer, authorization context and fee quote. A platform-signed credential stage groups an issuer-signed payment with the primary effect: zero-value self-payment for authorization, or the exact quoted treasury fee at the final effect. Both members commit the same note. Recipient opt-in uses the recipient's actual custody context. The primary confirmed transaction proves the complete canonical atomic group, not an unrelated fee payment. Mint/transfer/burn/revoke fees use the separate native NFT policy; no fungible percentage fee is reinterpreted.

Pending-node or indexer evidence never fabricates an asset identifier. Expired replacement requires an indexer caught up through the validity window and matching genesis block evidence. Missing evidence retains NeedsOperator and the original bytes. Burn after transfer first returns the ASA to its original creator; the creator must then authorize destruction of the complete supply. Frozen credential delivery/revocation uses actual clawback authority, and no arbitrary imported ownership row establishes the logical issuer.

## Native AVM application operations

Typed AVM artifacts carry compiled approval/clear programs, schemas, references and arguments. TEAL compilation uses Algod, never host scripts. Quotes account for bounded page/schema minimum balance and encoded transaction fees. The immutable native preparation journal wins before custody; exact genesis, rounds, intent note and wire survive retries.

Call completion additionally requires indexed application creation/update history at the confirmed call round and intra-round position. Input program hashes alone are not execution proof. Pagination must be exhausted; incomplete history and ambiguous inner mutation ordering preserve uncertainty. Expiry without effect requires caught-up indexed absence plus matching block genesis and no continuation token. Official indexer search contract is captured in `.omc/research/round2-avm-history-api-2026-10-15.md`.

Source and adversarial fixtures are authored; actual local Algod deployment/call proof remains part of the final integrated gate, not a claimed result.

## Tinyman quote evidence

TinymanPoolState validates the deterministic pool, SDK-derived validator rekey
address, full-width ordered asset pair, unique local keys, current fees/reserves,
unlocked state and accumulated protocol-fee overflow. The quote module requires
explicit `Dex:Tinyman:Networks:<network>` ValidatorAppId, ApprovalSha256,
ClearStateSha256 and GenesisHash plus a configured indexer. It verifies current
application code, genesis and pool snapshot freshness, then reads real metadata,
holdings and opt-in costs. No default public deployment or fee policy is invented.
Dependency failures have a distinct unavailable result for the API's environment
condition mapping. Read-only quote fixtures are not swap execution evidence.

Tinyman submission reconstructs and verifies the complete saved signed group,
checks its transaction identities and current genesis, and sends only the original
concatenated envelopes. An elapsed validity window causes no egress and requires
authoritative observation. A transport failure or mismatched node response after
send remains ambiguous; it never creates a new generation or proves failure.

Tinyman finalized observation verifies the whole outer group in one indexed block,
its exact member identities/order/fees/arguments, single pool-to-owner inner output,
and fixed-input amount/fee logs. Genesis and the saved immutable validator code
pins must agree; the reviewed Tinyman validator rejects update/delete. Complete
caught-up absence for every member through expiry is required before replacement.
An opt-in confirmation leaves incremental rent unknown. Separate reconciliation
reads the owner's account at the previous round and replays earlier transactions
in that block, including nested inner execution order, before assigning actual
rent. The pinned Indexer converter repeats the parent's intra-round offset for
inner transactions, so those offsets must not be globally deduplicated or sorted.
Unknown historical state retains the original full reservation. Official schema
and converter are captured under `.omc/research/round3-indexer-*2026-10-15.*`,
commit `f84ad3e337a2e6eb111ac548f21dc65df8cc5e70`.

The quote partial is not yet advertised as IDexTransactionModule. Complete
group construction, signatures, durable first-send and authoritative settlement
remain required before enabling the capability. Pinned SDK application-address
derivation is captured in `.omc/research/round3-algorand-sdk-Address.md`; validator
state/math provenance is `round3-tinyman-approval.md` in the same research folder.

## Durable fungible creation

The shared AlgorandSignedEnvelope helper enforces exact canonical framing and,
where single-key authority is required, verifies Ed25519 over the SDK's actual
transaction signing bytes. Delegated auth addresses, multisig and logic signatures
are excluded from that closed signing mode. Tinyman browser groups rebuild every
unsigned member from immutable parameters before checking signatures, so extra
effects, reordered members and changed fees cannot hide inside a valid signature.

`IFungibleCreationModule` uses the immutable native preparation journal with a
fungible domain key. The winning unsigned plan precedes custody; exact canonical
signed bytes and creator signature are checked before persistence and replay.
Only the outer lifecycle authorizes submission. Indexer creation receipts bind
all original ASA parameters and created index to the saved transaction, validity
window and genesis block; mutable latest asset state is not issuance provenance.
Expired replacement requires exhausted, caught-up indexed absence. Native costs
are separately quoted and explicitly accepted; NFT fee policy is unrelated.
Official protocol/Indexer passages are captured in
`.omc/research/round3-asa-targeted-schema-2026-10-15.md` and
`round3-asa-protocol-limits-2026-10-15.md`. Fixtures are authored, not yet executed.

AVM application and asset identifiers cross JSON/SDK boundaries as canonical unsigned decimal strings, including values above JavaScript's safe integer range. Conversion to UInt64 occurs only at the wire boundary. Compiler provenance distinguishes uploaded bytecode from server compilation. For source compilation, the configured Algod build, genesis and consensus evidence must agree before and after compilation; this is RPC-reported provenance, not a cryptographic attestation of the compiler executable. Caller compiler labels are discarded at fresh admission. Official schema is captured in .omc/research/round3-algod-full-openapi.json.

Validation is fresh artifact admission: compiler labels supplied by callers are discarded, including labels copied from a previous server response. Reuploading compiled bytes is explicitly uploaded-bytecode provenance and is stable on subsequent validation. Stored artifacts retain their original server-produced provenance through their immutable artifact receipt; reading or quoting that receipt does not run fresh admission.

## Tinyman cleanup and rent release

Tinyman cleanup has separately accepted current-zero-holding consent. Its zero
ASA transfer closes to the sender itself: pinned go-algorand apply code rejects
the operation if any positive holding remains, so it cannot sweep arriving units.
Preparation-time absence of intervening closes does not identify an incarnation.
Release accounting instead exhausts a fixed finalized range in persisted pages
of 25, walking outer and recursive inner order. Indexer inner effects are located
by root ID and index path, not invented inner IDs or repeated inner offsets.
An earlier external close owns the original allocation release even when the
holding was reopened before cleanup. Pinned apply source is
`.omc/research/round3-asa-close-pinned-2026-10-15.go`; Indexer converter and schema
are pinned at `f84ad3e337a2e6eb111ac548f21dc65df8cc5e70` in the research directory.

## Durable generic value transfer

New Algorand value preparation requires a stable PreparationKey and claims one canonical unsigned NativePreparation plan before custody. The full intent hash is committed in the transaction note. Concurrent preparations may sign only that same plan; the stored signed candidate wins once. Submission requires exact saved signed bytes and plan identity, canonical single-envelope framing and valid single-sender Ed25519 authority. Historical no-key records allow observation only, never fresh preparation or submission.

Value confirmation binds the indexed transfer semantics, immutable intent note, fee/validity interval and genesis. Malformed JSON types or unexhausted Indexer pagination cannot establish final absence. Only complete caught-up evidence beyond the original validity window allows no-effect classification. Hold-specific cost quotation/account acceptance and the B2 domain remain in progress; this adapter repair alone does not complete the Hold feature.

ValueCustodyQuote reads the exact ASA parameters and sender/recipient/holding accounts without custody calls. It requires unfrozen holdings, exact source units, no foreign account rekey and manager/freeze/clawback authority absent or held by the declared custody account. The quote binds full asset parameters and physical genesis, separating transfer fee from recipient acceptance fee and added minimum balance. Official schema evidence is captured in `.omc/research/round3-algod-full-openapi.json`; the 100000 microAlgo per-asset minimum is recorded in the B1 protocol evidence. New budgeted transfers recheck current mechanics/readiness before claiming unsigned bytes and validate saved fee/protocol against the accepted budget. Quote expiry belongs to outer durable admission, not wire resumption.

IValueCustodyModule recipient acceptance uses the same NativePreparation journal and custody signer, with a distinct keyed stage. Only a resolvable user context with TransferSign may sign; platform substitution is refused. The recipient funds its network fee and added account minimum. Exact saved bytes may rebroadcast after outer first-send authorization; confirmation proves the exact zero self-transfer, note, validity, fee and physical genesis. A current opted-in balance alone never proves this operation confirmed. Source review and runtime gates remain pending, and Hold caller activation still depends on the full domain lifecycle.

## Wormhole Algorand source preparation

Source quotations inspect actual pinned apps, full-width ASA identity, exact storage roles and current fees at a bracketed round. Emitter storage permits only an unassigned zero blob or the core publication sequence layout; original-asset custody permits only the unassigned blob. The fixed protocol storage deposit and ASA custody opt-in deposit remain separate from network fees and transferred value. Shared WttAlgorandGroup sealing binds every member to genesis, validity, exact fee and preparation identity.

Source preparation claims the canonical unsigned group before custody and fills its signed candidate once. Recovery validates the original winner before consulting mutable prices; a sealed generation cannot return or submit its candidate. Template storage opt-in has the exact undelegated protocol logic signature, while payer members require real single-owner signatures. Submission revalidates the durable candidate and physical genesis and concatenates the unchanged signed envelopes. Transport ambiguity never establishes absence. The provider now declares `IWttSourceModule`; first send is authorized only by the WTT operation journal. Authored fixtures have not run.

WttAlgorandProgramHistory consumes complete application-filtered root transaction history, deduplicates repeated root matches, and replays nested application changes in execution order before the exact root/inner call. Pinned Indexer writer stores each inner application's actual creation/call ID; the query joins matches to the root transaction and the converter returns its full inner tree. Evidence is under `.omc/research/round3-indexer-app-query-find-2026-10-15.md`, `round3-indexer-app-history-writer-2026-10-15.md`, and the pinned converter capture. The caller must prove complete fixed-boundary pagination; partial history cannot establish executed code. WttAlgorandPublication extracts the exact unique core message but does not independently prove finalized group execution or active program identity.

Historical proof now advances one application-filtered page per observation through IWttEvidenceProgressStore. Query identity binds preparation, genesis, application, exact root/path/round/offset and both code hashes. The reducer retains mutations and the target, validates ascending root positions and repeated-root hashes, and completes only on exhausted pagination. Source observation checks the exact contiguous outer group, every called application's execution-position code, the unique inner publication and repeated finalized block evidence. Expiry requires caught-up, exhausted absence for every group member plus the original genesis's expiry block. Publication transaction identity is the actual token-app group member; the stage receipt retains the first group member ID.

WttAlgorandTargetWire uses the pinned SDK stateless verifier bytes and program-domain address, six signatures per verification call, pooled zero-fee verifier members and payer-signed verifyVAA. Registration follows verifyVAA/payment/two distinct nops/receiveAttest; redemption follows verifyVAA/completeTransfer with exact sequence, guardian, custody and recipient accounts. Canonical full-width ASA and sequence values never pass through floating point. The current target builder is a wire grammar, not authoritative guardian verification or a capability activation. Pinned SDK and processor captures use bb5957f321b0a9d168f52e906699ee77baa82a28; source filenames include round3-wtt-algorand-target-order and token_bridge-py. Target state integration and actual protocol fixtures remain required.

Target quotation re-verifies the VAA through the shared guardian verifier, binds the actual core verifier-program address and current guardian snapshot, and reads sequence bitmap state before reserving prerequisites. Integer bitmap bits use the pinned processor's least-significant-bit convention. Compatible prefunded but unallocated template addresses remain usable; foreign rekey or local state refuses. Wrapped registration, replay storage and recipient-owned opt-in have distinct deposits and signers. A frozen existing recipient holding cannot be repaired by advertising another opt-in. The quote is not send authorization; target preparation, finalized effects and durable admitted-offer continuation remain separate required integration work.

Target preparation persists the resolved registered ASA and unsigned group before custody. Saved signed recovery precedes mutable state reads; external recipient opt-in cannot use payer custody. Target submission shares the exact original broadcast helper with source submission. Target observation reuses physical finalized-group and execution-position code evidence, then validates actual inner creation or delivery, including exact recipient/relayer amounts and no close or extra effects. New wrapped metadata follows the processor's first-zero trim and eight-byte symbol limit. The provider declares `IWttTargetModule` and `IWttParticipantModule`; the journal records participant exposure before disclosure. Protocol fixtures remain required.
# Exchange groups

Exchange preparation journals the complete canonical unsigned group before custody,
then persists the winning signed envelopes. Submission only reuses that candidate.
The final reserve self-payment carries the bounded pooled fee; changing network
minimums cannot rewrite signed bytes. Confirmed and absent outcomes are distinct;
partial indexer history never permits projection or replacement. AlgorandGroupReceipt
is the unchanged shared finalized-group verifier extracted from WTT, including
contiguity and every expected outer transaction body. Exchange capability remains
unadvertised until its coordinator and source-review gates are complete.

## Application call identity

Algorand2 2.0.0.2024051911 declares `ApplicationId` on each on-completion subclass (`ApplicationNoopTransaction`, `ApplicationOptInTransaction`, …), not on the `ApplicationCallTransaction` base. `AlgorandApplicationCall.Id` is the one switch over those subclasses; a create or any unrecognized shape returns null, and every caller (`AlgorandGroupReceipt`, `TinymanSwapReceipt`, the two WTT observations) treats null as a mismatch or an unknown-application error. Never cast the base to a guessed subclass.

## Asset acceptance wire

`AssetAcceptTransaction` (zero-amount self opt-in) has no close-to field in the SDK. Acceptance validation relies on `AlgorandSignedEnvelope.DecodeCanonical`: a wire carrying `aclose` either decodes to a different subclass (rejected by the `is AssetAcceptTransaction` pattern) or loses the field on re-encode (rejected by the byte-exact round trip). The acceptance cost `Generation` is a `long` on the contract and an `int` on `native_preparation`; values outside `0..int.MaxValue` are refused before the claim, never truncated.

## Exact-bytes signing and zero-amount members (round 4)

The Algorand2 SDK decodes every zero-amount `axfer` as `AssetAcceptTransaction` and drops `aclose`, and omits zero
fields (`fee`, `amt`, `aamt`) on encode, so they decode as null. Therefore: `AlgorandTransactionSigner` signs the
provider's exact bytes (`"TX" || txn`) and frames the canonical `{sig, txn}` envelope instead of re-encoding a decoded
object; `AlgorandSignedEnvelope.MatchExact` verifies a signed envelope by re-encoding the *expected* transaction under the
submitted signature; receipt/group checks compare `Fee ?? 0`, `Amount ?? 0` and treat a zero-amount member's absent
`amount` as zero (`AlgorandGroupReceipt`).
