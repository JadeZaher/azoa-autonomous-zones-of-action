# Solana provider

## WTT authoritative state

The WTT state module reads core config, guardian membership, token config, foreign
endpoint, both loader-v3 program/program-data pairs and Clock in one finalized
`getMultipleAccounts` context. Program-data hashes include the entire allocated
code region after the loader header, matching the existing native artifact hash.
Core/token config PDA seeds, guardian big-endian index seed, endpoint big-endian
chain seed and little-endian account layouts follow Wormhole commit
`bb5957f321b0a9d168f52e906699ee77baa82a28`; captured SDK account sources live in
`.omc/research/round3-wtt-*`. Clock is exactly 40 bytes, with the snapshot slot and
signed Unix time checked before narrowing to the protocol's uint32 time range.
The exact context block hash is retained; missing archival block evidence fails
closed. RPC blockTime estimates never substitute for the program's Clock time.

Configuration is explicit `AZOA:Solana:Wtt:Networks:<Network>`: `Enabled`,
`CoreBridge`, `TokenBridge`, `CoreProgramDataHash`, `TokenBridgeProgramDataHash`.
Existing network genesis pinning also applies. No configured guardian membership
or foreign-emitter list substitutes for actual owned program state. The supplied
source emitter is only an endpoint PDA seed: the owned account must attest to
that exact chain/address. This module supplies evidence to centralized WTT proof
verification; it does not itself verify signatures or claim token settlement.

## DEX route arguments

DEX argument decoding is separate from signing authorization. `JupiterRouteCodec`
accepts only the reviewed single-hop Whirlpool subset from
`jup-ag/jupiter-amm-implementation` commit
`cc068c9d1df0060c62f9a8a4fc37ea13ea7b9b39`. Integration must independently verify
fixed/remaining accounts, compiled privileges, mints, owners, pool and lookup-table
state. AMM-interface enum ordinals and abbreviated instruction-parser headers are
not wire authority. Whirlpool V2 accepts no extra accounts or one supplemental
tick-array slice (AccountsType 6, at most three arrays), following Orca commit
`cc683ec92acb5e7606caa3d8e514d9383e9c7c11`. Hooks, unknown variants, noncanonical
booleans/options, split routes and trailing bytes are rejected. The accepted
contract and source links are in the closeout track's `round3-dex-contract.md`;
this codec alone does not advertise settled DEX execution.

## Signing and custody

The input to `SolanaTransactionSigner` is canonical serialized Solnet `Message`
bytes. `ITransactionSigner.Sign` accepts exactly one required signer and returns the
complete signed wire transaction. `IPartialTransactionSigner.SignPartial` returns a
detached Ed25519 signature only for the declared required public key, verifies it,
and rejects noncanonical message bytes. Provider assembly requires exactly the
expected participant keys and verifies every signature and the complete envelope.

All durable operations resolve keys through consent-aware custody, including
platform signing with `SigningContext.ChainType = Solana`; they have no plaintext
configuration fallback. A per-user failure cannot use platform custody. Solnet owns
Ed25519, base58, PDA derivation and transaction framing. Source helpers never export
private keys; custody owns buffer zeroing. Existing direct SPL APIs retain their
older development-only platform fallback, without changing the durable contract.

The production provider resolves scoped custody through `IServiceScopeFactory`.
The explicit handler/custody constructor is the test seam. Do not capture a scoped
custody object in the factory's long-lived network provider.

## Durable transaction lifecycle

`SolanaProvider.Durable.cs` validates RPC genesis against the bound public-network
identity. A local validator requires the explicit provider-owned override
`AZOA:Solana:Networks:<Network>:GenesisHash`. The default public hashes come from
Solana's `sdk/src/genesis_config.rs`, captured under `.omc/research/`.

A prepared operation contains complete signed bytes, the actual first signature as
transaction ID, and provider-owned recovery metadata: genesis, blockhash, context
slot, last-valid block height, deterministic account inputs and intent digest.
Before submission/observation the module rebuilds the exact expected instruction
list and compares the canonical message, signatures and wire serialization. The
intent digest is signed in a Memo instruction, including preparation generation.
No additional transactions or instructions are accepted. Final NFT effect and
nonzero native platform fee are instructions in the same transaction.

