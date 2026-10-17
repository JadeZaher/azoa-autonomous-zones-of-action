# Changelog

All notable changes to AZOA (WebAPI + `azoa-sdk`) are recorded here. This
project is pre-launch; versions before `1.0.0` are alpha and the API surface
may still shift. See `conductor/tracks.md` for the full per-track history this
changelog summarizes.

## [Unreleased]

Everything below is on `feat/kyc-aml-providers-value-rail` and unpublished. The SDK
in `sdk/azoa-wallet` is at `0.3.0` in its manifest; nothing has been pushed to the
registry, so the frontend consumes it as `file:../sdk/azoa-wallet`.

### Changed — BREAKING

- **Platform terminology neutralized** (`refactor!: 836f549`). The domain nouns were
  renamed across C#, the SurrealDB schema, REST routes, the SDK and the frontend. The
  C# entity is `ActionDefinition` and its namespaces are plural (`Models.Actions`) to
  avoid colliding with `System.Action` under `ImplicitUsings` (D35) — but the DB table,
  the REST route segment and the SDK type are all plain `action`, so the **wire surface
  is unchanged by the C# suffix**. Consumers pinned to the old nouns must migrate.

### Added

- KYC/AML control plane with a versioned policy-provenance envelope, plus an
  `auth/oauth` external-identity surface (`ExternalAuthController`).
- A third chain provider (**Ethereum**, EIP-2718 type-2 signing, `BigInteger`-only wei
  arithmetic) alongside Algorand and Solana.
- The **value-rail seam**: `IValueRailProviderFactory` resolves a bound provider for a
  rail whether or not that rail is a chain (D9), so a fiat settlement partner is
  expressible without pretending to be a blockchain.
- `AnalyticsController` — `GET /api/analytics/transactions`, `GET /api/analytics/positions`.
- `CredentialController` — `POST /api/credential/{assetId}/revoke` (see the caveat below).
- Rebuilt visual action builder in the frontend.

### Landed but NOT enabled — do not describe as done

- **Governance vote / tally (D20)** — shipped in `a59bd85` with a **live Sybil hole**:
  the vote node claims through the person-uniqueness gate, but the tally counts raw
  child holons and never reads the claim ledger, so ballots posted straight at
  `POST /api/holon` are counted. Two compounding defects: the ballot ordering key comes
  from attacker-controlled holon metadata, and `HolonManager.UpdateAsync` blind-merges
  caller metadata so a cast ballot's option can be rewritten. Must not be enabled until
  `conductor/tracks/governance-vote-of-record_20260906` closes.
- **Credential revocation (D19)** — shipped in `a59bd85` and **inert**. The route
  authorizes the credential's issuer, but the only production mint path stamps the
  issuer as the holder, so the check is vacuous; no production path writes
  `current_owner` on a soulbound row, so a real revoke dies at the holder-address
  precondition first. Corrective: `conductor/tracks/credential-third-party-issuance_20260906`.

Both failed the same way — *a control is not wired until the thing that reads its
result is wired too.* As of `7b28a16` the refusal is a control rather than a claim:
`Services/Actions/NotEnabledActionNodeTypes.cs` blocks `GovernanceProposalCreate`,
`GovernanceVote`, `GovernanceTally` and `CredentialRevoke` at publish and at both
dispatch seams. It gates **action node types**, not the REST surface — the credential
revoke route stays reachable and inert.

### Fixed

- Rename fallout: `ActionDefinition` suffix leaks in routes and prose, stale path
  citations, article agreement and doubled words (`c089268`, `d58d14b`).
- Frontend builds the SDK from source instead of a stale registry pin; Turbopack root
  points at the repo rather than `frontend/`.

## [0.1.0-alpha] — 2026-07-05

Initial alpha cut. Everything in this release is pre-launch: the only actions
remaining after this tag are operator actions (`railway up` + secret/guardian-set
provisioning), documented in `docs/NODE-HOST.md`.

### Core platform
- Self-sovereign user identity (self-owned identities, external-wallet
  challenge-signature auth) with a revocable, live-checked tenant-consent model
  (`KeyCustodyService` + `TenantConsentGate`).
- Durable Action workflow engine: DAG-defined multi-step workflows (gates,
  transfers, swaps, grants, external calls) that survive restarts, reconcile
  against chain truth before retrying a chain-action node, and never
  double-spend. Runs execute through the HTTP API with the saga hosted service
  on (`Sagas:Enabled=true` is the deliberate default, boot-guarded).
- Action Graph Bundle dapp generator (released as STAR-ODK), including the ecosystem-tree composition model
  (`Ecosystem`/`EcosystemNode`, multi-dApp series codegen).
- Holon storage model (NFTs, fungible tokens, and generic assets modeled as
  holons) with cross-provider search, mint, exchange, and an opt-in
  `HolonTypeRegistry` for typed asset metadata.
- Cross-chain bridge: real Algorand lock/burn value primitives, user-scoped
  idempotency keys, WHERE-guarded VAA replay protection, and a kill switch
  (`RealValueEnabled`) gating any real-value path. The Wormhole
  `Secp256k1VaaSignatureVerifier` is the only VAA-proof verifier in the tree —
  the always-true provider-level stub was removed.
- Fractionalization flow: `Bridge`/`Back` Tier-2 action nodes plus a canonical
  parameterized fractionalization action template.
- SurrealDB is the sole persistence engine (Postgres/EF/in-memory fallbacks
  removed); backed by SurrealForge (`SurrealForge.Client`/`.Schema`/`.Analyzer`)
  for typed queries, schema generation, and safe-SurrealQL enforcement.
- Cross-platform `azoa-sdk` TypeScript SDK: client-side signing, multi-chain
  wallet management, DEX adapters, holon/action/workflow API clients.
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
