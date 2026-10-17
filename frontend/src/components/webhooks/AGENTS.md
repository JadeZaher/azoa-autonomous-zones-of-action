# Webhook registration management

The tenant settings editor is keyed by authenticated user ID. It persists only the
exact management command before any mutation request; generated secrets remain
in component memory and are cleared on account remount. An uncertain request is
retried unchanged. The editor cannot clear it merely because GET lacks a receipt;
a newer registration version proves its compare-and-swap can no longer apply.
Legacy replacement requires explicit consent and the server-issued fingerprint.

The operator tenant console reuses the same editor through operatorRequest and
strict registration/withdrawal BFF paths. Storage is separately namespaced by
operator target tenant, and target changes remount the editor. Mutation bodies
are bounded and contain no caller-supplied authority or secret; cookie session,
same-origin protection and no-store response behavior remain in the existing BFF. A permanently rejected command uses
the explicit withdrawal route, whose audit key serializes against the original
mutation. The UI clears a saved command only after authoritative withdrawal or
already-applied disposition plus successful current-state reload. A lost response
retains the exact command for withdrawal retry. No local-only forget button is
permitted. Delivery leases and durable Emit receipts are
separate required work. UI regression tests are authored but unrun.
