# Round4Gates — real-DB exactly-once and fail-closed gates (round 4, L6)

Every class here runs against real SurrealDB (`SurrealTestDefaults.Endpoint`) in its own namespace
(`Interleaving/InterleavingDatabase`), with the generated goldens. They are the sweep's "exactly-once" ranked gate.
Interleaving fixtures (helper) are documented in `Interleaving/AGENTS.md`.

## §model-valid-rows
`ModelValidRow.Fill` builds a row every column attribute admits: `[Inside]` first member, `[References]` →
`<target table>:<hex>`, the `[Assert]` regex families (hex64, digits, decimal, `[A-Z]{3}`, network), `[Required]` →
non-empty, optional columns left NONE, JSON `{}`. Tests then overwrite only the fields their scenario is about, so a
seed never drifts from the schema again (the round-4 seed failures were all missing ASSERT/link shapes).

## §enum-round-trip
`EnumStorage/EnumColumnRoundTripTests`: (1) every enum column's `[Inside]` set equals the enum's names
(reviewed exclusion: `KycProvider.UNAVAILABLE`, a sentinel the gate refuses before any write); (2) for every
member of every enum column, a `SurrealWriter` write is found by a typed equality read — writer encoding and
SurrealForge 1.3.0 predicate binding agree (schemaless namespace, so only that agreement is tested).

## §captured-null
`CapturedNull/CapturedNullGuardTests`: SurrealForge 1.3.0 binds a captured null as NONE, so `TenantId == tenant`
with no tenant now matches tenant-less rows. Negatives pin that the flip admits only the caller's own tenant-less rows
(KYC latest/active submission, release-mandate owner list, fungible participant list). The Hold sites are in
`SurrealHoldCommandTests.CapturedNull.cs` (helper-authored, run in the sweep).

## §data-compatibility
`DataCompatibility/GeneratedSchemaDataCompatibilityTests` — the P2 rehearsal body; see
`Fixtures/UpgradeRehearsal/AGENTS.md`. Skips tables a POCO cannot write alone (`[VirtualTable]`, RELATE edges,
CLR-less `[ExtraSurrealField]` columns).

## §tenant-payout
`TenantPayout/TenantPayoutLedgerRaceTests` (D-8): two reservations against a balance covering one → one commit;
a replayed reservation (same payout id) rolls back whole; credit promotion vs reversal → one winner and
`credited == Σ available contributions`; a credit per funding intent is recorded once (the loser gets the winner's
row). The matching source rule (`SurrealQlSourceRulesTests.Tenant_payout_rows_change_only_through_the_ledger_commit`)
keeps every `tenant_payout_*` write inside `CommitAsync` / `RecordCreditAsync` / `GetOrCreateFloatAsync`.

## §client-signing
`ClientSigning/ClientSigningTransitionRaceTests` (D-3): the operation-row status CAS lets one transition leave
`AwaitingSignature` — two submits, or a submit racing the stuck-row reconcile.

After that race, a submission winner remains `PendingConfirmation` and belongs
to the stuck-row reconciliation page once old enough; a terminal failure does not.
The fixture preserves exactly one transition, winner payload and completion stamps.

## §fence-allow-paths
`Inventory/FenceAllowPathInventoryTests`: every `*Fence.cs` names a real-DB test whose success path commits
through it (a fence whose guards never pass fails closed silently; only an allow-path fixture notices). A new fence
without an entry fails; a stale entry fails; a named test that does not exist fails. Whether each named test is
green is the sweep's job (`round4-lane-L6-verification.md` lists the ones red on this tree and why).

## §store-family-guards
`Inventory/StoreFamilyGuardInventoryTests`: every store family that commits a BEGIN…COMMIT batch names one THROW-marker
case (typed refusal, nothing partial) and one duplicate/replay case (one winner), or a gap with its owner. The gaps
are the remaining fixture debt, listed in the lane record for the sweep.
