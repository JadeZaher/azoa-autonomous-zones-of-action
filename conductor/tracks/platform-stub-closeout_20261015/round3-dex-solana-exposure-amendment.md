---
type: design
---

# Solana external-signature recovery

Status: Root accepted the design with explicit complete-history coverage requirements; no observer API change yet. The produced-block ancestry refinement below is the implementation contract.

Exposing the exact compiled message is potential submission even though its transaction signature is not yet known. The journal retains the existing exposure fence, full immutable message, owner, genesis, blockhash and last-valid block height. A missing upload never restores unsent cancellation semantics.

The provider observer takes an optional prior, provider-validated checkpoint from the saved observation. It scans finalized `getSignaturesForAddress` history for the exact required owner, who must be the sole payer/signature, then retrieves candidate transactions as actual base64 wire. A candidate only matches after real Ed25519 verification and exact equality to the complete prepared message. An unrelated signature, owner activity, missing transaction body, or RPC failure cannot establish settlement or absence. A matching candidate is adopted with its actual signature and proceeds through the same finalized receipt validator as a server-signed candidate.

The first pass pins a finalized upper slot after the original blockhash has expired; before expiry it may discover a matching transaction but cannot establish absence. History is scanned through the original preparation context slot, inclusive. The checkpoint binds the exact plan hash, genesis, owner, expired blockhash/height, fixed upper boundary, next cursor and visited cursor identities. Results above the fixed boundary are ignored for economic decisions but still advance traversal. A cursor cycle, out-of-order slot sequence, mismatching response identity or missing candidate body keeps the stage unresolved and retains all reservations.

One pass processes a bounded page. There is no lifetime page-count cutoff. The persisted continuation is reused across worker passes and participant refresh. Address history is positive-discovery evidence only: neither an empty page nor `minimumLedgerSlot`/`firstAvailableBlock` proves that address indexing omitted no transaction.

Expired absence instead traverses complete produced-block ancestry. Pin a finalized upper block whose `blockHeight` exceeds the saved last-valid height. Fetch each block with `getBlock`, `encoding: base64`, `transactionDetails: full`, `rewards: false`, `commitment: finalized`, `maxSupportedTransactionVersion: 0`. Require its exact expected block hash and each predecessor's `blockhash` equal the child's `previousBlockhash`, following strictly decreasing `parentSlot` until below the original context slot. All transactions in every traversed block are inspected, including the inclusive lower-bound block if produced. Skipped slots are accounted for by actual parent links, not by an address query's omission. Missing/pruned blocks, unavailable transaction arrays, unsupported transaction versions, malformed ancestry or inconsistent finalized headers retain unresolved state and reservations. `getBlocks` is not used as an absence shortcut. The official RPC captures under `.omc/research/round3-dex-solana-{getblock,getblocks,firstavailableblock,minimumledgerslot}-2026-10-15.md pin the response contracts.

Each pass persists only a bounded number of complete blocks, the original fixed boundary/hash, next parent slot and expected predecessor hash, cumulative inspected interval and plan/genesis identity. A valid matching signed message is adopted immediately; a completed hash-linked traversal beyond expiry is the only unsigned absence proof. An archival node is needed when old block bodies have been pruned, but no configuration assertion substitutes for those bodies.

An actual matching wire may be found before or after expiry and is never replaced with a generated signature. Finalized success/revert evidence remains the signed observer's responsibility. Only the shared no-effect validator may accept a complete plan-bound absence proof for a new generation. Accounting is unchanged while discovery is pending.

Required fixtures: multiple pages with unrelated traffic, old/new boundary filtering, missing transaction body, cursor cycles, pruned history, lost upload followed by real signature recovery, expired complete absence, and replay of a checkpoint belonging to another plan/owner/genesis. The shared interface extension must preserve existing Algorand behavior and domain code must pass only the original persisted checkpoint, never arbitrary client evidence.