Observation requires finalized history with the exact stored wire transaction.
A finalized failed transaction proves no token effect, but network fees may still
have been charged. Missing history, including an expired blockhash, remains
`NeedsOperator` for NFT or `Unknown` for value; it never authorizes a replacement or
unit compensation. Only identical persisted bytes can be submitted again while
valid. The value module supports native SOL and classic SPL Token transfers, with
actual mint/ATA ownership and balance reads before preparation.

## NFT standards and frozen credentials

Normal NFTs create a zero-decimal single-supply mint, immutable Metaplex metadata,
and a master edition with maximum print supply zero. Credential assets deliberately
use another standard, `SPLFrozenCredential`: immutable metadata, removed mint
authority, platform freeze authority and the logical issuer as verified creator.
Prepared evidence and lifecycle authority retain the actual contextual standard.
`AZOA:Solana:CredentialAuthorityAddress` must match the platform custody public key.

Credential acceptance is recipient-signed ATA creation/issuer-delegate approval
and platform-signed freeze in one transaction. Delivery thaws, transfers and
refreezes atomically. Revocation additionally requires the logical issuer's actual
signature as delegate. The official SPL processor rejects approving or revoking a
delegate while the account is frozen. Delivery checks exact owner/mint/frozen state,
zero destination balance and issuer delegate amount. Closing/recreating an empty
accepted ATA does not bypass this: a stale prepared delivery fails its thaw
instruction, and a new preparation requires acceptance again. A holder cannot
move a frozen credential through the normal transfer path.

Ownership reads check token-program ownership, initialized zero-decimal supply-one
mint, immutable metadata/verified creator, normal master-edition authorities or
credential freeze/mint-authority constraints, and the actual ATA balance/state.
Application authority additionally enforces immutable logical issuer and revocation.

## Codec provenance

`SolanaNftMetadata` independently encodes the Borsh wire fields documented by the official Metaplex generated
`create_metadata_account_v3.rs`, `create_master_edition_v3.rs` and `accounts/metadata.rs`
layouts. Program/discriminator/account ordering and field evidence are captured in
`.omc/research/solana-metadata-codec-source-2026-10-15.md` and
`solana-metadata-account-genesis-2026-10-15.md`; upstream is
https://github.com/metaplex-foundation/mpl-token-metadata (Metaplex NFT Open Source License v1.0; client manifest 5.1.2-alpha.2).
No upstream implementation source was copied. The captured layout files are the revision evidence; GitHub commit API access was unavailable in this environment. No curve, signature, base58 or transaction-frame codec is implemented locally.

Removing mint authority uses the official SPL instruction bytes `[6,0,0]`, from
`interface/src/instruction.rs` (`SetAuthority`, `MintTokens`, `COption::None`). This
avoids Solnet's null-authority builder generating a random ignored public key and
therefore changing retry bytes. The official source is captured in
`.omc/research/spl-token-option-codec-2026-10-15.md`.

## Evidence and remaining surfaces

New module and signer tests are authored but unexecuted until the integrated sweep.
Local protocol tests execute real SPL freeze/delegate/account-close rules and real
Metaplex minting through the provider, using the pinned local validator setup in
`tests/AZOA.WebAPI.IntegrationTests/Fixtures/Solana/AGENTS.md`. Mock RPC tests establish
orchestration assertions only; no live-devnet execution is claimed.

The durable NFT capability creates metadata and destination ATAs. Standalone
`ISolanaMetaplexModule` and `ISolanaSPLModule` now inherit the typed native
quote/prepare/submit/observe capability. Account artifacts support actual metadata
creation/update, ATA creation and token-account close or explicit wrapped-SOL
unwrapping through the deployment journal. Old synchronous mutation signatures
were removed because they could not represent caller authority or pending recovery.
Bridge primitives and direct DEX exchange/swap remain required work tracked by the
full-goal assessment. No live-devnet execution is claimed.

## Native operations and execution-position evidence

`INativeOperationModule` uses explicit typed loader-v3 ELF, instruction, or SPL/Metaplex account artifacts. The quote includes every bounded stage and conservative rent reservation. `native_preparation` claims the full unsigned stage plan before custody; retries retain its genesis, blockhash, validity height, canonical message, and signed candidate. Preparation never authorizes submission. Every signer is an actual user custody context; conflicting roles sharing a key are rejected.

