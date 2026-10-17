---
type: Review
status: Bounded source accepted; runtime unverified
---
# Independent AVM precision and provenance review

Lane C reviewed root-authored UInt64Text, NativeArtifacts foreign identifiers, AlgorandProvider.NativeArtifact/CompilerIdentity, SDK deployment foreign ID strings and the focused AlgorandNativeOperationTests additions. Canonical uint64 decimal text survives JSON and converts only at the wire boundary. Configured algod build/genesis/protocol evidence brackets compilation; this is explicitly node-reported provenance, not independent compiler attestation.

The review found null box-element validation and unstable nested caller provenance. Root corrected both. Fresh admission discards untrusted caller provenance, repeated uploaded-bytecode normalization is stable, and a reuploaded compilation result becomes uploaded bytecode. Original immutable stored artifact receipts retain the original server compilation evidence. Only generation calls validation, so read/quote/recovery of that stored receipt does not relabel it.

Source acceptance is limited to the inspected slice. Before/after compiler-change, precision and repeated-upload fixtures were inspected but not executed. No build, chain protocol fixture or runtime gate ran.
