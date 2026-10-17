# Providers/Blockchain/Ethereum — agent notes

## DEX preparation and policy boundary

The DEX quote pins one finalized block, genesis, actual deployed executor/router/
factory/quoter/wrapped-native code, immutable authorities and reviewed token code.
`ExecutionLifetimeSeconds` is explicit (300–86400); its resulting chain deadline
is hashed into the accepted quote and never extended by stage or generation
retries. Quote admission has a separate short wall-clock expiry.

The policy signer is independent: only the configured retained `PolicyKeys`
version matching the executor's signer can issue a permit. Custody/platform
wallet keys are not a fallback. Saved permits recover against the original
public signer and exact EIP-712 domain without requiring its private key.

DEX first claims an unsigned shared allocator reservation with an explicit owner
descriptor and fixed full call/fees. A missing custody context never means
platform custody. Current actual caller context is supplied only when signing;
canceling/canceled/reverted rows cannot return cached signed bytes as success.
Future executor nonces are not reserved until their exact allowance prerequisite
is finalized. Policy permits and unsigned messages remain private until the
domain records explicit external-signing exposure.

`EthereumSignedEnvelope` checks the full canonical type-2 frame, exact unsigned
bytes, low-S/scalar/parity and real sender recovery. Successful upload validates
bytes; it does not authorize server submission. Browser and server wire share
this validator. Provider/module activation awaits completion and final runtime
conformance; these source slices alone do not advertise readiness.

Directory-level rationale for the Ethereum provider. Terse one-line doc-comments live in
the code; the "why" lives here. Cross-provider rules are in `Providers/Blockchain/AGENTS.md`.

## §amount-boundary — the one thing to read before touching amounts

`IBlockchainProvider` types Mint/Burn/Transfer amounts as `RailAmount` (`Core/RailAmount.cs`):
arbitrary-precision `BigInteger` base units plus the `ValueDimension` they are counted in.
It replaced a `ulong` surface — chosen because Algorand's ASA `AssetAmount` is ulong-native —
whose ceiling of ~1.8447e19 wei ≈ **18.446744 ETH** meant the shared interface could not
express an ordinary Ethereum holding at all.

Consequences for this provider:

- Every Ethereum internal is `System.Numerics.BigInteger` — wei amounts, balances, gas math,
  fee math, nonces, chain ids, ABI values. **Wei never transits a `ulong`.**
- There is **no conversion at the interface edge any more.** `RailAmount.BaseUnits` is already
  the BigInteger the pipeline wants, so `IEthereumValueModule` is the direct path rather than
  the exact-path escape hatch it was. The former `EthereumBoundaryAmount` widen/narrow type is
  deleted; there is no `PENDING-INTERFACE-WIDENING` tag left to grep.
- The one check that remains at the boundary is `RejectUnsettleableAmount`, inherited from
  `BaseBlockchainProvider`: an amount denominated in fiat is refused rather than reinterpreted
  as wei. Chains that narrow to 64 bits (Algorand, Solana) use `NarrowToChainBaseUnits`;
  Ethereum deliberately does not narrow at all.
- `GetBalanceAsync` renders the exact `BigInteger` as a decimal string. There is no longer a
  ceiling to warn about, so it no longer appends one.

## §signer — canonical-byte contract

`ITransactionSigner.Sign(byte[] canonicalTxn, SigningKeyMaterial key)` is chain-agnostic:
the **provider** owns producing `canonicalTxn`; the **signer** owns the signature over
exactly those bytes. For Ethereum:

- `canonicalTxn` = the EIP-2718 type-2 **signing payload**
  `0x02 ‖ rlp([chainId, nonce, maxPriorityFeePerGas, maxFeePerGas, gasLimit, to, value, data, accessList])`
  — exactly `Transaction1559.GetRLPEncodedRaw()`.
- The signer returns the **fully framed submittable envelope**
  `0x02 ‖ rlp([…, yParity, r, s])` — exactly `Transaction1559.GetRLPEncoded()`, the bytes
  `eth_sendRawTransaction` takes. The provider hex-encodes and submits them **unmodified**;
  re-encoding after signing would invalidate the signature.
- The signature is secp256k1 over `keccak256(canonicalTxn)` with **y-parity** (0/1), not
  the legacy chain-encoded V. The signer refuses any payload whose first byte is not `0x02`
  rather than guessing at legacy V handling.
