# Changelog

All notable changes to AZOA (WebAPI + `@azoa/sdk`) are recorded here. This
project is pre-launch; versions before `1.0.0` are alpha and the API surface
may still shift. See `conductor/tracks.md` for the full per-track history this
changelog summarizes.

## [0.1.0-alpha] — 2026-07-05

Initial alpha cut. Everything in this release is pre-launch: the only actions
remaining after this tag are operator actions (`railway up` + secret/guardian-set
provisioning), documented in `docs/NODE-HOST.md`.

### Core platform
- Self-sovereign avatar identity (self-owned avatars, external-wallet
  challenge-signature auth) with a revocable, live-checked tenant-consent model
  (`KeyCustodyService` + `TenantConsentGate`).
- Durable Quest workflow engine: DAG-defined multi-step workflows (gates,
  transfers, swaps, grants, external calls) that survive restarts, reconcile
  against chain truth before retrying a chain-action node, and never
  double-spend. Runs execute through the HTTP API with the saga hosted service
  on (`Sagas:Enabled=true` is the deliberate default, boot-guarded).
- STAR-ODK dapp generator, including the ecosystem-tree composition model
  (`Ecosystem`/`EcosystemNode`, multi-dApp series codegen).
- Holon storage model (NFTs, fungible tokens, and generic assets modeled as
  holons) with cross-provider search, mint, exchange, and an opt-in
  `HolonTypeRegistry` for typed asset metadata.
- Cross-chain bridge: real Algorand lock/burn value primitives, avatar-scoped
  idempotency keys, WHERE-guarded VAA replay protection, and a kill switch
  (`RealValueEnabled`) gating any real-value path. The Wormhole
  `Secp256k1VaaSignatureVerifier` is the only VAA-proof verifier in the tree —
  the always-true provider-level stub was removed.
- Fractionalization flow: `Bridge`/`Back` Tier-2 quest nodes plus a canonical
  parameterized fractionalization quest template.
- SurrealDB is the sole persistence engine (Postgres/EF/in-memory fallbacks
  removed); backed by SurrealForge (`SurrealForge.Client`/`.Schema`/`.Analyzer`)
  for typed queries, schema generation, and safe-SurrealQL enforcement.
- Cross-platform `@azoa/sdk` TypeScript SDK: client-side signing, multi-chain
  wallet management, DEX adapters, holon/quest/workflow API clients.
- MCP surface (`/mcp`) exposing read-only tools behind the same JWT/API-key
  auth as REST.

### Deferred / known-disabled (fail-closed, not launch-blocking)
- Real Solana SPL, Wormhole sequence-parsing, and Ethereum secp256k1 value
  routes stay disabled (`RealValueEnabled=false`) until their follow-up work
  lands — see `docs/NODE-HOST.md` §4.1.
- KMS/HSM custody, distributed (multi-instance) rate limiting, and a few other
  post-alpha hardening items are tracked as explicit follow-ups, not silent
  gaps — see `conductor/tracks/_archive/final-hardening-cutover/spec.md` §H-followups.

### Alpha-gate hardening (Phase H, this release)
- Simulated-mode blockchain provider is now fail-fast in Production
  (`Blockchain:Mode=Simulated` throws at boot outside Dev/IntegrationTest).
- Version stamps added (this changelog, WebAPI assembly version, SDK package
  version) and the SDK package now ships its own `LICENSE` file
  (Apache-2.0, matching root).
- Minimal CI added (`dotnet build` + unit tests + SDK build/vitest on
  push/PR — see `.github/workflows/ci.yml`).
- Documentation drift swept: stale `.NET 8` / SurrealDB `1.5.4` references
  corrected to `.NET 10` / SurrealDB `3.1.4`; deprecation banners added to
  superseded operator docs pointing at `docs/NODE-HOST.md`.
