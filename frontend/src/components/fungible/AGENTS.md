# Fungible creation client

Keep admission descriptors, original keys and versioned commands in actor-scoped local recovery storage before network calls. A new distinct admission is explicit; response loss only replays the preserved request. Server discovery is required because browser storage is not authoritative. Parent keys the panel by actor to discard stale asynchronous responses on account switch.

Display quote amounts as exact atomic-unit text with currency, scale, payer, network and expiry. Admission and quote acceptance are separate from signing continuation. Finalized asset evidence and optional AttachmentPending are distinct. A current-owner attachment uses only the original projection/asset/Holon identity and keeps its own persisted command.

A lost quote acceptance can be superseded only by successful server quote replacement at a newer version while still AwaitingConsent. That CAS serializes against old acceptance; merely clearing local state or interpreting absent responses is forbidden. Genesis has separate actual operator authority and no ordinary cancellation route. No tests or runtime gates have yet run in the closeout authoring phase.

## Saved command reconciliation

Participant and genesis clients can explicitly reconcile an uncertain versioned command using an authenticated read of the exact operation. Only a strictly greater durable version fences a previously uncommitted CAS. Equal/older versions, different operation or attachment authority, unavailable reads and unsafe numeric versions preserve the command. The client archives original command plus observed state before retiring the pending slot. This does not withdraw a previously committed authorization or hide its effects; the current operation remains displayed. Attachment reconciliation additionally pins the original finalized projection authority. Original issuance keys/definitions are never replaced by this mechanism.