`SolanaNativeLoader` encodes official loader-v3 bincode discriminants and seeded account addresses. Public address seeds use SystemProgram.CreateAccountWithSeed and still require the base authority signature; they are not private-key seeds. ELF bytes are supplied artifacts, never host scripts. Writes are bounded to 512-byte chunks. Deployment/upgrade preserve authority unless the request explicitly removes it. Upgrade capacity is the existing program-data allocation.

Mutable native code confirmation uses finalized program-data history and buffer allocation/write history, ordered by block transaction position and instruction sequence. A successful call is not evidence that the preflight code version ran. Program history reconstructs deployment, upgrade, extension and closure; buffer bytes are rebuilt from system allocation and exact loader writes. Missing history remains uncertain. The direct immutable-code shortcut requires deployment in an earlier slot. Agave v3.1.8 activates deployed code one slot later and zeroes upgrade padding; official sources are captured in `.omc/research/round2-solana-loader-processor-extract-2026-10-15.md` and the companion loader processor capture. RPC provenance is `.omc/research/round2-solana-program-history-2026-10-15.md`.

`SolanaNativeMetadata` encodes Metaplex DataV2 create/update from official generated instruction layouts captured in `.omc/research/round2-solana-metadata-operations-2026-10-15.md`. Updates resolve omitted fields from the quoted snapshot and disclose the complete DataV2 replacement. The snapshot hash is a pre-send check, not an on-chain compare-and-swap; concurrent updates by the same authority can be overwritten by the explicitly authorized replacement. ATA creation is idempotent and reserves rent even when an ATA currently exists. Close requires the actual close authority, exact refund destination, and zero token amount unless wrapped SOL unwrapping is explicitly authorized.

These native source paths and orchestration tests are authored, not runtime-verified. Legacy standalone entrypoint migration and local protocol fixtures are still in this implementation lane; the earlier remaining-surface section must be reconciled at handoff.

Partial loader failure cleanup and separately quoted linked recovery after fee-cap exhaustion remain required Round3 work. Current multi-stage failure handling is not a completed recovery journey; do not infer released rent or safe restart from a failed stage.

Native loader quotes disclose a separately priced CloseBuffer fallback outside the successful stage sequence. Its original authority and refund destination are frozen in the quote; normal stage selection never executes it. Recovery orchestration must prove all prior authorized wires resolved before enabling this branch. Native receipts separately project confirmed BufferFunded/Deployed/Upgraded/AuthorityFinalized effects, preserving economic truth when a later stage fails. Buffer creation observations identify the actual buffer, not the future program.

## Token metadata reads

Token metadata reads use finalized mint supply and the derived Metaplex metadata
PDA. Missing supply or decimals is an error, not zero. An absent metadata account
is disclosed separately from mint existence. Present metadata must have the actual
Metaplex owner, non-executable account role and exact requested mint before its
name, URI, update authority and mutability are returned. URI values are on-chain
claims; this read does not fetch or attest off-chain content. Unexpected transport
failures and cancellation propagate to the centralized boundary.

## WTT instruction framing

WttSolanaInstructionData follows the pinned Wormhole Solitaire coder at commit bb5957f321b0a9d168f52e906699ee77baa82a28: one-byte discriminator, little-endian nonce/amount/fee/target-chain and exact 32-byte recipient. Transfer amounts retain full uint64 width. Attest, complete-native, complete-wrapped and create-wrapped use their actual protocol discriminators. This codec does not establish account roles, custody, source finality or redemption; the provider must derive and verify the complete required account list and bind the resulting message to its durable journal before signing. Official source capture is round3-wtt-sdk-js-src-solana-tokenBridge-coder-instruction-ts-2026-10-15.md.
WTT source and target inspection reads both pinned executable/program-data pairs, core/config accounts, mint, wrapped metadata, Metaplex metadata and requested holding in one finalized account snapshot. Legacy SPL layouts are exact; Token-2022 extensions are not silently interpreted as legacy SPL. Wrapped origin and precision must match the bridge PDA and mint signer. This inspection is read evidence, not guardian verification, first-send authority or settlement.

WttSolanaAccounts follows the pinned Rust token-bridge instructions.rs account order and flags (captures round3-wtt-solana-token-instructions and its tail). Core and SPL dependency program accounts follow that source order. WttSigning accepts only the real payer plus explicitly bound protected stage keys, rejects role collisions, and verifies every signature against the same compiled message. Protected seeds never become caller-visible or derive from public operation IDs. RFC8032 vector coverage is authored; provider end-to-end execution remains pending.