- The signer re-derives the sender from the assembled envelope and refuses if it does not
  recover to the signing key.

Nethereum is used for RLP framing, secp256k1, keccak, EIP-55 and ABI encoding — the parts
that must not be hand-rolled on a value path. The JSON-RPC transport is deliberately this
provider's own `HttpClient` (not `Nethereum.Web3`) so the house `RetrySafety.Broadcast`
double-spend guard and the in-process test stub both still apply.

## §sender-binding — why the from-address check is load-bearing

An EIP-1559 transaction has **no `from` field**: the sender is *recovered* from the
signature. So a wrong key does not fail — it silently spends from a different account, at a
nonce that was read for another one.

The custody callback derives the address controlled by the resolved key and refuses
unless it matches the caller's `fromAddress`. This protects against a wrong configured
key or mismatched user wallet even when the key has the expected 32-byte length.
Chain-specific derivation does not replace the sender-ownership check.
## §custody — chain-specific key derivation

`KeyCustodyService.WithPlatformSigningKeyAsync` resolves the requested chain through
`PlatformKeyDerivationRegistry.Default`, which includes Algorand, Solana and Ethereum.
Ethereum platform custody uses its own configured private key; it does not derive an
Algorand key. The sender-binding check remains mandatory.

`WalletKeyService.GenerateEthereumKeypair` creates a real secp256k1 keypair and derives
the address from that key. Its 32-byte private scalar is the signer's input. Historical
notes describing HMAC/SHA key generation or Algorand-only custody are superseded by
this implementation; missing configured keys still fail closed.
The provider also carries the same env-gated platform fallback the Solana provider has
(`AZOA:Ethereum:PlatformPrivateKey`, denied in Production/Staging) for dev/test/local
tooling when no custody service is wired.

## Durable nonce ownership and recovery

Every signer egress path, including legacy fungible calls, uses the same
`ethereum_preparation` store. Unique protocol/sender/fixed-width nonce ownership
survives crashes and multiple hosts. The candidate is the maximum of chain pending,
highest durable reservation plus one, and its immutable activation checkpoint.
No lease expiry releases a potentially signed nonce. Preparation identity binds the
entire call and signing context; retries load the first persisted wire.

Preparation and egress authority are distinct. NFT/value callers first win their
own lifecycle submission transition, then provider submission marks
`SubmissionAuthorized`. The worker only replays this state. A signed orphan from a
lost outer CAS is never auto-broadcast. Owner recovery resumes legacy admissions;
NFT/value recovery goes through its owning service. Same-nonce cancellation signs a
zero-value self-send through the original custody authority, preserves every
candidate and needs canonical finality before the original effect becomes impossible.

Activation requires a configured deployment epoch after quiescing all previous
API/worker instances and a clean latest=pending=finalized signer nonce. Existing
ambiguous legacy work blocks activation; it is never reclassified from an empty
mempool alone. The isolated deployment script refuses a listening API or occupied
EVM port and creates a fresh loopback-only chain and development overlay. It cannot
replace a running chain. Production activation requires the same deployment
quiescence discipline; rolling old/new allocators are unsupported.

## Prepared NFT and value effects

NFT wire binds the complete immutable lifecycle authorization, final/intermediary
stage and native fee quote. ABI encoding and EIP712 permit layout match
`contracts/ethereum/src/AzoaNft.sol`. Both the effect owner/issuer and the platform
policy authority sign the exact payload. Private material remains inside custody.
The final fee payer is the original actor; platform policy permission cannot be
forged by an owner supplying a zero quote. The configured runtime hash and on-chain
credential authority are verified before preparation.

Prepared observations require the exact original transaction semantics and a receipt
in a canonical finalized block. ERC20 restitution additionally requires the exact
Transfer event and units; NFT confirmation requires the exact lifecycle effect event
and owner/fee. A mined revert or finalized same-nonce cancellation is `FailedOnChain`,
never `ExpiredAbsent`. Pending/unknown never advances preparation generation.
An unresolved cancellation reports non-egress `Pending`. A concurrent cancellation
or terminal state makes submission of the exact saved original a no-op; changed
wire or an unadmitted candidate still fails validation.
Recovery enumerates every enabled Ethereum network from the same configuration
model as the factory. Each network has its own worker error boundary, so an outage
cannot prevent replay on another network. Saved rows remain network-scoped.

## §fees — EIP-1559 only

