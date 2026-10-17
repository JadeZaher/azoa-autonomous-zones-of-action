---
type: Design Amendment
title: Executable deployments and typed native operations
status: Accepted for staged authoring; independent source review and runtime gates pending
---

## Scope and ownership

Lane C owns bundle/series generation and deployment managers, a new deployment
service/store/controller/POCO package, typed native-operation contracts and provider
partials, standalone Solana metadata/token-account operations, associated SDK/UI,
and their tests. Staged implementation is in progress. Lane B owns ActionManager, action
engine/common pending contracts and frozen-run loading; Lane A owns central Program,
configuration, wallet authentication and hosted identity. Shared SDK additions will
be coordinated by section. No test, compiler, container or schema execution occurs
until the integrated sweep.

This completes existing provider families under the add-azoa-chain-provider skill.
Reuse the existing interpreter, composition validation, scoped custody, generated
typed persistence, provider-owned exact-wire verification, Ethereum nonce journal,
Solana metadata/PDA codec and Algorand signed-envelope logic. Do not reuse Ethereum's
legacy DeployContractAsync directly: it selects SigningContext.Platform and its
legacy transport creates a random preparation key.

## Public product and artifact contract

Generation request version 2 requires `deploymentTarget` with an explicit `kind`:

| Kind | Required generation input | Executable result |
| --- | --- | --- |
| PlatformRuntime | Ordered action definitions/revisions, mappings, bound references, execution chain/network context | Immutable executable graph snapshot plus a generated ES module loader |
| NativeContract | Chain/network and one typed artifact below | Validated native artifact with exact compiler/provenance descriptor and byte digest |

`TargetChain` alone is not a target discriminator. Legacy config-only GeneratedCode
is marked RequiresRegeneration, never accepted as deployed evidence. Existing UI
must explicitly select a target and regenerate. No migration silently turns a native
chain selection into platform activation. Historical fake DeploymentConfig remains
audit data but cannot make a version active/confirmed.

Generation produces `DeploymentArtifactV2`: artifactId/contentHash, formatVersion,
target, runtime/compiler descriptor, immutable executable content, reference manifest,
generation actor/tenant and provenance. Hash canonical UTF-8 JSON with deterministic
property ordering, exact binary hex/base64 and sorted unordered collections; exclude
timestamps/labels from executable identity. Include template version and generated
loader bytes. Byte-equivalent regeneration reuses content identity; different source,
target, budgets or authorities cannot reuse an admitted deployment request.

Deploy body requires artifactId/hash, idempotency key, explicit target, wallet/authority
selection and quoted limits for native effects. A targetOverride is applied BEFORE
generation/validation. On deploy it must exactly match the artifact target; otherwise
return RegenerationRequired with the requested target, not mutate signed meaning.
Expose generate/deploy/read/list/continue/cancel on the SDK and dashboard and migrate
both bundle and series controllers coherently. GeneratedCode becomes the actual loader
for platform artifacts or a display/export of the native artifact descriptor; it is
never advertised as compiled code when it is only configuration.

## Platform runtime is an invocable versioned registration

The generated dependency-free ES module exports `describe()` and
`invoke({baseUrl,deploymentId,accessToken,input,consent,idempotencyKey})`. It uses ordinary fetch
to POST `/api/deployments/{deploymentId}/invoke`, sends the expected artifact hash,
and returns the real durable run ID/status. The caller supplies deploymentId after
activation; the loader bytes embed only the already-known expected artifact payload
identity, and the server validates it against that registration. Artifact digest
uses a noncircular two-part envelope: payloadHash covers immutable graph/native content
and descriptors; loaderBytesHash covers the fixed template containing payloadHash;
artifactHash covers those two hashes and format version. No deployment ID or final
artifactHash is injected into already-hashed loader bytes. It does not eval uploaded scripts, embed
tokens, select a deployer wallet or deserialize JSON and call that execution. An
authenticated download route supplies the exact loader bytes whose digest is stored;
the SDK can invoke the same route. Loader generation uses a fixed reviewed template,
JSON string escaping and no remote CDN/compiler dependency.

