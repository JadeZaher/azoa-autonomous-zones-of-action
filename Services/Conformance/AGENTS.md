# Services/Conformance

## node-conformance-manifest L0

This is the local-only, pre-federation proof surface. It deliberately contains
no Holochain runtime/client/conductor package, DHT publication, peer handling,
cross-node resolution, private SurrealDB data, PII, wallet keys, or value
authority. The sole public result is a bounded, signed document at
`/.well-known/azoa-node.json`; it is absent (404) unless an operator opts in
with all required configuration and fresh evidence.

### Evidence source

`TrxNodeConformanceEvidenceSource` accepts exactly five CI-produced TRX files:
`G1.trx`, `G2.trx`, `G3.trx`, `G5.trx`, and `G7.trx`. Each must contain only
passing results and at least one matching gate test name. The service calculates
the SHA-256 digest itself and includes only gate, digest, and count in the
public document. It does not accept operator-entered pass/fail claims. CI must
mount these output files read-only into `NodeConformance:EvidenceDirectory`.
The document is intentionally unavailable when artifacts are stale, missing,
or failed; current code requires a separate deployment freshness policy before
the L0 track can close.

### Node identity, rotation, and restore

`ProtectedFileNodeIdentityKeyService` uses a distinct NIST P-256 identity key.
It never receives a wallet, mnemonic, chain provider, or custody abstraction.
The PKCS#8 private key is protected by ASP.NET Data Protection and stored only
in `NodeConformance:KeyStoragePath/node-identity.v1.protected`; the public key
is SubjectPublicKeyInfo with a `sha256:` key id. The node identity file and the
Data Protection key ring are one recovery set: restore both atomically from a
verified backup with the same `DataProtection:ApplicationName`, then verify the
previous well-known document offline before serving a new one. Restoring only
one member fails closed.

Rotation is an explicit service operation. The former key signs a
domain-separated canonical statement over the successor key; `PreviousKey`
ships in the next document and the offline verifier demands that continuity
when a previous document is supplied. Do not silently replace the protected
file, reuse custody keys, or expose key rotation as an unaudited public route.

### Canonical protocol and limits

Canonical JSON has fixed property order, UTC seven-digit timestamps, sorted
gate IDs, and no pretty printing. Signatures are ECDSA P-256/SHA-256 over
domain-separated canonical bytes. The verifier rejects malformed keys,
unsupported versions, evidence changes, invalid signatures, expiry, and broken
rotation. Document lifetime is capped at 24 hours and payload size at 64 KiB;
the deployment default is 16 KiB. Any protocol change requires new schema and
domain versions with independent golden vectors.
