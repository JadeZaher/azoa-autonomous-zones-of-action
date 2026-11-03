# Integration conformance gates

## G1 storage contract

The release/CI engine comes only from deploy/railway/surreal-contract.json: immutable production3.2.0 and SurrealKV with sync=every on a persistent /data volume. The actual G1 test remains ACKed store writes ? SIGKILL ? restart ? exact row comparison; an engine's startup flag is not runtime crash proof. The static gate independently enforces the synchronous release URI and fresh-template binding.

The default local docker-compose.dev.yml deliberately remains the legacy3.1.4 RocksDB profile to preserve existing developer volumes. Its SURREAL_SYNC_DATA flag still provides that profile's per-commit posture; it is not the SurrealKV switch. No test or normal release silently converts an existing volume, changes production options or starts a new empty database. The Chaos category remains excluded from normal integration but is explicitly executed/exported by CI conformance. Test-owned namespace/container selection and exact cleanup semantics remain unchanged.

## Static G1 fixture isolation

G1_DurabilityConfigurationTest is a plain Fact class, with no IntegrationTestBase, host, fixture, HTTP or container dependency. Its static assertion and repo-root helper moved unchanged from the runtime class. G1_CrashDurabilityTest retains only real ACK/crash evidence and its existing base/runtime behavior. Both remain Category=Chaos and CI explicitly selects both classes into G1.trx. CI exporter and runtime evidence reader require exactly the two named methods, passed once each: static-only, crash-only, duplicates, old static identity or extra methods fail closed. A local static pass never substitutes for the real3.2/SKV crash gate.