Type-2 transactions only. `maxFeePerGas = baseFeePerGas × BaseFeeMultiplier + tip`, where
the base fee comes from the head block and the tip from `eth_maxPriorityFeePerGas` (falling
back to `AZOA:Ethereum:DefaultPriorityFeeWei` for nodes that do not implement it). A chain
whose head block carries no `baseFeePerGas` is pre-1559 and is **refused**, never silently
downgraded to a legacy `gasPrice`.

The gas limit is always `eth_estimateGas × GasLimitBufferPercent`, never hardcoded. A
failing estimate is a revert the node has *already simulated*, so refusing costs nothing
while broadcasting anyway would burn the whole limit. Both the computed `maxFeePerGas` and
the buffered gas limit are checked against configured ceilings and refused when exceeded.

## §status — the vacuous-null trap, closed explicitly

`eth_getTransactionReceipt` answers `result: null` for a transaction that is simply not
mined yet, and a receipt's `status` field can be absent. A check shaped like
`receipt?.status == null ⇒ success` reports an **unknown transaction as confirmed**.

`ProbeTransactionAsync` is the single source of truth and is exhaustive:

| observation | verdict |
| --- | --- |
| receipt with `blockNumber` and `status == 0x1` | `Confirmed` |
| receipt with `blockNumber` and `status == 0x0` | `FailedOnChain` |
| receipt with `blockNumber` and **no readable `status`** | error ⇒ `Unknown` |
| no receipt, but `eth_getTransactionByHash` returns the tx | `Pending` |
| no receipt and no mempool record | error ⇒ `Unknown` |
| any RPC error | error ⇒ `Unknown` |

`GetTransactionConfirmationAsync` is overridden (rather than deriving from the status
dictionary) precisely so the mempool lookup can separate `Pending` from `Unknown`.
`GetTransactionStatusAsync` emits `success` only for the two on-chain verdicts and
`confirmed: false` for the mempool case, which is what `ChainTxClassifier` needs.

## §chain-id — replay guard

The chain id is signed *inside* the payload. The node's `eth_chainId` is cached for the
provider's (single) network binding, and when the configured network pins
`Metadata.ChainId` a mismatch is **fatal** — a repointed node URL is exactly how a
transaction meant for a test network gets signed for mainnet.

## §erc20 — the codeless-address trap

A call to an address with **no code succeeds and does nothing**. An ERC-20 "transfer" to a
mistyped contract address would therefore report success while moving nothing. Every ERC-20
path preflights `eth_getCode` and refuses a codeless target.

Ethereum has no chain-native asset creation (no ASA, no SPL mint), so `MintAsync` needs an
already-deployed contract exposing `mint(address,uint256)`: it takes the address from
`tokenUri` when that parses as one, else `AZOA:Ethereum:PlatformTokenContract`, else fails
closed. `BurnAsync` calls `burn(uint256)` (the OpenZeppelin `ERC20Burnable` shape). A
contract without those entry points reverts at `eth_estimateGas`, which fails closed before
any broadcast — that is the honest "where it does not" branch.

## §abi — exactness rules

`EthereumAbi` refuses every lossy coercion: binary floating point is rejected outright (a
`double` cannot hold a `uint256`), out-of-range integers are rejected rather than wrapped,
JSON numbers are read from their raw token text, and tuples are refused because their
component types cannot be recovered from an untyped argument bag.

`IReadOnlyContractModule.QueryContractAsync` performs typed read-only `eth_call`
without allocating a nonce or signing. State-changing deploy/call operations use
`INativeOperationModule` with explicit actor custody, immutable artifacts, quoted
caps, durable preparation and finality observation. The old synchronous ambient
platform mutation wrappers are removed.

## §hex

`EthereumHex` is strict on both directions: QUANTITY parsing accepts non-minimal forms
(nodes emit them) but caps width at 32 bytes; QUANTITY emission is minimal; DATA requires an
even digit count. Nothing parses through `long`/`ulong`.

## §bridge

Ethereum implements **neither** `IBridgeSourceLeg` **nor** `IBridgeTargetLeg`, and that
absence IS the refusal. The reason is the real one: no reviewed Ethereum bridge lifecycle
exists — no vault-control proof, no canonical wrapped-token contract or mint authority, and
no lock/mint/burn/release pairing.

There is no longer a refusal body here that could regress into a fabricated `Ok`, because
there is no method here at all. The orchestrator resolves the legs via `TryGetModule` and
fails closed on absence.