Activation is an atomic durable registration from artifactId to a deployment ID,
owner/tenant, visibility, target, frozen snapshot/hash and Active status. It has no
transaction hash. A stable deployment ID is derived from actor/tenant/idempotency key;
the unique claim compares the full intent and conflicts on changed input. Registration
and bundle/series projection commit together (or projection is replayably repairable
from the canonical registration). Success requires the route to resolve this active
registration, not only a timestamp on the bundle.

The frozen snapshot contains every node/config/edge, ordered series entry, input/output
mapping, entry/terminal connection and source action ID/version/published hash.
Composition rewrites node IDs deterministically by stable occurrence path (ordered
series entry identity and nested ecosystem occurrence path), source action ID/node ID and
rewrites all edge/data bindings; repeated uses of one action have distinct node IDs.
Mappings that cannot uniquely identify a repeated occurrence must provide explicit
source/target occurrence paths. It validates one durable control successor and mapping
types using the existing DAG rules. A series invokes this composite graph, not only
its first action. Nested ecosystem bundles are flattened with cycle/depth/size limits
and immutable child artifact hashes; no mutable child pointer can change execution.
Limits: 64 source actions, 1,000 nodes, nesting depth 8, 2 MiB canonical artifact.
Existing unsupported graph structure returns an actionable validation error at generation.

Invocation authenticates the ACTUAL caller and tenant. Private registrations require
owner/authorized dApp access; public activation does not publish private source graphs
or Holon content. Recheck current source action publication/permission, source revision
hash, Holon/reference access and current tenant/delegated authority. A changed executable
source/reference causes VersionConflict and explicit regeneration, not latest-definition
execution. Revoked access stops future invocations. Runtime data reads remain governed
by ordinary current-caller permissions; the snapshot pins reference identity/config,
not a promise that mutable business data never changes.

Lane B exposes an INTERNAL frozen-run start seam using this verified snapshot and
registration binding, preserving current caller, tenant, consent, quotas, dependency
gates and durable enqueue. Its worker loads the saved run snapshot. No public API may
submit an arbitrary snapshot as trusted. Lane C owns snapshot creation/authorization
and calls that seam. Every economic node obtains the caller's current scoped custody
grant; deployment never delegates the publisher's wallet to invokers. Invocation
idempotency binds actor/tenant/deployment/hash/input/consent and returns the same run
after response loss. Input must satisfy a declared bounded schema; secrets never enter
public artifact/metadata. No fresh signature is created by a background observer.

## Shared durable operation and receipt

Agreed with lane B: `ActionExternalOperationReference` carries explicit Version=1, Domain, RequestKey,
OperationId, IntentHash, ActorUserId and ActingTenantId. Domain `deployment` has a
server-registered resolver owned by Lane C. Deterministic operation ID exists before
admission. Engine persists the reference before calling the domain; resolver uses
saved identity, never caller URLs or a new signing context. The full domain intent
binds artifact/target/network/authority/budgets and is compared on every replay.
Persist the exact canonical effective payload together with its hash before admission;
unknown versions/domains and changed actor/tenant/intent are rejected. Recovery never
reconstructs the request from a mutable latest node. Previously authorized exact-wire
observation remains distinct from current authority checks for new effects.

`DeploymentReceiptV2` distinguishes RuntimeActivation and NativeDeployment and exposes
operationId, artifactId/hash, target, state, stage, deploymentId/invokeUrl OR native
application/program/contract ID, transaction IDs, protocol identity, confirmations,
spent/reserved fee/rent and safe next action. States: Admitted, Preparing, Prepared,
Submitted, AwaitingAuthorization, NeedsOperator, Confirmed, Failed, Cancelled.
RuntimeActivation reaches Confirmed only after atomic registration. NativeDeployment
requires final chain code/authority evidence. No synthetic hash, no success on upload
alone, no Failed meaning an unknown send. A multi-step failure records completed
stages and remaining assets/rent; cancellation never implies refund of spent fees.

Domain store writes immutable intent before preparation, exact prepared stage wire
before submit CAS, stage/protocol/account identity and authoritative observations.
Worker observes/rebroadcasts already authorized bytes only. Authenticated `continue`
may prepare a fresh stage/generation after checking current authority and original
budget; absent consent yields AwaitingAuthorization. Unknown outcome retains exact
wire. Replacement requires provider proof of no effect, not time elapsed. Cancellation
is allowed only before any sent ambiguity/effect, or after explicit no-effect proof;
partly deployed assets require a separate authorized cleanup operation. Atomic stage
claims, generation CAS and total fee reservations fence overlapping continuations.

