---
type: Review
title: Wallet authentication source review
status: Corrections required; implementation in progress
---

Coordinator inspected the chain identity helper, verifier, authentication manager, typed challenge/invite stores and narrow user authentication CAS. Signature verification uses EIP-191 recovery with bounded canonical signatures and raw Ed25519 verification for Algorand/Solana. Typed nonce consume retains a conditional single-use update; canonical Ethereum identity is separate from case-sensitive Solana identity.

## Blocking credential-retention finding

ClaimAsync replaces only the chosen credential. A password claim preserves the previous wallet binding; a wallet claim preserves the previous password hash. A tenant or other prior credential holder can therefore obtain a fresh login after ownership is severed, bypassing the intended protection of the old-token watermark. Claim must atomically revoke both prior credential forms: password claim clears the old wallet binding; wallet claim disables or replaces the old password with an unknowable credential. Add regressions proving old credentials cannot mint a new session after claim.

## Remaining source acceptance checks

The author already identified concurrent challenge-cap admission, exact issuer/audience/expiry message validation and legacy noncanonical wallet rows as unfinished seams. They remain implementation work. Verify nullable expected authentication fields compile to correct NONE predicates rather than bound JSON null; positive real-Surreal tests must cover never-linked/null-watermark and previously unset fields. Remove stale verifier references and move verbose implementation rationale to directory documentation. No runtime gates have run.

## Migration conflict resolution finding

Early migration source normalizes the first scanned canonical-equivalent account, then emits a diagnostic only for the second account whose unique-index write fails. The repair API accepts only that diagnostic, so the operator can clear only the second binding. This implicitly privileges scan order. Duplicate diagnosis must identify all equivalent owners with private exact-identity fingerprints and permit an explicit audited choice of which conflicting binding to clear. Authentication remains gated until deliberately resolved. Add reversed-order coverage showing that either legitimate account can be preserved without automatic merging or owner selection.

## User-store integration findings

The narrowed generic Upsert rejects AuthNotBefore changes, but UserManager.LogoutEverywhereAsync still uses it to advance that watermark. Migrate logout to a dedicated monotonic revocation mutation; concurrent claim/logout must never move the watermark backwards. Generic profile updates must also preserve password and role authority: reading the latest record for a predicate while writing a stale caller object's PasswordHash or DappRole can restore an older credential/role. Use dedicated credential and role mutations with appropriate concurrency semantics and preserve the existing role-assignment API.

Follow-up source reread finds those credential, nullable-CAS, logout and role corrections present, along with all-owner migration diagnostics and durable challenge admission slots. One remaining authority field issue is generic Upsert assigning stale IsActive/IsVerified values. Preserve them during ordinary profile saves and use dedicated expected-state mutations for intentional changes; cover concurrent profile save versus disable/revocation. Collision diagnosis also needs lease renewal or bounded persisted progress while scanning a large set; a fixed two-minute lease must not prevent eventual diagnosis. Source acceptance remains pending these corrections and regression review; tests remain unrun.

Final bounded source reread confirms generic profile writes preserve activation/verification flags, intentional activation uses expected-state CAS, and the stale profile regression covers both flags. Collision diagnosis renews and verifies lease ownership before every bounded page. These reported wallet findings are source-closed; authored fixtures and migration/database behavior remain unexecuted until the integrated gate.
