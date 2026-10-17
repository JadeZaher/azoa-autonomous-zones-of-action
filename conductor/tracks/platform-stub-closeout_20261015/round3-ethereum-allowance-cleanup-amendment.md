---
type: design
status: pending-independent-review
---

# Ethereum allowance cleanup consent

ERC20 allowance revocation is a separate effect from closing an empty token
account. The shared cleanup command will admit a second explicit semantic,
`RevokeCurrentAllowance`, with its own affirmative consent flag. It identifies
the original input asset; the spender is exclusively the executor already
committed in the original accepted quote. No arbitrary spender is accepted.
The existing `CloseCurrentZeroHolding` command and its consent flag retain their
current output-account meaning. The two flags are mutually exclusive.

The owner authorizes setting their current allowance for that exact token and
executor to zero, including an allowance changed outside this operation. This
does not transfer tokens or claim that the current allowance was created by the
operation. The exact token, executor, owner, original quote and cleanup fee cap
are displayed and persisted before planning or exposing executable bytes.
Completed workflows use fresh direct owner authority for cleanup; they do not
pretend that the old action is still running.

The operation must retain its durable allowance resource ownership. Historical
unresolved or exposed approval/swap stages prevent cleanup admission. The
provider prepares only `approve(originalExecutor, 0)` at a newly reserved nonce,
with the original token code pin and a separately budgeted cleanup gas ceiling.
No policy permit or swap effect is included. The same command/generation retains
the exact unsigned reservation and signed wire across response loss.

A finalized successful exact call, matching Approval event and canonical
resource-bound zero allowance evidence may release the resource. A reverted,
pending, unknown or changed call cannot release it. A completed swap whose
allowance remains nonzero retains the resource and discloses the explicit
cleanup requirement; maximum ERC20 allowances must not be assumed consumed.
Cleanup reporting uses zero swap input/output/platform fee and actual network
fee. Ethereum has no token-account rent allocation or release to manufacture.

The generic allocator recovery worker must never replay a domain-owned swap
reservation independently of the swap journal. It may observe the original
candidate, but first or repeated server egress belongs to the DEX coordinator
and its persisted withdrawal/authorization checks. Owner same-nonce cancellation
remains available through the existing authenticated recovery route; cancellation
cannot be confused with successful allowance cleanup.

Required unrun fixtures: cross-semantic/flag/input-asset refusal; changed spender
or token wire refusal; original completed-workflow cleanup with fresh owner
authority; unresolved old stage refusal; response-loss exact replay; actual
zero-allowance proof before release; max-allowance retained after swap; pending
or reverted revoke retains resource; generic worker cannot bypass withdrawal.
