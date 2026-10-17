---
type: Handoff
status: awaiting-independent-review-runtime-unrun
---

# B1 fungible and genesis client slice

Authored SDK fungible creation and genesis methods/types/exports, exact uint64 string supply, required original key, participant operation panel, operator genesis console, narrow BFF admin route mapping and navigation. The existing wallet-generation/portfolio page remains and embeds the durable panel. Participant server discovery survives browser storage loss. Saved requests and versioned commands survive response loss; current owner attachment is separate from finalized issuance. Genesis uses operator cookies and has no ordinary cancellation.

The immutable platform-definition claim now joins actual recent operator/bootstrap authorization atomically, before pending lifecycle admission. Active platform reads require completed operation plus authority/projection/reserve lineage. Old mocked genesis fixtures were migrated to pending admission and explicitly seeded verified-read responses; real persistence lineage and concurrent finalization fixtures remain in SurrealFungibleProjectionTests. Initial current/revoked claim fixtures are also authored.

Tests authored: SDK client.fungible-creation, frontend fungible-creation-panel, operator genesis-route, migrated PlatformTokenGenesisTests and extended SurrealFungibleProjectionTests. No build/test/lint/typecheck/schema or protocol runtime ran. This is a source handoff for review, not B1 completion. Remaining B1 work includes other old manager/dispatch fixture migrations, lifecycle crash/recovery coverage, final changed-file pruning and resolving independent review findings. B2-B5 remain required staged implementation.

## Follow-up source corrections and fixture migration

Old manager/supply-ceiling/reserve/controller fixtures now use durable admission boundaries; no mocked pending transaction is asserted to be an asset. SurrealFungibleProjectionTests adds real-store restricted recovery and crash-before-send/lost-submit-response cases, exact persisted wire, revoked participant accounting and initial operator claim fences. Root independently accepted the bounded final authority/reserve/genesis projection and ownership-conflict attachment source, with runtime unrun.

Saved continue/replace/cancel and operator commands now have explicit server-version reconciliation; attachment also checks exact projection authority. Generic errors never retire a command. Original command plus observed newer state is archived, and already-authorized effects remain visible. Frontend fixtures cover equal-version/read failures and successful greater-version retirement, attachment identity mismatch, and operator recovery. Display separators and stale unmounted operator read handling are corrected. Completion and existing-reserve locks now use scoped typed mutation builders inside the multi-record transaction; renewal locking was similarly pruned. Broader runtime validation remains scheduled for the final sweep.

The final pruning pass also converted the exact authorization renewal lock to typed mutation, removed stale one-shot/CallerWallet failure commentary, and narrowed reserve public-address derivation's catch-all to expected validation/cryptographic exceptions. Reserve behavior rationale now lives in the directory AGENTS. Saved commands outside the first server discovery page can recover their exact operation directly without executing them.

Root independently reread and accepted the bounded saved-command reconciliation semantics. Root found the operator button passed a Promise to run instead of a callback; corrected to run(reconcileCommand). Source-only acceptance does not replace final typecheck/tests. Root authorized beginning the already-accepted B2 subwave while B1 final integration/review remains tracked.
