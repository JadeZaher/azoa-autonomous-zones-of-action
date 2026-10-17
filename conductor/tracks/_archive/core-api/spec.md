---
type: Track Spec
title: "Core API — Specification"
tags: [core-api, archived]
---

# Core API — Specification

## Goal
Establish the foundational abstractions and shared infrastructure used by all higher-level tracks (User, Holon, Action Graph Bundle, Blockchain).

## In-Scope
- `IAZOAStorageProvider` interface and base result types (`AZOAResult<T>`, `AZOAResponse`)
- `ProviderContext` / provider activation logic
- `BlockchainProviderFactory` and `IBlockchainProvider` contracts
- `AZOADbContext` (EF Core) with JSON value converters
- Unified `AZOARequest` / `AZOAResult` serialization patterns
- `BlockchainOperationBuilder` fluent API

## Out-of-Scope (per product.md)
- Karma / reputation system
- HoloNET / Holochain bridge
- gRPC / GraphQL / CLI endpoints
- Unity / JavaScript client SDKs
- Hot-swappable plugin loader (MEF)
- Full HyperDrive v2 auto-failover engine

## Acceptance Criteria
- [x] Base interfaces compile and are implemented by all providers
- [x] `AZOADbContext` handles User, Wallet, Holon, BlockchainOperation, ActionGraphBundle
- [x] Provider switching works via `ProviderContext.Activate`
- [x] Stryker mutation score for core files ≥ 50 %