## Typed native module and common consent

Introduce a provider capability for typed native operations with Prepare/Submit/Observe
and explicit stage planning. Provider DTO union accepts only the owning family's
artifact/instruction contract. Generic orchestration knows IDs, hashes, state and
limits, not chain opcodes or ABI rules. Legacy deploy/call surfaces adapt only when
they have all required typed inputs and signing authority; otherwise return the missing
input contract and UI offers the actual supported path. They must not hide a permanent
stub behind an informational error.

Every mutating request names chain/network, pinned protocol identity, stable operation
key, exact artifact/calldata hash, caller wallet(s), authority roles and base-unit caps.
Provider fee quote includes context digest, max network fee, max locked rent/minimum
balance and maximum transferred value. API authorization echoes the digest/caps. Caps
are canonical decimal strings; no floating point. Distinct payer and authority require
each actual user's authorized custody context; role collisions cannot overwrite one
context with another. Preflight fee/rent/ownership before signing; cumulative reserved
amounts cannot exceed caps across batches/generations. Program execution may spend
funds from explicitly authorized accounts, so instruction/ABI disclosure is part of
consent, not a claim that a network fee cap bounds arbitrary contract behavior.

## Algorand AVM artifacts and calls

`AvmApplicationArtifactV1` has approval and clear-state inputs discriminated as TEAL
UTF-8 source or compiled program bytes; declared AVM version, global/local integer
and byte-slice schema, extra pages, application arguments and explicit references.
TEAL source is capped at 128 KiB per program and sent as data to the configured trusted
algod `/v2/teal/compile`; capture `/versions`, compiler network/genesis, source hashes,
compiled bytes and returned program hash. Independently verify hash/encoding and
protocol size/schema bounds. No shell interpolation, Python/TypeScript compiler,
template script or arbitrary host process is accepted. Precompiled bytecode records
provenance as supplied, not falsely source-verified. Compiler version policy is
operator configured and pinned in the artifact; changed output requires regeneration.

Application create is a real application-call transaction with app ID zero, both
programs, exact schema/pages, arguments/references, caller sender, genesis and bounded
rounds/fee. Persist canonical signed envelope before send. Confirmation requires the
pending/confirmed transaction's created app ID and matching on-chain creator/programs/
schema, not a GUID. App IDs/amounts remain protocol-width integers represented as strings.

`AvmCallV1` contains app ID, OnComplete (NoOp, OptIn, CloseOut, ClearState, Update,
Delete), raw base64 application arguments OR an explicitly typed ARC-4 method/arguments,
foreign apps/assets/accounts and box references. Update includes new immutable artifact
hash/program bytes. Compile/type coercion is provider-owned. Explicit mutating mode
requires caller signing; read-only simulation reports simulation, never chain success.
Disallow undeclared rekey/close/payment extras. If an operation requires associated
payments, expose them as an explicit atomic typed group with each signer, recipient,
amount and aggregate budget; do not silently add funding. Application policy enforces
update/delete authorization on chain; this API never claims ownership of an arbitrary app.
Expiry replacement uses existing genesis/round/caught-up-indexer absence evidence;
missing coverage retains NeedsOperator. AVM updates/deletes report exact confirmed
effect and preserve audit rather than changing a deployment's original artifact.

## Solana ELF deployment and instruction calls

`SolanaProgramArtifactV1` accepts precompiled ELF bytes (max 1 MiB), byte digest,
declared sBPF/loader-v3 target and optional build-provenance record. It validates ELF
header/class/endianness/machine/segments/length bounds and then uses simulation/actual
loader validation; a header check alone does not prove executable validity. Uploaded
Rust, Cargo manifests/build.rs, executable scripts and arbitrary build commands are
not accepted by the server. The first-class deployable input is ELF; a fixed repository
fixture can be compiled offline using the pinned Agave toolchain. User provenance is
not labeled reproducible/source-verified unless independently verified.