## Versioned message envelopes and lookup tables

`SolanaMessageEnvelope` adds version-zero framing absent from the pinned Solnet
message builder. Solnet still compiles the original instruction/account privileges;
the shared helper removes only unsigned, non-invoked keys into validated tables,
remaps every instruction index, and preserves required signer order. It rejects
unknown versions, noncanonical short-u16 aliases, trailing bytes, invalid headers,
dynamic program IDs and duplicate resolved accounts. Ed25519/base58 remain Solnet
operations. Legacy and version-zero detached signatures cover the complete exact
message. The signer does not grant authority over resolved lookup accounts: each
provider's closed builder must reconstruct the expected instructions and resolve
them against persisted table snapshots before custody.

Lookup snapshots retain actual owner, complete account bytes and finalized slot.
The reader implements the official 56-byte metadata layout and same-slot extension
prefix rule, rejects deactivating tables for new preparation, and never replaces a
saved snapshot during replay. A current pre-send read compares the exact used
resolved addresses; unrelated append-only extensions do not change an old message.
Deactivation/closure cannot be interpreted as absent economic effect. Official
Solana v1.18.26 message/ALT/short-vector sources are captured in
`round3-solana-v0-alt-authority-2026-10-15.md` and
`round3-solana-v0-alt-activation-2026-10-15.md`. Local actual-runtime v0, lookup
activation and full guarded-packet conformance remain required final-sweep gates.

## Guarded WTT source publication

Source quotes freeze the actual immutable guard, core/token executable hashes,
exact protocol fee and Rent bytes, source asset/holding and complete table bytes.
Read-only simulation measures the concrete v0 message before custody. The policy
supplies a maximum compute limit and explicit micro-lamport price; the quote freezes
the measured limit with a bounded margin. Publication and cleanup network fees,
protocol native value and retained account rents remain separate reservations.
Sequence rent is conservatively reserved for each publication; an earlier
attestation may make that amount an actual recoverable bridge-payer residual.

The closed atomic source message puts the guard after ComputeBudget, then fixed
funding, optional explicit native wrapping, actual-owner approval, real protocol
publication and delegate revocation. An existing unrelated delegate is refused.
The protected bridge payer never substitutes for the wrapped token's actual owner.
The provider declares `IWttSourceModule`; first-send authority, body ownership and
residual cleanup admission live in the WTT operation journal (Services/Wormhole/AGENTS.md
§Operation journal). `Enabled` configuration and the protocol gates still decide activation.

Reliable publication reads follow the pinned `msg` account layout. The canonical
guardian timestamp is `submission_time`, confirmed by official watcher client.go
line1180; finality is processor `Finalized` -> publication byte32. These captures
are `round3-wtt-solana-publication-authority-2026-10-15.md` and
`round3-wtt-solana-watcher-timestamp-2026-10-15.md`. Source proof requires exact
finalized wire/canonical block/actual fee, saved loaded-key resolution, expected
payload and token debit/custody balances. Later successful writes to the reliable
message account invalidate current-account publication proof. Paginated finalized
address history is inspected back to the original transaction; incomplete history
does not authorize a replacement. Confirmed publication retains actual protected
payer balance as a separate cleanup resource, never an assumed refund.

Core target verification uses the pinned secp256k1 offset table and the actual
instruction position after guard/funding, not the SDK helper's position-zero
assumption. Each verification instruction immediately follows its secp instruction.
Signature sets bind the single Keccak body hash, guardian index and canonical
boolean vector; PostedVAA PDAs also use the single hash. Cross-chain replay identity
continues to use the separately defined double-Keccak body digest. Posted account
evidence distinguishes the original VAA timestamp from the target submission clock.
The target account codecs require exact framing and body fields; provider ownership,
PDA, finalized state and journal correlation remain separate required checks.
Sources are captured in `round3-wtt-solana-core-instructions-2026-10-15.md`,
`round3-wtt-solana-core-post-2026-10-15.md` and
`round3-wtt-solana-posted-account-2026-10-15.md`.

