# Services/Conformance

## node-conformance-manifest L0

This is the local-only, pre-federation proof surface. It deliberately contains
no Holochain runtime/client/conductor package, DHT publication, peer handling,
cross-node resolution, private SurrealDB data, PII, wallet keys, or value
authority. The sole public result is a bounded, signed document at
`/.well-known/azoa-node.json`; it is absent (404) unless an operator opts in
with all required configuration and an evidence directory. Artifact integrity
is limited to self-computed digests of supplied bytes; CI provenance and
bounded freshness remain required before the L0 track can claim conformance.

### Evidence source

`TrxNodeConformanceEvidenceSource` accepts only a `metadata.json` plus the five
declared TRX files: `G1.trx`, `G2.trx`, `G3.trx`, `G5.trx`, and `G7.trx`. The
metadata schema, ordered file set, lower-case SHA-256 digests, repository,
workflow, runtime assembly source revision, timestamps, and freshness must all
match configured trust. Each TRX must contain only passing results from its
expected fully-qualified gate test class; `G3.trx` must contain exactly the two
non-skippable runtime injection proofs. Mixed or full-suite artifacts are
rejected. The service calculates every digest from the bytes it parses and
includes only gate, digest, and count in the public document. It does not
accept operator-entered pass/fail claims. The deployment must provide these
files read-only in `NodeConformance:EvidenceDirectory`; missing, expired, or
untrusted evidence makes the document unavailable. The manifest service caps
its own expiry at the evidence validity boundary and refuses an already-expired
snapshot; a signed descriptor must never outlive the CI proof it reports.

Trusted push CI produces a metadata-bound tarball containing these five files
and an artifact attestation. Upload integrity alone is not deployment provenance:
a protected promotion step must verify the expected repository, protected `main`
commit/ref, signer workflow identity, attestation, metadata hashes, and bounded
freshness before mounting the extracted files read-only and enabling the endpoint.

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