Request declares Create or Upgrade, payer/authority, immutable final policy or retained
caller upgrade authority, program ID for upgrade, capacity, rent recipient and caps.
Create derives stable seeded program/buffer addresses from domain-separated operation
identity and the authorized base wallet using System CreateAccountWithSeed; this is
an address seed, never private-key derivation. Pin address/seed/base/loader/genesis in
the intent. Verify existing accounts against exact expected ownership/state; an
unrelated account at either address is a conflict, not reusable storage.

Stages:
1. Atomic buffer allocation plus InitializeBuffer with caller authority and exact rent.
2. Sequential bounded Write chunks (max 768 data bytes, final wire <=1232), exact
   offset/data digest. Each stage confirms before the next; inspect finalized buffer
   bytes/authority. Store chunk map and original full artifact. Writes never trust a
   local offset counter alone.
3. Create: atomic seeded Program allocation plus DeployWithMaxDataLen, loader-owned
   ProgramData PDA, exact capacity and payer. Upgrade: verify existing Program and
   ProgramData authority/code baseline, then Upgrade from the verified buffer with
   explicit rent spill destination. Do not create another program after ambiguous finalize.
4. If Immutable selected, remove ProgramData upgrade authority in the same final
   transaction where protocol/size permits; otherwise persist it as a mandatory final
   stage and never report confirmed immutable deployment until removal is proven.

Final proof reads finalized executable Program, its exact ProgramData pointer, loader,
deployed byte range/hash and declared authority policy, and matches landed transaction
wire. Padding/capacity is verified separately from ELF digest. Unknown expired writes
or finalize transactions retain NeedsOperator unless exact finalized account/effect
evidence safely establishes the stage; missing history alone proves nothing. Observer
may recognize a matching known stage but cannot use unrelated later state as proof of
the admitted transaction. Cleanup closes only the owned buffer with authority and
the original rent recipient, via its own disclosed operation; no automatic program close.

`SolanaInstructionCallV1` has executable program ID, ordered account metas with exact
signer/writable bits, base64 instruction data, payer, requested compute limits/priority
fee caps and each signer's custody context. Validate executable program/loader and
accounts; reconstruct canonical message and reject extra instruction/signers before
custody. Optionally bind program-data/code hash to protect upgrade drift. Simulation
returns explicitly simulated logs/output; state-changing calls use durable prepare/
submit/finalized observation. No ambiguous method-name-to-instruction guessing.

## Ethereum native target

`EvmContractArtifactV1` carries deployment bytecode, typed constructor ABI/args and
expected runtime-code descriptor; compiled source provenance is explicit, not inferred.
Use existing EthereumAbi coercion and PrepareCallAsync with stable domain key, caller
SigningContext and configured per-network chain ID (Lane A), sharing the nonce store.
Bind value/gas/fee caps and bytecode before signing. Contract creation receipt address,
status, canonical confirmation and deployed code must match; mutable constructor
effects need declared inputs and cannot be silently re-run. Typed state-changing call
support uses explicit ABI, target code identity, caller and value caps through the same
module; read-only eth_call stays explicitly read-only. Native creation never routes
through the legacy Platform signing default. Existing replacement/cancel policy remains
the authority for nonce recovery.

## Standalone Solana metadata, ATA and close

- MetadataCreate: mint, name/symbol/URI/royalty, verified creator list, mutability,
  update authority, mint authority and payer contexts. Read mint and derive metadata
  PDA; validate authority and exact codec bounds; prepare real metadata instruction.
- MetadataUpdate: metadata/mint, expected previous data hash/update authority and
  explicit changed fields/new authority. Reject immutable metadata and unauthenticated
  creator verification. Read authoritative metadata before preparation; confirmed
  transaction plus final account proves the update. Round 1 immutable NFT assets stay
  immutable; this is a general mutable-metadata operation, not a bypass.
- AtaCreate: mint/owner/payer, classic SPL token program and rent cap. Validate mint
  owner and derive ATA, use idempotent associated-account instruction. Existing ATA
  counts as satisfied only after owner/mint/program/state validation. No fresh secret
  authority is needed for the recipient; payer signs its own rent allocation.