The same pinned token-bridge revision supports both the original 32-byte Config
and the migrated 137-byte pause-role tail (`round3-wtt-solana-token-types-2026-10-15.md`).
Both bind their first 32 bytes to the core. Canonical paused state remains readable
for guardian reconciliation, while quote/preparation and the execution guard refuse
it. An expired pause timer does not clear the flag; the protocol requires an actual
unpause instruction. Truncated, oversized and noncanonical pause layouts are refused.
Solana transfer payload recipients are token-account addresses, not wallet addresses:
target inspection derives the exact ATA and supplies its protocol bytes separately
from the recipient wallet used for custody/participant consent.

## Jupiter Whirlpool account grammar

`WhirlpoolAccountState` reads the exact pinned 653-byte pool and fixed or dynamic
tick-array layouts. Pool identity includes configuration, ordered mint pair,
actual fee-tier seed and stored bump. Tick arrays bind their pool, canonical
start index and derived PDA; dynamic tags must match their 88-bit initialization
bitmap and exact serialized length. Account ownership, executable status and
finalized RPC context are checked by the caller before these pure decoders.
This helper does not authenticate an entire Jupiter route or declare settlement.
Sources are Orca revision `cc683ec92acb5e7606caa3d8e514d9383e9c7c11`, captured in
`round3-orca-programs-whirlpool-src-state-whirlpool.rs.md`,
`round3-orca-fixed_tick_array-2026-10-15.md`,
`round3-orca-dynamic_tick_array-2026-10-15.md` and associated tick/allocation
captures under `.omc/research`. The complete route and compiled privilege union
remain required before provider integration.

`JupiterRouteAccounts` binds the current nine/thirteen fixed account headers to
the one-hop Whirlpool CPI layout, including the explicit AMM program prefix
from Jupiter's `to_dex_account_metas`. It returns token-owner and privilege roles
for subsequent authenticated account and compiled-message checks; those roles
are not themselves RPC evidence. `JupiterBuildPlanCodec` requires the official
v2 Build shape, one complete Whirlpool hop, exact canonical uint64 amounts,
zero aggregator fee and agreement between quote metadata and decoded route
bytes. Unknown instruction-bearing fields, tips, cleanup or v1 semantics refuse.
Its parsed setup/compute instructions and lookup map still require complete
provider validation before compilation. Source: Jupiter implementation
`cc068c9d1df0060c62f9a8a4fc37ea13ea7b9b39` and docs
`74418054200bedb5303865c8fd7a6b1a08b6075d`; captures include the current IDLs,
Build OpenAPI and `round3-jupiter-amm-prefix-2026-10-15.md`.

`JupiterRouteAuthority` checks the route instruction's duplicate-account
privilege union and reads token roles from actual legacy SPL-owned,
nonexecutable account envelopes. Frozen accounts, delegated spending, conflicting
mint/owner roles and foreign close authority refuse. Missing setup accounts are
not treated as existing accounts; the later setup planner must prove their exact
creation separately. The complete compiled-message union also includes trusted
setup, explicit fee transfer and native-account lifecycle instructions, and must
be validated before signing; the route-only helper does not replace that gate.

## Shared SPL decoding and registration costs

`SolanaSplAssetCodec` owns initialized legacy SPL mint/holding decoding for WTT and
DEX. WTT-specific wrapped origin metadata remains in `WttSolanaAssetCodec`.
`WttSolanaMetadataPolicy` names reviewed Metaplex revision
353d01be4af3018e1daf49d0cd35ba9f731d9c9c. Its 607-byte metadata rent is distinct from
the creation levy, minimum-balance(1308)+5440. Exact immutable source captures are
`.omc/research/round3-wtt-metadata-pinned-*-2026-10-15.md`. The descriptor alone is
not executable provenance; admission must match a reviewed code-hash catalogue,
and actual deployed local protocol evidence remains a required gate. Registration
uses closed v2 cost guard; other stages retain exact v1. No capability enabled.

## WTT target stage receipts

Target plans bind the complete transfer envelope and optional original-chain
attestation. A currently posted exact core-owned body can satisfy posting after
old guardian expiry; fresh posting still revalidates current guardian/emitter
state. Both paths require actual finalized-head freshness. Recipient protocol
bytes identify the derived SPL token account, not its wallet owner.

