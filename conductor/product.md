# Product: AZOA WebAPI — Lightweight

## Vision
A clean, minimal ASP.NET Core WebAPI that exposes the AZOA Avatar API, Holon API, and STAR Dapp-Generator API without the legacy Karma module. AZOA acts as an **OAuth-like cross-chain user registration and asset exchange tool**, enabling avatars to own, query, and orchestrate interactions across a universe of assets stored on-chain (including NFTs as storage holons). The API should be easy to reason about, fast to bootstrap, and built around a single unified provider pattern.

## Target Users
- Developers integrating AZOA Avatar / cross-chain identity services
- Teams that need a lean, provider-swappable backend for multi-chain asset management
- Dapp creators using STAR to generate and deploy applications that orchestrate holonic assets

## Key Features
1. **Unified Provider Pattern** — one way to activate, switch, and chain storage providers (including on-chain/NFT providers)
2. **Avatar API** — Register, Login, Get, Update, Delete avatars with JWT auth (OAuth-like identity layer)
3. **Holon API** — Query and operate on holons across all providers; NFTs function as storage-backed holons
4. **STAR API** — Dapp generator: scaffold, configure, and orchestrate dapps that interact with the holon universe
5. **Swagger/OpenAPI** — Auto-generated docs for all public endpoints
6. **No Karma** — Karma endpoints, services, and models are excluded

## Success Metrics
- Builds clean with `dotnet build`
- All endpoints reachable via Swagger UI
- Provider switching works via request headers or query params
- No Karma-related code in the compiled output