- TokenAccountClose: exact token account, token program, owner/close authority,
  expected mint/state and rent destination. For classic non-native SPL require zero
  token balance; wrapped-native close is an explicit unwrap operation with amount and
  destination disclosed, never silently treated as empty close. No platform fallback.
  Confirm absence plus exact landed close instruction/destination; absent account
  alone is insufficient proof an ambiguous operation paid the requested recipient.

These use the same prepared operation journal and truthful receipts, server custody
capabilities mirrored by SDK methods. Browser-only transaction building remains an
explicit separate capability; no fake parity flag.

## Protocol provenance, fixtures and acceptance

Official references captured in `.omc/research/round2-native-deployment-contracts-2026-10-15.md`,
`round2-loader-avm-contracts-2026-10-15.md`, `round2-native-source-contracts-2026-10-15.md`
and `round2-native-fixture-identity-2026-10-15.md`. The design uses official
[Solana deployment](https://solana.com/docs/core/programs/program-deployment),
[loader-v3 instruction definitions](https://raw.githubusercontent.com/anza-xyz/solana-sdk/master/loader-v3-interface/src/instruction.rs)
and [Algorand app construction](https://dev.algorand.co/docs/algokit-utils/typescript/latest/concepts/building/app/).
Record exact SDK/source revision and protocol golden vectors during authoring; mutable
documentation is not itself a reproducible binary fixture.

Solana local protocol fixture reuses the authored Agave 3.1.8 Containerfile/checksummed
release and adds a fixed minimal ELF fixture with source, pinned build descriptor and
digest. Actual local upload/finalize/invoke/upgrade/immutable refusal tests execute in
the final sweep. Ethereum reuses the existing contract/toolchain fixture and nonce
tests, adding a minimal bytecode deployment/call integration. No uploaded-source build
service is necessary to deploy those explicit compiled artifacts.

Algorand official AlgoKit source names `algorand/algod:latest` and supplies config/
network templates. A mutable tag is not an acceptable final fixture pin. Fixture
authoring must resolve the official registry manifest to a sha256 digest, commit that
digest with copied reviewed LocalNet configuration and record `/versions`/genesis;
registry discovery does not start a container. If registry metadata remains unavailable,
use the verified official go-algorand v5.0.3-stable source and its official container
build inputs with pinned source/toolchain hashes. Do not invent an image tag. This is
remaining fixture authoring, not external deferral or evidence of a passing gate.
Bind only loopback ports, use isolated ledger/fixture keys and no existing volume wipe.
Deploy minimal TEAL app, call/update/delete under its actual authorization and verify
application programs/state and rejection cases. The repo remains Surreal-only; any
optional local chain indexer database is isolated protocol tooling, not app persistence.

Authored regressions must cover loader invocation through the real registered route;
full multi-action mappings; private-reference leakage/current permission revocation;
source graph drift; original caller rather than publisher signing; regeneration/target
conflicts; admission/prepare/send/confirmation/projection crashes; multi-chunk overlap;
changed authority/artifact/fee/rent destination; actual typed chain create/call results;
immutable metadata refusal; safe ATA/close; request discovery and pending UI states.
Real Surreal transaction/index tests prove immutable claims and stage CAS. JS tests
execute the generated loader against the authenticated API fixture, not just compare
strings. Separate reviewers evaluate source before the final integrated runtime sweep.

## Action quotes and preparation durability amendment

Root accepted this refinement during Lane C authoring. A published native deployment
node cannot obtain a quote for a future run ID. It freezes explicit fee/rent/attached
value ceilings, exact artifact/target/input and actual actor custody instead. At
handler admission the service resolves its deterministic run/node operation key and
claims one server quote before any signature. Race winners and restarts reuse the
same saved quote. Only operation/protocol identity and quote fields are normalized;
wallets, instruction input and ceilings remain exact. Recovery may claim this
request without signing and exposes Continue; no observer obtains fresh authority.

The domain's action run/node/frozen hash fence participates atomically in admission,
preparation ownership, runtime activation and initial submission authorization.
Existing authorized wire remains observable after cancellation/fork. AVM/Solana
stage plans are separately claimed before custody, including exact validity window
or blockhash context, so a lost prepare response never chooses different bytes.
Changed costs are checked against the immutable quoted plan before signing; history
and per-stage allocation preserve aggregate caps. Ordinary quote expiry prevents new
API admission and does not rewrite an admitted action's quote on restart.