Quotes reserve explicit maximum compute and actual message fees for all ordered
stages. Only the currently executable stage can be simulated before prerequisite
accounts exist; each exact later stage is simulated before new custody use.
Verify batches share one protected body/guardian/generation signature-set key;
Post references that public account without acquiring signing authority. Each
stage has a separate protected payer and explicit cleanup reserve. Quote admission
expiry and durable multi-stage consent still require the operation-journal seam.

`WttFinality` shares exact signed wire, loaded account resolution, repeated final
receipt, unique canonical block inclusion and mandatory fee checks across source
and target. Target evidence separately proves verification bitmap, immutable
posted body, registered mint origin/authority, or actual redemption claim and
recipient/relayer/custody token changes. Confirmed verification/posting is an
intermediate stage, not overall bridge settlement. Recipient-owned native unwrap
and protected-payer cleanup remain explicit stages driven by the journal; the provider
declares `IWttTargetModule`, `IWttCleanupModule` and `IWttNativeOutputModule`.

Whirlpool Oracle decoding follows the same pinned Orca revision's packed `Oracle` (254 bytes), adaptive constants (34 bytes), and variables (44 bytes). It binds the derived Oracle PDA and stored pool, validates the protocol's constant bounds and trading timestamps against the finalized Clock, and rejects nonzero reserved bytes as an unreviewed layout. RPC ownership/executable checks and same-context collection remain the caller's responsibility. The original source is captured in `.omc/research/round3-orca-oracle-2026-10-15.md`; this decoder is not an independent price quote or completed route authorization.

## Native metadata cost policy

SolanaMetadataCreationPolicy is shared by WTT registration and native metadata creation. Native metadata requires independent NativeMetadata:Networks:<network> CostGuardProgram, CostGuardProgramDataHash and MetadataProgramDataHash, plus a unique matching NativeMetadata:ReviewedExecutables CodeHash/SourceRevision catalogue entry under AZOA:Solana. No default pin is trusted. Finalized guard/program/Rent/Clock evidence is checked before quoting and preparation; a version3 closed guard pins metadata executable and exact Rent under execution account locks. Retained rent uses the supported 607-byte account size; the non-refundable levy is Rent.minimum_balance(1308)+5440 and consumes the native Value allowance separately from locked rent and network fees. Old stages omit nullable ProtocolLevy; their zero-value behavior remains unchanged. NFT create integration and actual guard runtime/packet fixtures remain required.

Jupiter setup accepts only the pinned ATA interface's opcode 1 and exact six account roles for the original owner's input/output ATA. The source is associated-token-account revision 06b93de62562a86eb0a5da8164a40fd06fbd4f74, captured in round3-ata-instruction-2026-10-15.md. Compute accepts only exact limit/price opcodes with no account authority or duplicate override; full message privilege union and explicit provider-selected limits remain mandatory. SolanaComputeBudget shares unchanged encoding and simulation framing with WTT.

JupiterCompiledMessage compares the entire resolved message against provider-reviewed instructions, including payer, signer/write union, exact instruction order/data/accounts and packet size. It never treats an HTTP Build instruction list as reviewed by itself. JupiterNativeAccount derives a distinct seeded WSOL account from the immutable preparation key, funds and initializes it, and closes only that derived account to the owner. The planner must prove absence and bind rent/funding to the persisted quote; compilation must keep its lifecycle atomic or journal prerequisites explicitly. Official SPL instruction source is pinned at c666f1dc61f38dd0a9c0fcdeaeac4778274f7116 (round3-spl-native-lifecycle-2026-10-15.md).

## Durable packet admission

SolanaPacketBudget accounts for the short-vector signature count and every 64-byte signature before custody. SignDurableAsync rejects oversized legacy envelopes before any key callback; NativeMessageFeeAsync uses the same provider-owned calculation before fee RPC. Existing signed-wire packet validation remains defense in depth. Boundary and actual signing-seam no-custody fixtures cover this path.

The Jupiter public quote uses explicit native asset ID SOL (nine decimals); the canonical wrapped mint address remains a distinct SPL asset. Quote issuance validates actual same-context mints/holdings/pool/ticks/Oracle/programs, immutable guard readiness, ALT evidence, exact message fee and simulation. Temporary WSOL address/key is frozen in quote evidence and disclosure; because a quote is consumed once, its account key belongs to that operation and is reused only after authoritative no-effect proof. Stage preparation must use that saved nativeAccountKey, not replace the disclosed account with a new random address. Actual provider module registration remains gated on the complete signed lifecycle and protocol evidence.

