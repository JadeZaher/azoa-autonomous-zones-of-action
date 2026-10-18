---
type: qa-accounts
round: 4
environment: railway-ardanova-production
created: 2026-10-18
---

# Live QA test accounts (Railway production, round 4)

No passwords, API keys or tokens are kept here. The secrets are in the gitignored root `.env`.

**Why these are wallet accounts, not email/password accounts.** Production refuses
`POST /api/user/register` (403 "Public self-registration is disabled on this node"):
`Registration__AllowPublicSelfServiceInProduction` is not set, and `/auth/oauth/providers` returns `[]`.
The only way left to create an account is Solana wallet sign-in (`POST /api/user/auth/challenge` →
`/auth/verify`), which creates a self-owned user. That this works at all is finding H-A in
`round4-post-qa-deferrals.md`. Each account signs in with an ed25519 key (no password). The email
shown below was set afterwards through `PUT /api/user/{id}`. Accounts have no usable password, so
email/password login is impossible for them.

How to sign in from a script: load `QA_ACCOUNT_<NAME>_SOLANA_SECRET_PKCS8` (base64 PKCS8 DER ed25519),
call `POST /api/user/auth/challenge {address, chainType:"solana"}`, sign `result.message` (UTF-8, raw
ed25519) and send `POST /api/user/auth/verify {address, chainType, signature(base64), message}`.
To get a browser session, put `azoa_token` / `azoa_user_id` in localStorage. The helper is the
session scratch `qa/lib.cjs` (`walletLogin`, `sessionPage`).

| Name | User id | Solana auth address (public) | Stored username | Email (profile) | Role(s) | Tenant link | API key (prefix only) | Used for | Created | `.env` keys |
|---|---|---|---|---|---|---|---|---|---|---|
| USER1 | `e98a36ef-ba65-8cbd-3dc0-402d78ddf7c4` | `9jhEzRLoFFm7UmvobT2ZV8kc3fePTzDBZH6VV6KEM9HR` | `wallet-solana-9jhezrloffm7-e98a36ef` | qa.user1+r4@azoa.test | `dapp:user` | none | `azoa_83dd1631b5f` (UI-created, scope `kyc:read`, **revoked**) | dashboard crawl, wallet create (Algorand platform wallet `ISXHZ5FL…`), KYC start, funds/cash-out, API keys, sign-out, cross-user target | 2026-10-18 | `QA_ACCOUNT_USER1_EMAIL`, `QA_ACCOUNT_USER1_SOLANA_SECRET_PKCS8` |
| USER2 | `ace4a65d-7819-49ca-f59a-c7ef68fc28a3` | `5gPSjKr77XH64kCXYQhfmd3GRSbBZ8gcrv8xVjiFaUcX` | `wallet-solana-5gpsjkr77xh6-ace4a65d` | qa.user2+r4@azoa.test | `dapp:user` | none | none | cross-user probes, profile-save UI, "log out of all devices" | 2026-10-18 | `QA_ACCOUNT_USER2_EMAIL`, `QA_ACCOUNT_USER2_SOLANA_SECRET_PKCS8` |
| TENANT | `043a8958-3a29-8638-284f-1b53ea3e22bc` | `2Gha8GgC3aWm2nhZ89brnec5PGhKCJS4cmGgzrvLzb1J` | `wallet-solana-2gha8ggc3awm-043a8958` | qa.tenant+r4@azoa.test | `dapp:user` + tenant principal (holds a tenant-scoped key) | is the tenant (tenant id = user id) | `azoa_f94c4de26dc` (operator-minted via `POST /api/apikey/tenant`, scopes `tenant:provision,wallet:manage,kyc:read,kyc:submit`, 30-day expiry, **active**) | KYB page, tenant KYC provider page, payout-balance top-up, custodial-account capabilities and provisioning (refused KYB_REQUIRED) | 2026-10-18 | `QA_ACCOUNT_TENANT_EMAIL`, `QA_ACCOUNT_TENANT_SOLANA_SECRET_PKCS8`, `QA_ACCOUNT_TENANT_API_KEY` |
| DAPPDEV | `187ebe05-b022-021a-61d4-b95d6b38196d` | `5xoveF2tcP8hV8aFfuwKLv74aCxo5ncb3kukX4kXwK9E` | `wallet-solana-5xovef2tcp8h-187ebe05` | qa.dappdev+r4@azoa.test | `dapp:developer` (assigned by the node operator through `PUT /api/user/{id}/dapp-role`) | none | none | action builder: draft `qa-dappdev-draft` (`722fbace-df55-4ea0-a095-381535a22527`, private, Draft, validated) | 2026-10-18 | `QA_ACCOUNT_DAPPDEV_EMAIL`, `QA_ACCOUNT_DAPPDEV_SOLANA_SECRET_PKCS8` |
| MANAGER | `4bf931d6-8c80-3320-4fa8-794897eaf79d` | `BrVfSAL2oTr1hT1P19TMbUfcY1fU2g7xpGj6wApv7hWf` | `wallet-solana-brvfsal2otr1-4bf931d6` | qa.manager+r4@azoa.test | `dapp:manager` (operator-assigned) | none | none | `/admin/*` dashboard pages (all refused 403) | 2026-10-18 | `QA_ACCOUNT_MANAGER_EMAIL`, `QA_ACCOUNT_MANAGER_SOLANA_SECRET_PKCS8` |
| node-operator | `a20a0000-0000-4000-8000-000000000001` (reserved) | n/a | `node-operator` | `node-operator@azoa.invalid` (reserved) | `operator:admin`, `node:govern` | n/a | none. Operator identities cannot own keys | operator console, tenant-key minting, role assignment | seeded at first boot | **not in `.env`**: the credential lives only in Railway `azoa-api` vars `NodeOperator__Username` / `NodeOperator__Password` (read at runtime, never written down) |

## Cleanup notes
- To retire the tenant key: `POST /api/apikey/{id}/revoke` as TENANT, or let it expire 30 days after 2026-10-18.
- No real money moved. The tenant payout top-up attempt (25.00) was refused with 503 before reaching a payment page, but the UI kept a pending "Resume that top-up" record in the browser.
- A future DB reset deletes all of these. The `.env` keys stay valid for a fresh reset because wallet sign-in re-creates the same deterministic user id from the same key. Roles, tenant key and emails would then have to be granted again.


## Added 2026-10-18 (post-QA fix deploy `0de1b99d`)

| Name | User id | Email | Role | Created via | `.env` keys |
|---|---|---|---|---|---|
| SMOKE | `5d228a37-b869-4d9c-9768-0c689930a4a8` | qa.smoke+r4@azoa.test | dapp:user | email registration (`POST /api/user/register`, open on the QA node per D-12) — orchestrator smoke check | `QA_ACCOUNT_SMOKE_EMAIL`, `QA_ACCOUNT_SMOKE_PASSWORD` |
