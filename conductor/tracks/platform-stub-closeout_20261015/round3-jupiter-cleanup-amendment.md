---
type: design
---

# Jupiter retained-account cleanup

Status: independently accepted design by root. Opcode 5 guard and framing fixtures are authored; provider cleanup integration and actual execution remain required and unverified.

The existing separate owner cleanup command authorizes closing the current zero holding for one originally disclosed ATA, even if the owner previously closed and recreated it. It does not reuse a completed workflow's signing authority. The signer and refund recipient are the original owner; fresh direct/participant consent, exact asset/account identity and the separate cleanup fee cap are mandatory.

Legacy SPL CloseAccount rejects a nonzero ordinary token balance, but deliberately permits closing native wrapped-SOL holdings with positive balance. The pinned processor at solana-program/token revision c666f1dc61f38dd0a9c0fcdeaeac4778274f7116 confirms this in `process_close_account` (captured as round3-spl-close-processor-2026-10-15.md). Preparation-time reads therefore cannot enforce the zero-holding consent by themselves.

Add a closed opcode 5 to the existing immutable cost guard. Its exact 97-byte payload is opcode, token-account address, mint and owner (three 32-byte keys), with exactly two accounts: that writable nonsigner token account and the original owner signer/payer. Require canonical legacy SPL program ownership, nonexecutable 165-byte initialized token layout, exact mint/owner, amount zero, and absent or original-owner close authority. For native accounts also require current lamports equal the stored native reserve, refusing unsynchronized incoming SOL as well as a positive recorded amount. No account mutation or transfer occurs in the guard. The following exact SPL CloseAccount closes that account to the same original owner. Both instructions execute atomically under the account locks; a racing token/SOL arrival cannot be swept by the admitted zero-holding command.

Cleanup preparation reuses the original pinned guard deployment and a fixed-width operation/stage/generation Memo. It verifies the current owned account, computes exact message fee and simulation before custody, and retains the original prepared bytes across retries. A stale guard deployment or positive holding refuses new signing. External exposure retains potentially-submitted semantics, exact-signature recovery and complete expired block ancestry proof.

Confirmed cleanup proves the exact signed transaction, finalized block identity, source token account removal, account lamports returned only to the owner and exact network fee. It reports the original economic swap unchanged. Actual account closure does not by itself release any original allocation reservation.

Allocation release separately traverses complete finalized block ancestry from the cleanup's fixed finalized position back to the original successful ATA creation transaction position. Full transaction order and inner SPL CloseAccount instructions identify the first successful close after the allocation. The traversal preserves the earliest close encountered while walking backward and completes only after reaching the exact original transaction. An intervening external close/reopen releases the original allocation at that first close; later cleanup does not claim it again. Missing/pruned transaction bodies or unresolved ordering retain the allocation. Credit is capped at the original operation-attributed actual rent, and the existing store's unique allocation release CAS prevents duplicates across external/cleanup proof paths. No currently missing holding or latest account state substitutes for this history.

Required authored coverage: zero ordinary/WSOL account cleanup; positive balance and unsynchronized SOL refusal at execution; wrong mint/owner/account/close authority; exact destination; duplicate/reordered cleanup requests; closed/reopened account with original allocation released once; earlier external close and cleanup in the same finalized block; expired/missing history; stale original workflow with fresh owner cleanup consent; actual deployed guard runtime and packet/fee fixtures at the final sweep.