## NFT creation cost offers

QuoteNftProtocolCostsAsync reads the separately configured NFT guard and metadata catalogue, verifies the physical genesis, and prices the complete guarded unsigned creation message without custody. AZOA:Solana:Nft:Networks:<network> owns CostGuardProgram, CostGuardProgramDataHash and MetadataProgramDataHash; AZOA:Solana:Nft:ReviewedMetadataExecutables owns CodeHash/SourceRevision. Account rent (mint, initial ATA, metadata and ordinary master edition), permanent metadata levy and network fee remain separate. The immutable source policy at revision 353d01be4af3018e1daf49d0cd35ba9f731d9c9c allocates master editions at 20 bytes, not the older padded size; source capture is .omc/research/round3-nft-master-edition-policy-2026-10-15.md. Credential creation omits edition rent. NftCostEvidence binds the exact creation intent excluding its enclosing quote, deterministic account addresses, guard, metadata policy and Rent. Optional recovery-plan evidence omits null to preserve older wire identity. Preparation enforcement and public receipt issuance must use this same evidence; a read-only quote itself grants no signing permission.

### NFT creation preparation enforcement

New Create preparation requires the admitted server receipt. It rereads the guard/metadata/Rent evidence and matches actor, tenant, payer wallet/address, lifecycle, generation, physical network, token standard, immutable intent and account allocations before custody. Actual message fees are checked independently before signing and again before sending the exact saved message. Receipt expiry governs fresh domain admission, not historical prepared recovery. Old saved plans with no cost evidence and no quote still validate their original signed bytes; new quoted plans must reconstruct the exact guard and costs. The local protocol fixture reads independent executable pins through standard environment configuration and obtains a real provider quote; absence of deployment/pins fails the gate rather than substituting a policy.

JupiterAcceptedRoute preserves the original quoted-output integer and slippage trailer, asserting that the decoded on-chain floor equals the accepted minimum exactly; fresh route accounts do not replace economic consent. JupiterSavedPlan retains raw account/ALT evidence and reconstructs the full message before custody, browser acceptance or submission. New account effects must occur in the original quote disclosures. Submission replays only the original signature and message, and expired validity returns the original identity for observation rather than proving absence. RPC rejection or response loss remains ambiguous. These source paths remain unadvertised until finalized receipt/history and protocol gates are complete.

Every Jupiter message includes a fixed-width domain-separated Memo commitment. Quote estimation uses the frozen quote execution key; durable preparation replaces only the fixed-width commitment with the exact operation/stage/generation preparation-key hash. This preserves packet/fee estimate width while making distinct operations and replacement generations distinct signed messages even if the RPC supplies the same recent blockhash. Native account derivation remains bound to the original disclosed quote key. Quote admission also atomically consumes the quote once, but message identity does not rely only on that store invariant.

Jupiter expiry observation uses full finalized block ancestry rather than address-index absence. Each bounded pass checks every transaction in eight complete blocks, follows decreasing parent slots with exact previous-block hashes and contiguous produced block heights, and persists its plan/genesis-bound cursor through the stage observation. Only traversal below the original context slot after an upper block beyond the last-valid height permits ExpiredAbsent. Missing historical blocks or transaction arrays never imply absence. Positive address history only recovers actual signatures. The shared Solana finality helper retains exact wire, unique canonical block inclusion, repeated receipt and saved ALT resolution checks for WTT and DEX.

## WTT residual return

Cleanup offers reobserve the original saved source/target transaction and repeat exact finalized wire/block/fee proof. Original refundable allocation is the positive protected-payer post-minus-pre delta, bounded by the original deposit and exact funding less permanent protocol costs. Original precredits and later unsolicited deposits may be returned but never counted as original rent recovery. An unexplained lower current balance is ambiguous. Each offer freezes the original observation hash and exact return amount; credits after quotation remain explicitly reported in RemainingStageBalance.

Cleanup preparation uses a fresh direct original-wallet WttCleanupSign context and the existing protected payer envelope only. It never creates a replacement stage key or revives the original workflow. A separate canonical request/generation Memo and durable unsigned winner precede both signatures; signed replay precedes mutable price/expiry checks. Publication-message and verification-signature-set keys cannot use cleanup authority. Missing expired transaction history is still unresolved. The operation journal claims cleanup only while every original attempt is terminal, authorizes its first send under fresh direct-owner `wtt:cleanup:sign` authority and restores the operation's prior state afterwards. Zero residual is a read-only result, not a fabricated cleanup transaction.

