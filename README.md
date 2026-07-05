# AZOA — Autonomous Zones of Action

**AZOA is an open-source engine for building financial workflows that are highly
dynamic but strictly structured.** You describe a multi-step process — who acts,
what moves, under what conditions, in what order — and AZOA runs it durably to
completion, settling value across whichever rails the workflow touches:
blockchains, fiat, or both.

It is built from three composable layers:

- **Identity** — a self-sovereign avatar that a person actually owns, and a
  consent model that lets an app act on their behalf only within explicit,
  revocable permission.
- **Quests** — durable, structured workflows. A quest is a graph of steps
  (gates, transfers, swaps, grants, external calls) that survives restarts,
  reconciles against real-world truth before acting again, and never
  double-spends.
- **Dapps (STAR)** — a generator that scaffolds and deploys applications on top
  of the identity and quest layers, so a new product doesn't rebuild the
  plumbing.

Bridges connect these workflows to the outside world: value can enter and leave
a quest over a blockchain or over a fiat settlement partner, through the same
structured, idempotent seam.

## Why it exists

Financial workflows want two things that usually fight each other: they need to
be **dynamic** — branch on conditions, wait for external events, span multiple
systems and days — and they need to be **structured** — auditable, idempotent,
never running a step twice or losing one halfway. Ad-hoc scripts and cron jobs
give you the first and none of the second.

AZOA is the substrate for the second. A quest is a durable state machine: it
parks when it needs to wait, resumes on a signal or a timer, and — critically —
reconciles against the source of truth (a chain confirmation, a settlement
callback) *before* it takes the next irreversible action. The identity layer
makes every action attributable to a real, self-owned subject, and the consent
layer makes delegated action safe. The result is a workflow you can trust with
money.

## Rails: blockchain and fiat, same shape

AZOA treats a blockchain and a fiat partner as two kinds of settlement rail
behind one uniform interface. On-chain assets — tokens, NFTs — are modeled as
**holons** and moved through provider adapters. Fiat is a **thin seam**: a
settlement tenant runs its own checkout and payment flow, and *after* money has
cleared, makes one idempotent, KYC-gated call into AZOA to provision a wallet or
allocate an asset. AZOA never runs the checkout, holds card data, or owns the
payout — it materializes the value once and records it exactly once.

A quest doesn't care which rail a step settles on. That is the point: the same
structured workflow can move on-chain value and fiat-originated value in the
same run.

## The operator responsibility pattern

AZOA is deliberately honest about the line between what the software can
guarantee and what a human operator must own. The engine makes the *safe* path
automatic and the *ambiguous* path explicit:

- **Exactly-once by construction** — idempotency claims, atomic state
  transitions, and replay ledgers mean a retried or duplicated step settles
  once, or not at all. Never twice.
- **Fail closed** — a signature that can't be verified, a consent grant that
  isn't live, a guardian set that isn't configured: the action is refused, not
  waved through.
- **Reconcile, never guess** — the engine re-derives status from the real
  source of truth. It does not auto-reverse or auto-fail an ambiguous result;
  a not-yet-found transaction is left alone, not declared dead.
- **Escalate to a human** — when a workflow is genuinely stuck, AZOA flags it
  for **manual intervention** with full context rather than silently resolving
  it. Some decisions belong to the operator, and AZOA surfaces them instead of
  faking them.

The same principle governs deployment: durability guarantees, guardian-set
configuration, and rail credentials are operator responsibilities that AZOA
documents and gates explicitly (see `RUNBOOK.md` and the residual-risk runbook),
rather than pretending a config it can't verify is safe.

## Open source

AZOA is licensed under the [Apache License, Version 2.0](LICENSE). It is meant
to be self-hosted and extended: new chains plug in via the `ChainProvider`
interface, new DEXes via `DexAdapter`, new workflow steps as quest nodes, and new
settlement rails behind the same bridge seam. Run it yourself, audit it, and own
the responsibility pattern above end to end.

## Status

Pre-launch. The **user-self-sovereignty** initiative has shipped: end users own
their avatars (wallet-challenge login, ed25519), and a tenant may act for a user
only within a live, revocable `ConsentGrant`; the signing custody seam
(`KeyCustodyService`) is consent-gated and fails closed. A security review of the
auth + custody surface is still owed, and the cross-chain bridge value-transfer
primitives are not yet safe for real value. 916 unit tests green (2026-06-22);
integration tests run against a persistent podman SurrealDB. See
`conductor/tracks.md` for the full catalog.

## Under the hood

A quick orientation for contributors:

- **.NET 8 WebAPI** — the AZOA protocol: identity, quests, holons, swaps, STAR,
  bridges. Dual auth (JWT + `X-Api-Key`).
- **SurrealDB** — the sole data engine, via the `Azoa.SurrealDb.*` packages.
  Real-world state (chain confirmations, settlement callbacks) is the source of
  truth; balances are read, never stored.
- **@azoa/wallet-sdk** — TypeScript SDK (`AzoaClient` facade) with pluggable
  `ChainProvider` and `DexAdapter` points.
- **Next.js 14 frontend** — reference UI, including a visual quest builder.

### Repo layout

- `Controllers/`, `Managers/` — HTTP surface and business logic (identity,
  quests, holons, swaps, STAR, bridge, …)
- `Core/` — providers, base classes, auth handlers
- `Persistence/SurrealDb/`, `Providers/Stores/Surreal/` — data engine and
  per-aggregate stores; conventions in `CONVENTION.md`
- `packages/Azoa.SurrealDb.*` — SurrealDB toolkit (C#-first schema authoring +
  Roslyn injection-guard analyzer)
- `sdk/azoa-wallet/` — TypeScript SDK
- `frontend/` — Next.js 14 app
- `conductor/tracks/` — narrative tracks documenting every shipped feature and
  the decisions behind it
- `tests/` — xUnit unit + integration projects

## Getting started

**Prerequisites:** .NET 8 SDK, Node 20+, podman or Docker (for SurrealDB).

```bash
# Backend
dotnet restore && dotnet build

# SDK
cd sdk/azoa-wallet && npm install && npm test

# Frontend
cd frontend && npm install && npm run dev
```

**Full stack (SurrealDB + WebAPI + Frontend):**

```bash
./dev-up.sh          # or ./dev-up.ps1 on Windows
```

This brings up SurrealDB, the WebAPI, and the frontend via
`docker-compose.dev.yml`, then applies the schema. Tear down with
`./dev-down.sh`.

## Docs

- `PROVIDERS.md` — API surface and provider architecture
- `API_SYNC.md` — Controller ↔ SDK regression mapping; read before shipping
  controller changes
- `DEVELOPMENT.md` — Developer setup, dev-up variants, conventions,
  troubleshooting
- `RUNBOOK.md` — Operations: local stack control, production deploy, diagnostics
- `conductor/tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md` — Operator
  runbook: exactly-once guarantees, reconciliation, manual-intervention gates
- `conductor/product.md` — Product vision and goals
- `conductor/tracks.md` — Feature track catalog and status

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