The signer is complete — the refusal is never blamed on it, and
`EthereumProviderPipelineTests` pins that by asserting the real `IEthereumValueModule`
surface is still present alongside the absent bridge legs. See
`Providers/Blockchain/AGENTS.md` §bridge.

## §faucet

`SupportsFaucet` is false. Ethereum test-network ETH comes from external, rate-limited,
captcha-gated third-party faucets; there is no server-side dispense and no provisioned
faucet account, so `DispenseFromFaucetAsync` refuses with that reason.

## Per-network EIP-155 pins

Each enabled Ethereum network requires its own positive decimal `Blockchain:Chains:<index>:<network>:ChainId`. The RPC identity must match before signing, and cached identity cannot be relabeled by a config change. Legacy chain-wide `Metadata:ChainId` is intentionally ignored: migrate it explicitly to each intended network. Base Sepolia Devnet/Testnet pin 11155111 and Mainnet pin 1 remain disabled unless configured; the local deployment overlay selects Devnet 31337.

## Typed native deployment and calls

`INativeOperationModule` accepts resolved creation bytecode plus exact expected runtime
bytes, or a typed ABI call plus a pinned target code hash. Constructor arguments use
the existing strict ABI codec; unresolved library and immutable placeholders fail
hex validation. No compiler script or ambient platform signer runs on this path.

The quote is read-only. Its gas limit, EIP-1559 caps and attached value are checked
against fresh estimates before a nonce is allocated. The complete quoted intent
binds the durable preparation fingerprint. A lost preparation response reuses the
saved candidate, including after quote expiry; a new signature requires a current
quote. The outer deployment journal alone authorizes initial egress. Exact replay
uses the same nonce and wire, and cancellation stays under the existing allocator
recovery rules.

Completion requires the exact finalized transaction receipt and runtime code at
its block. The fee budget is a maximum reservation, not a claim of actual gas spent.
Attached value caps do not constrain arbitrary internal contract effects; the exact
call and address are the caller's authorization for those effects.

## WTT authoritative state

Ethereum WTT state reads use the pinned Wormhole getter/struct/proxy contracts from commit bb5957f321b0a9d168f52e906699ee77baa82a28. Configuration under AZOA:Ethereum:Wtt:Networks:<network> must explicitly enable the profile and pin CoreBridge, TokenBridge, GenesisHash, CoreProxyCodeHash, CoreImplementationCodeHash, TokenBridgeProxyCodeHash and TokenBridgeImplementationCodeHash. No public address fallback is used. The implementation slot is EIP-1967 and both proxy and implementation runtime hashes are checked.

Every guardian/foreign-emitter read uses one finalized block hash with requireCanonical through EIP-1898. The core and token bridge must agree on their EVM and Wormhole identities, and the token bridge's wormhole() getter must identify the pinned core. Ethereum mainnet/local WTT ID is 2; current Sepolia testnet ID is 10002, distinct from its EIP-155 ID 11155111. Genesis is additionally pinned so matching chain IDs do not identify an unrelated local deployment. Guardian tuple framing is bounded and canonical before decoding its address array with the existing Nethereum ABI decoder. The provider returns authoritative evidence; cryptographic proof verification stays in Services/Wormhole.

This state module alone is not a completed WTT transfer route. Source/target transaction modules, official protocol deployment fixtures and all-six-route acceptance remain required integration work.

## WTT protocol calls and publication

`WttEthereumProtocol` uses the pinned Wormhole `Bridge.sol` type-1 transfer,
native wrapping, token attestation, wrapped registration and redemption ABI.
Nethereum remains the call encoder; quantities retain full uint256 precision.
Publication decoding accepts only the configured core log with the exact token
bridge indexed sender, transaction/block identity, nonce, consistency and payload.
Dynamic ABI offsets, integer high bits, complete length and zero padding are
checked before constructing the canonical VAA body. Receipt observation must
select exactly one matching publication; this helper never picks the first log
and does not establish finality by itself. Source receipt/code identity, provider
stage orchestration and on-chain target settlement are required subsequent seams.

## WTT asset admission

