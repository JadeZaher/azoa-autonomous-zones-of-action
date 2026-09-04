# Providers/Blockchain/Ethereum — agent notes

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

The custody callback therefore derives the address the resolved key actually controls and
refuses unless it matches the caller's `fromAddress`. This is not defence in depth; it is
the only check that can catch the current platform door, because
`KeyCustodyService.WithPlatformSigningKeyAsync` resolves an Algorand Ed25519 seed which is
**also 32 bytes** — a length check cannot tell the two apart.

## §custody — known blocker (not fixable from this directory)

`Services/Custody/KeyCustodyService.cs`'s platform door is Algorand-only:
`PlatformMnemonicConfigPath = "AZOA:Algorand:PlatformMnemonic"`, resolved via
`new Algorand.Algod.Model.Account(mnemonic).KeyPair.ClearTextPrivateKey`. A
platform-context Ethereum op routed through custody therefore fails closed on the
sender-binding check above, with a message that names that exact cause.

Per-user custody works (the wallet's own decrypted key bytes are handed through) **subject
to** a second, separate defect: `Services/Signing/WalletKeyService.GenerateEthereumKeypair`
is a documented stub — it derives the "public key" with HMAC-SHA256 and the address with
SHA-256 instead of secp256k1 + keccak-256. Wallets it generated therefore do **not** own the
address recorded for them, so the sender-binding check refuses them too. That is the correct
outcome (signing anyway would spend from an unrelated account), but it means per-user
Ethereum value ops cannot work until that keygen is real.

Both fixes are outside this directory; see the provider's INTEGRATION NOTES.

The provider also carries the same env-gated platform fallback the Solana provider has
(`AZOA:Ethereum:PlatformPrivateKey`, denied in Production/Staging) for dev/test/local
tooling when no custody service is wired.

## §nonce — race strategy

A nonce collision on Ethereum is not a duplicate: it is a **replacement**. Two ops that
read the same pending nonce produce two valid transactions, exactly one of which mines.

Strategy:
1. The nonce is read from `eth_getTransactionCount(from, "pending")` — the node is the
   authority.
2. The `fetch → build → sign → broadcast` window is serialized per `(chainId, from)` by a
   `SemaphoreSlim`, so two in-process ops on one account cannot interleave.
3. A per-account **high-water mark** of the last nonce this process broadcast covers the
   window where a node has accepted a transaction but has not yet reflected it in its
   pending count: the next nonce is `max(nodePending, highWater + 1)`.
4. The gate is released *before* the confirmation poll — the nonce is committed once the
   broadcast returns, and holding the gate across a multi-block wait would serialize the
   account for minutes.
5. On an **ambiguous** broadcast failure (transport timeout / post-send 5xx) the high-water
   mark IS advanced: the node may hold the transaction, so reusing that nonce would replace
   it. On a **definitive** JSON-RPC rejection it is not: the node either never accepted the
   transaction (nonce still free) or already knows it (its pending count already reflects
   that), and the node stays the authority.

**Limitation, deliberately not papered over:** this is in-process only. Two app instances
sharing one platform key can still collide. That needs a durable nonce allocator (a
per-account lease in SurrealDB); until then the collision surfaces as one replaced
transaction, and the pending-confirmation + reconcile path settles it from chain truth.
No path in this provider ever re-broadcasts.

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

`CallContractAsync` performs real read-only `eth_call`s but **refuses state-changing
calls**: that interface method carries no `SigningContext`, so a write could only run under
ambient platform authority with no caller attribution.

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
