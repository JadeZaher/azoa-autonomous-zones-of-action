---
type: Review
status: Bounded foundation and same-cap source accepted; full API/replacement and runtime review pending
---
# Independent native recovery source review

Reviewer: lane C, separate from root author. No build, test, schema or protocol execution occurred.

Reviewed slices: normalized resource/cleanup effects; original fallback reserve and refund disclosure; `SurrealDeploymentStore.LinkedRecovery` atomic parent/child claims and completion; `SolanaProvider.NativeRecovery`; `DeploymentService.LinkedRecovery`; same-cap `INativeRecoveryModule` and `DeploymentService.Recovery` transitions.

The corrected foundation retains a confirmed deployed/upgraded resource separately from cleanup. Parent/child claim binds exact parent version, generation, original request/history and saved server quote with database-clock expiry; ordinary parent updates cannot race a claimed child. Child mutation/completion remains bound to that parent. Deterministic persistence race fixtures are authored but unrun.

Provider recovery reobserves every original resolved wire. Buffer cleanup requires original funding with no confirmed deployment effect, actual buffer loader ownership/authority, original recipient and exact finalized balance refund evidence. Authority finalization requires the originally requested immutable deployment and the original program identity. No absence assertion permits cleanup of an uncertain original wire.

Same-cap recovery preserves original intent/hash and quote. Its mode is selected through parent version CAS; fallback cleanup stays outside the normal stage sequence. Cleanup terminal outcomes are AbortedClean/FailedClean rather than deployment success. Cap exhaustion parks RecoveryQuoteRequired for separately consented recovery.

Required corrections found and reread:

- Generic helper originally classified provider stages and allowed cleanup to replace deployment evidence. Provider-normalized separate effects now resolve this.
- Normal and cleanup refund destinations were conflated; disclosure now separates them.
- Explicit null recovery DTO members could throw; bounded guards were added.
- Recovery capability selection used a Solana family string; it now uses INativeRecoveryModule.
- Cumulative cost rejection originally happened after preparation/signing. NativeNextStage.Reservation now lets Advance reject before Preparing/provider preparation, retaining a second actual-preparation check. `ExhaustedRecoveryBudgetParksBeforePreparationOrCustody` asserts no prepare or submit.

These slices have no remaining concrete blocker found in the bounded static pass. This is not full package approval: replacement-child lifecycle, controller/SDK/UI journeys, final integration and actual local protocol execution still require their own evidence. WTT authoring is not reviewed by this report.

## Recovery client and unadmitted quote withdrawal

The controller/SDK route bodies, authenticated actor/tenant scope and exact saved
client command replay were inspected separately. Required fixes now reread:
saved linked command mode/economic disclosure survives remount; embedded Pascal
or camel fields are decoded without modifying saved bytes and conflicting aliases
are rejected; account/operation remount and selection guards discard late replies.

The withdrawal endpoint initially permitted cross-parent and caller-lowered-version
false success. It now requires the exact immutable original intent/operation ID,
server-issued quote bytes/hash and parent version frozen inside that quoted intent
before any success. Parent read then child read proves absence only after this
identity binding. Advancing the exact parent version fences delayed admission;
child admission and withdrawal compete on that same CAS. The child link remains
durable, including replacement. Two-parent, altered outer version, lost-response
replay and both database race ordering fixtures are authored, not executed. No
remaining concrete blocker was found in this bounded withdrawal/retry source pass.
Replacement-child lifecycle and runtime verification remain outside this verdict.

## Replacement review continuation

Independent reviewer: lane C; author: Root. Revised `round3-native-replacement-amendment.md` accepted for implementation after separating exact resolved-failure proof from never-authorized retirement, requiring full generation/history evidence for the latter, and removing a lifetime retry limit.

Bounded source reread accepted `NativeRecoveryReplacement`, `SurrealDeploymentStore.ReplacementRecovery`, and the service predecessor re-observation seam: exact child journal/version, parent link/version/history, unexpired persisted quote and new child creation are atomic; old children are permanently sealed against ordinary updates. Quote and admission both re-observe every resolved prior wire. The signed-but-unsent case relies on initial generation/empty history/no authorization and the domain first-send CAS, not a fabricated expiry verdict.

Replacement-aware withdrawal was independently reread: exact authenticated server quote and quoted parent version remain required before any nonadmission proof. Equal-version withdrawal locks the exact predecessor journal and advances the parent without clearing its child. Resume and replacement cannot simultaneously satisfy the same predecessor/parent guards. The earlier cross-parent and forged outer-version findings remain corrected.

These are bounded source verdicts, not execution results. Paged full-ancestry validation, final replacement UI journey and final integration/DB race execution remain required. No test, build, schema or protocol execution occurred in this review.