Source and target inspections reuse the same finalized EIP-1898 deployment snapshot as guardian reads. Original tokens require per-address `StandardTokens:<address>:ContractKind` (`NonProxy` or `Eip1967`) and `RuntimeCodeHash`; proxies additionally require `ImplementationCodeHash`. These are reviewed standard-transfer behavior policies, not a claim that arbitrary ERC20 bytecode can be inferred safe. Fee-on-transfer/rebase behavior needs a separate supported model and cannot silently inherit this policy. Wrapped assets require exact reverse origin mapping, a bridge-owned EIP-1967 beacon, and `WrappedProxyCodeHash`/`WrappedImplementationCodeHash`; `implementation()` must equal `tokenImplementation()`. Those getters are defined by pinned `BridgeImplementation.sol`. Code/state must be revalidated before signing and execution evidence must bind actual effects.

Wrapped representation decimals do not prove original decimals above eight. `OriginDecimals` is unknown on wrapped-source inspection; a return to the origin reads the actual origin token decimals. No guessed original precision enters economics. Metadata parsing matches the bridge's Solidity dynamic-string decoding followed by first-32-byte copying, including empty/raw/truncated strings; generic bytes32 token metadata fallbacks do not pretend to satisfy that bridge ABI. These read methods alone do not advertise the full source/target capability.

WTT source quotes reserve every prerequisite, including zero-reset/exact allowance when needed, metadata attestation and actual transfer. `StageGasLimits:<stage>` is an explicit per-network upper bound, not a fabricated gas estimate; current EIP-1559 prices price the whole plan, and real estimation must fit that immutable bound before each later nonce/signature allocation. Quote construction never signs, allocates a nonce or submits. Total native fee and message/native-value requirements are checked independently against caller caps. Origin attestation is not synthesized from a wrapped source.

Source preparation reuses the durable Ethereum allocator with operation/phase/stage/generation identity and the full frozen intent/quote. Restart returns the winning signed wire; submission validates its exact stored fingerprint and original genesis. Finalized observation requires exact receipt identity, paid fee, unique authorized publication, and actual token debit/burn or allowance logs. Core, token bridge and asset code are checked at parent and execution block; an execution-block upgrade is retained as unknown until position-specific evidence resolves it. A finalized same-nonce cancellation retains its own receipt and paid fee as separate original-stage failure evidence. These methods are not yet an advertised complete WTT module: journal first-send authorization, target settlement and all-six-route protocol acceptance remain required.

Target plans freeze original or wrapped-token code policies, canonical VAA call bytes,
registration prerequisites and the total payer fee cap. New preparation revalidates
guardian/emitter evidence and asset readiness; recovery of an already saved candidate
precedes mutable policy checks and never signs or sends. Target observation checks
historical wrapped origin/mapping, precision, body consumption and the uniquely
attributed `TransferRedeemed` event. ERC20 payouts require exact transfer/mint logs.
Native unwrap evidence combines exact WETH withdrawal amounts with successful
execution of the pinned bridge code, whose immutable call names the recipients;
it does not claim an RPC execution trace. When recipient and relayer are identical,
the protocol pays the whole amount to that recipient. Full journal integration,
target protocol fixtures and all-six-route acceptance remain required.

## Native cancellation evidence

Native observation keeps the original stage transaction identity, but a finalized
same-nonce cancellation is separate evidence of original failure. Both successful
and reverted cancellations consume the nonce. Its own receipt supplies gas costs;
missing fee evidence or changed canonical/finalized blocks cannot seal recovery.
The cancellation hash and status are retained explicitly, with no deployed resource.

## Unsigned DEX reservations

ReserveCallAsync shares the existing nonce checkpoint and unique reservation with legacy, NFT, native and WTT callers. A DEX reservation carries an explicit immutable owner descriptor and no custody context; null never authorizes a platform key. SignReservedCallAsync requires the actual current participant context, matching actor/wallet/tenant/scope, and a still-reserved row before custody. Generic recovery may cancel an owner nonce using fresh authority, but cannot resume a DEX operation outside its domain admission. Exposure and first-send remain separate domain transitions. The reviewed design and remaining browser/nonce-winner gates are in round3-ethereum-dex-allocation-amendment.md.

EthereumPayloadPermit shares exact EIP-712 Execution(bytes32 payloadHash) framing with explicit name/version domains; the NFT wrapper preserves AZOA NFT/1. DEX uses a distinct retained policy authority and AZOA DEX/1. ContractReads preserves the existing pinned-block eth_call and strict single-word uint decoding; WTT wrappers remain for current callers. The DEX executor tuple has nineteen static ABI words and rejects noncanonical or overflowing decimal amounts before encoding. These helpers do not establish deployed-bytecode readiness or successful settlement.
