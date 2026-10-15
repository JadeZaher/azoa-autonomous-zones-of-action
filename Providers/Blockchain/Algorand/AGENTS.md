# Algorand provider notes

## Bridge capability — SOURCE only, and that asymmetry is deliberate

`AlgorandProvider` implements `IBridgeSourceLeg` and **not** `IBridgeTargetLeg`.

That is the honest shape of this chain today, and it is the shape a single `SupportsBridging`
boolean could not express: the boolean was false, which suppressed Algorand's genuine outbound
capability entirely. Capability is now structural — a chain that cannot do something simply
does not implement the module, so absence IS the fail-closed refusal. There is no boolean to
set true by mistake and no refusal body left that could regress into a fabricated `Ok`.

### What Algorand CAN do, and how each capability is PROVEN

`LockAsync` and `ReleaseAsync` broadcast real, signed transactions and return real transaction
ids. Every precondition is read from chain state before anything is signed; none is assumed,
and an unreadable fact refuses rather than guessing.

- **Lock** — proves the VAULT is opted into the ASA (Algod account read). Algorand rejects an
  asset transfer to an account that has not opted in; broadcasting anyway would burn a fee and
  let a caller read the submitted tx id as evidence the value moved.
- **Release, proof 1 — vault control.** The vault must BE the platform signing account, and
  its Algod `auth-addr` must be absent or equal to it. A rekeyed vault is signed by a
  *different* key than this provider holds, so same-address reasoning there would be false and
  the broadcast would be unauthorised.
- **Release, proof 2 — vault balance.** The vault's holding of the ASA is read and must cover
  the requested amount and must not be frozen.
- **Release, proof 3 — recipient opt-in.** The recipient must have opted in and be unfrozen.
  The platform *cannot* opt in on the recipient's behalf — the account must sign its own
  opt-in — so the only safe primitive is verify-and-refuse.
- **Pending ≠ settled.** A broadcast that does not confirm inside the poll window returns
  `BridgeLock.PendingConfirmation = true` with the real tx id. That is an explicit field rather
  than a message marker precisely so it cannot be lost to a reworded message, and so a slow but
  valid lock is never recorded as failed (which would invite a double broadcast).

`BurnAsync` (`AssetDestroy`) proves, before signing, that the platform is the asset **creator**
and holds **every single unit** — the exact precondition Algorand enforces. Previously this was
assumed; a destroy attempted against outstanding supply is rejected on chain, and reporting the
attempt as a burn would claim value was destroyed that still exists in other accounts.

### What Algorand CANNOT do, and why there is no code claiming otherwise

No canonical platform-controlled **wrapped ASA** lifecycle exists. A safe wrapped mint needs a
canonical wrapped asset resolved once per source asset (creating a fresh ASA per mint is the
broken model this provider previously had), then a recipient opt-in that the platform cannot
perform, then a transfer — and its paired destroy needs the full-supply proof above. Until that
lifecycle is designed and reviewed, `IBridgeTargetLeg` is **not implemented**, so there is no
wrapped mint or burn on this provider to fabricate a success with.

### Tests

`BridgePrimitivesTests` covers the four focused cases this file demands — confirmed submission,
pending-confirmation recovery, ownership/opt-in proof, and failure without fabricated success —
plus module presence/absence:

- `Algorand_lock_broadcasts_real_asset_transfer_to_an_opted_in_vault`
- `Algorand_lock_reports_a_broadcast_but_unconfirmed_transaction_as_pending`
- `Algorand_release_refuses_when_the_recipient_has_not_opted_in`
- `Algorand_release_refuses_when_the_vault_holds_less_than_the_requested_amount`
- `Algorand_release_refuses_when_the_vault_is_not_the_release_signer`
- `Algorand_release_refuses_when_the_vault_is_rekeyed_away_from_the_release_signer`
- `Algorand_release_broadcasts_a_real_transfer_once_every_precondition_is_proven`
- `Algorand_destroy_refuses_when_the_platform_does_not_hold_the_full_supply`
- `Algorand_destroy_refuses_when_the_platform_is_not_the_asset_creator`
- `Algorand_ships_a_source_leg_and_deliberately_no_target_leg`

Do not implement `IBridgeTargetLeg` until wrapped mint and burn have the same standard of
proof: confirmed submission, pending-confirmation recovery, ownership/opt-in, and failure
without fabricated success.
