# Ethereum custody contracts

## DEX executor

AzoaDexExecutor binds the classic Uniswap V3 router/factory/WETH deployment and a
separate immutable EIP712 policy signer/key version. Every effect requires the
actual owner as transaction sender and a policy signature over canonical complete
ABI payload, including operation, quote digest, fee revision and token code hashes.
Successful operation identities cannot be reused. No general call, delegatecall,
operator withdrawal or owner-forged zero fee is exposed.

Exact gross input, atomic fee, exact router allowance, minimum output and recipient
delivery share one reverting nonreentrant transaction. Router allowance returns
to zero; before/after balances preserve prior token/native dust. Native wrapping
and unwrapping are explicit flags. Reviewed token deployment policy excludes
rebasing, transfer-tax, callback/proxy and temporary-allowance implementations;
balance checks and code hashes supplement that policy, not replace its review.

SafeERC20.forceApprove behavior is pinned to OpenZeppelin 5.4.0; its source capture
is `.omc/research/round3-openzeppelin-safe-erc20-5.4.0.md`. Real pinned Uniswap local
protocol tests, deployed immutable-byte verification and provider permit assembly
remain required before activation. Source authoring alone is not protocol proof.

The pinned OpenZeppelin ERC721 implementation supplies read/event compatibility;
all mutation entrypoints are restricted to `execute`. Each execution requires two
EIP712 permits over the full ABI payload: the actual owner/issuer/recipient for its
stage, and the platform authority attesting the persisted server-selected fee policy.
An owner cannot forge an authorized zero fee. Typed data uses the contract/chain
domain and an `Execution(bytes32 payloadHash)` message. The payload includes the
logical lifecycle, operation, stage/generation, final-effect marker and quote.

Stage keys prevent exact replay. Final logical effect keys also prevent charging a
new generation after success. Treasury forwarding, receiver callbacks, NFT effect
and replay consumption share one reverting, nonreentrant transaction. Credential
issuer and soulbound state are immutable; direct ERC721 transfers/approvals cannot
bypass them. The three-argument inherited safe transfer calls the overridden
four-argument mutation and is also refused.

Build uses exact solc/OpenZeppelin versions, Shanghai bytecode, fixed optimizer and
metadata settings. Constructor authority is immutable, so runtime code hash must be
computed from the deployed instance, with artifact immutable offsets checked against
the configured authority; an unpatched template runtime hash is not an activation
proof. Local deployment never uses production keys or a public RPC.
