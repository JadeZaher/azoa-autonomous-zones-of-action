---
type: review
---

# Round 1 independent recovery review

Reviewer: root, separate from the lane authors. Static review in progress; no runtime approval or test execution is implied.

## Identity

The new durable submission precedes provider creation, and the send transaction fences current attempt and tenant/provider authority. This removes the callback-before-submission ordering gap for new attempts.

Findings sent to Lane A:

- A provably-unsent operator release retains the hosted row ID while renaming its admission key. Per-attempt retries reuse that ID, so CREATE conflicts without finding the old admission key. Preserve audit while providing a safe retry transition or a proven-unsent attempt retirement path.
- Ownership classification must compare the creation token, not only the session ID: per-attempt concurrent callers share an ID but do not own the same creation token. Fresh versus aged reconciliation status must remain accurate.
- Legacy active hosted records need explicit identifier-shape handling before exact GUID parsing. Unknown previous sends cannot be silently recreated.

Required evidence includes actual database alias collision, pre-send authority changes, operator release/retry, and concurrent creator tests.

Lane A addressed the three findings: released-unsent admission is reclaimed under a new token and monotonically increasing version while retaining audit; ownership checks compare ID and token; legacy correlations return a resumable redirect or explicit reconciliation state. Root inspected those changes and the authored database regression coverage. No remaining concrete defect identified in that bounded static pass. Database execution remains pending.

Subsequent review found two additional issues and the author corrected them: reclaimed admission age now uses a distinct creation-generation timestamp rather than its preserved original creation date; direct-user send fencing now uses an explicit tenant-presence boolean rather than equating a bound null parameter with SurrealDB NONE. A positive unexpired node-default send-ownership database regression was added, supplementing the earlier negative authority cases. These tests are authored and remain unexecuted.

## Fiat

The recovery service caps restitution by authoritative cumulative funding returns, reserves a stable target/key, and re-reads bank payout state before restitution. These are appropriate static boundaries, subject to integration verification.

Findings sent to Lane B:

- ReturnUnitsAsync currently logs a reserve accounting draw failure and returns success. That can finalize recovery and clear its reservation before accounting repair. Preserve the reservation and retry the same completed transfer key to repair accounting.
- Pending reserve transfers must retain a durable transaction identity and reconcile chain confirmation. An error string and failed claim alone cannot support eventual safe completion.
- Add regressions for bank state changing between reads, compare-and-swap loss, and transfer success followed by accounting failure/retry.

The earlier pending issuance finding is being corrected in AllocationManager and OnRampSettlement: a transaction hash alone cannot prove completed issuance. Tests must supply authoritative operation state rather than synthetic success.

Root updated the OnRampSagaTests fixture with a stable operation ID and authoritative operation-store response, and authored pending-to-confirmed and terminal-without-hash regressions. Lane B independently reviewed that test change and found its state/key assertions consistent with the settlement implementation. Tests remain unexecuted.

## NFT shared persistence

Reviewed the generated-statement parameter scoping helper independently. Its quote-aware token handling avoids rewriting parameter-like text in literals and rejects unsupported input. No concrete static defect identified in that pass. Actual typed writer/query and database composition regressions remain required; this is not runtime approval.

## NFT lifecycle review findings

Root sent the following concrete findings to Lane C; corrections are in progress:

- Preserve the mint's nontransferable policy instead of projecting every nonsoulbound asset as transferable.
- Use the transfer signing scope for transfers, and recheck governance before resumed preparation.
- Reserve the current asset authority before movement preparation so distinct request keys cannot concurrently move the same asset and strand a confirmed operation behind a stale authority version.
- Verify observation identity against the persisted prepared transaction before copying authoritative outcome fields.
- Complete recipient opt-in and transferred-ASA destruction journeys using the actual participant's authorization. Returning an unsupported error does not implement those journeys.

Delivery is staged: complete Algorand and all .NET entry points/provenance/revocation, then independent review alongside SDK/UI work, then Ethereum and Solana provider modules against the same capability. Those modules remain explicit implementation work. Round 1 has not completed its acceptance criteria.

## Restitution persistence follow-up

Persisting a pending transaction only on the versioned payout row leaves a gap if that write loses a race or the process stops after broadcast. Lane B is implementing a provider-owned preparation boundary that persists exact signed bytes and transaction identity before egress. An unsupported-chain refusal remains a safety boundary until the corresponding concrete module is implemented; it does not close recovery parity.

Pinned SDK source review confirmed a concrete semantic validation issue: Algorand's `AssetClawbackTransaction` is a subtype of `AssetTransferTransaction`. A positive transfer type check alone also accepts clawback transactions. Both lane authors were instructed to exclude that subtype for ordinary transfers. The package's polymorphic decoding attributes are present in the [pinned transaction source](https://github.com/FrankSzendzielarz/dotnet-algorand-sdk/blob/cc796c56fddc5849f625a6fb02aabf869a06d6bc/dotnet-algorand-sdk/Algod/Model/Transactions/AssetTransferTransaction.cs); exact signed-wire roundtrip tests remain required.

SDK/UI recovery must discover pending operations from the server for each authenticated participant. Browser-local operation references alone cannot serve recipient acceptance, creator destruction, or recovery after a device change. This requirement was sent to the client lane for coordination with the lifecycle author.

## Canonical signed Algorand wire boundary

Pinned Encoder source wraps the complete input in a MessagePackReader and invokes JsonSerializer.Deserialize once without checking exhaustion. Local Newtonsoft package documentation confirms CheckAdditionalContent defaults false. Consequently, validating the first decoded object does not validate trailing signed objects that submission would still send. Both NFT group members and prepared-value wire require exact canonical signed re-encoding equality or an equivalent reader-exhaustion check. Authors were asked to add primary/additional-member trailing-object regressions. This is source evidence; no runtime behavior has yet been claimed.