## Recipient native output

Native-output quotation reobserves exact original Redeem finality and binds its receipt hash, mint, net recipient amount and persistent ATA. It requires original UnwrapNative intent, native mint origin, fresh recipient funding, exact Rent and the independently pinned immutable guard. Actual unsigned simulation and fee RPC price the whole closed instruction sequence. A distinct domain-separated seeded token account receives only the accepted net amount and is atomically closed to the recipient; the original ATA remains open. The shared SolanaSeededNativeAccount preserves Jupiter's original fixed domain through its thin wrapper.

An exposed, never-authorized recipient message is observed by `ObserveExposedWttNativeOutputAsync`: once a finalized block past the message's `LastValidBlockHeight` exists, it walks finalized parent links back to the message's context slot (the reviewed Jupiter ancestry scan, reusing `ReadJupiterHistoryBlockAsync`/`JupiterBlockHeader`, eight blocks per pass with a resumable `wtt-solana-message-scan/1` cursor) comparing exact message bytes. A match is the recipient's own signed wire and re-enters acceptance (`Landed`); a complete scan is the `wtt-solana-message-absence/1` proof (`ExpiredNoEffect`); missing block history keeps it pending. Recipient preparation persists one canonical unsigned winner without custody and accepts only its real recipient signature. Original-token redemption does not grant target-payer authority over this wallet. The journal records exposure before disclosing the message, requires fresh recipient cost ceilings, allows no competing generation while exposure is unresolved, and separately authorizes first egress. Submitted recovery validates exact saved bytes, finality, persistent ATA token/native debit, recipient net SOL credit and complete temporary-rent return; missing expired history remains unresolved. Only an originally requested native-output operation waits for this stage; settlement then records both the redemption and the native output.

## Address roles

`SolanaAddress` is the single address-admission helper (`TryParsePublicKey` delegates to it). `TryParse` accepts only canonical base58 that decodes to exactly 32 bytes and re-encodes to the same text. Two role predicates sit on top:

- `IsSigner` — the key must produce an Ed25519 signature, so it must lie on the curve. Program-derived addresses (PDAs) are deliberately off-curve and have no private key; admitting one as a signer would only fail later inside custody or simulation, so it is refused at the boundary.
- `IsAccount` — any 32-byte key. Recipients, token accounts, mints, programs and refund destinations may legitimately be PDAs (vaults, ATAs, program-owned wallets).

| Call site | Field | Rule | Why |
|---|---|---|---|
| `SolanaProvider.DexQuote.cs` `BuildJupiterQuoteAsync` | `Owner.Address` (owner = payer = recipient) | signer | signs the swap transaction as fee payer and transfer authority |
| same | `PlatformFee.TreasuryAddress` | account | fee destination owner; only its derived ATA receives value |
| same | `CostGuardProgram` (config) | account | executable program id |
| `JupiterAsset` | SPL mint id | account | mint |
| `SolanaProvider.NativeAccounts.cs` CloseTokenAccount | `TokenAccount`, `Destination` | account | closed account and lamport destination never sign |
| `SolanaProvider.NativeArtifact.cs` `ValidateArtifactAsync` | instruction `ProgramId`, every instruction account | account | instruction signers are separately matched to custody authorities in `PlanSolanaNativeAsync` |
| same | account artifact `Mint`, `Owner` | account | an ATA owner may be a PDA |
| `TrySolanaNativeIntent` | `Payer`, `Authority`, every `Authorities[].Address` | signer | each resolves to user custody and signs |
| same | `RentRecipient` | account | lamport destination |
| `SolanaProvider.WttSourceQuote.cs` | `SourceAuthority.Address` | signer | source wallet signs approval/funding |
| `SolanaProvider.WttTargetProof.cs` | `TargetAuthority.Address` | signer | target payer signs post/redeem stages |
| same | `RecipientAddress` | account | the transfer credits the derived ATA; the recipient signs only the optional native-output stage, which custody gates separately |

A new call site picks the rule by asking "does this key sign?" — never by the field name.
