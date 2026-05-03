# Tracks

| Track | Status | Description |
|-------|--------|-------------|
| [core-api](tracks/core-api/spec.md) | `[x]` | Unified provider pattern, base abstractions, OASIS result/response models |
| [avatar-api](tracks/avatar-api/spec.md) | `[x]` | Avatar controller (register, login, CRUD, provider overrides) — OAuth-like identity layer + multi-wallet |
| [holon-api](tracks/holon-api/spec.md) | `[x]` | Holon controller (CRUD, query, cross-provider search, mint, exchange) — NFTs as storage-backed holons |
| [star-api](tracks/star-api/spec.md) | `[x]` | STAR dapp-generator API (scaffold, configure, deploy dapps that operate on holons) |
| [startup-config](tracks/startup-config/spec.md) | `[x]` | Program.cs / Startup.cs wiring, Swagger, JWT, middleware, manager DI |
| [tests](tracks/tests/spec.md) | `[x]` | 256 tests green (215 unit + 41 integration). Stryker mutation score 59.41 % (break 50 %) |
| [wallet-api](tracks/wallet-api/spec.md) | `[ ]` | First-class Wallet API with CRUD, portfolio analytics, and default-wallet management |
| [nft-api](tracks/nft-api/spec.md) | `[ ]` | Semantic NFT layer (mint, transfer, burn, metadata) built on Holon infrastructure |
| [search-api](tracks/search-api/spec.md) | `[ ]` | Unified cross-entity search with pagination, filtering, and faceted results |
| [validation-mapping](tracks/validation-mapping/spec.md) | `[ ]` | FluentValidation input pipeline + AutoMapper entity-DTO mapping layer |
