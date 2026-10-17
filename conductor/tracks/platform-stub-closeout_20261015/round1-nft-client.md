---
type: implementation-handoff
---
# Round 1 NFT SDK and UI handoff

Authored; no build, typecheck, lint or test execution. Independent review and final
integrated verification remain required. Core endpoint additions were coordinated
with the NFT author and may still be in flight at this snapshot.

## Contracts and behavior

- SDK UserNFT mint/transfer/burn and credential issuance return durable lifecycle views
  and require stable idempotency keys. Legacy NFT mutations retain their operation
  projections, now require keys; legacy mint requires a metadata URI.
- Typed lifecycle GET, refresh, accept, cancel, participant cursor discovery, fee quote,
  and original-transfer refund methods mirror the agreed controller contracts.
- Fee revision and native base-unit string caps survive retries without numeric rounding.
  The UI reviews the fee before a second explicit authorization click. Recipient or
  creator acceptance does not authorize a second platform fee.
- Pending UI states never report confirmed ownership. Recipient, creator-destruction,
  and refund authorization steps remain explicit. Refund names the original confirmed
  transfer and requires holder approval.
- Server participant discovery works without browser state and follows nonempty cursors
  on empty filtered pages. Persisted browser request keys provide retry convenience.
- Request storage is scoped to user and NFT; terminal outcomes clear only matching saved
  operation references. Unreadable pending keys are preserved, not silently replaced.
- The /nfts gallery links to the /user-nfts lifecycle controls; duplicate old mutation
  forms were removed. Ownership/access verification now uses real boolean contracts:
  false cannot display as verified. Binding paths/body fields match existing controllers.

## File inventory

SDK: src/api/client.ts, api/index.ts, root index.ts, api/AGENTS.md,
api/nft-lifecycle.test.ts, tests/api/client.test.ts, examples/nft-lifecycle.ts.
Frontend: dashboard nfts and user-nfts pages; shared nft-lifecycle-panel,
nft-move-actions, nft-pending-operations, nft-fee-review, nft-refund-action;
shared AGENTS and the three lifecycle/move/discovery component test files.
API_SYNC.md records the revised routes, fee contract and execution status.

## Authored regression coverage

- SDK pending facts and mutation headers; credential issue plus read-only lookup;
  negative verification; encoded cursor and lossless large fee authorization.
- UI submitted state does not claim completion; creator destruction requires a click.
- Retry after remount uses the original transfer key/recipient; corrupt saved request
  cannot start a fresh burn; fee display precedes burn authorization.
- Server recipient discovery follows an empty page cursor and selects the later operation
  without local storage.

## Follow-up verification

Core author must finish the agreed discovery/refund/fee endpoints. Independent reviewer
should check DTO parity and browser authority transitions. Final integrated sweep must
rebuild SDK before frontend typecheck/build, run API/SDK/frontend tests and exercise real
provider confirmation. No execution or external chain success is claimed here.

## Review fixes and final endpoint handshake

Core author confirmed discovery, request lookup/cancel, refund and fee routes landed.
Legacy /api/Nft mint/transfer/burn all take body feeAuthorization; UserNFT DELETE
uses query authorization. Credential revoke 202 now preserves a success envelope
with isConfirmed false and lifecycle evidence. AwaitingRefundDestinationAcceptance
requires the original requester to accept before holder-authorized return.

Root's client review found stale fee/account responses and key-only recovery deadlocks.
Responses are now generation-fenced across account/asset/chain changes and unmount;
old discovery rows are hidden/reset. Request cancellation uses the server's atomic
key tombstone, never absence. Lost first responses recover via key lookup before
terminal correlation clears local storage; concurrent replacement is preserved.
Added cancellation-conflict/tombstone, lost-response correlation, absent/wrong-key
retention, late old-chain quote and late old-account discovery regressions.

Additional files: shared use-nft-request-scope.ts, nft-request-recovery.ts and test,
nft-cancel-request.tsx and test. Root's JSX brace finding was corrected. No tests run.
