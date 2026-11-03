# Database contract consumption

CI dotnet, schema-job, perf and E2E jobs load the checked-out
deploy/railway/surreal-contract.json through surreal_contract.py after checkout.
The first three launch the same immutable linux/amd64 image and explicit
SurrealKV sync=every storage URI from those safe GITHUB_ENV exports. Each checks
the actual container's public binary version before readiness/test work; image
labels alone are insufficient. No legacy SURREAL_SYNC_DATA switch is relied on.

The E2E job loads the same contract, and its native Node launcher independently
reads that exact checkout so local Windows E2E needs no new Python dependency.
Compose image/storage variables have no tag or engine fallback. This contract
scope does not migrate, reset or relabel existing development RocksDB volumes.
The isolated CI/E2E stores are fresh fixtures; live promotion retains its separate
identity/storage/version gates and cannot infer compatibility from health alone.
