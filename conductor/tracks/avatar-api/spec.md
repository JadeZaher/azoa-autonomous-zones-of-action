# Avatar API — Specification

## Goal
Provide a clean `AvatarController` that handles avatar registration, authentication, and CRUD using the unified `ProviderContext`.

## Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/avatar/register` | Register a new avatar |
| POST | `/api/avatar/login` | Login, returns JWT |
| GET | `/api/avatar/{id}` | Get avatar by id (with provider override support) |
| GET | `/api/avatar` | Get all avatars |
| PUT | `/api/avatar/{id}` | Update avatar |
| DELETE | `/api/avatar/{id}` | Delete avatar |

## Authentication
- JWT Bearer scheme
- `Authorize` attribute on GET / PUT / DELETE
- Register / Login are anonymous

## Provider Overrides
- Every mutating endpoint accepts an optional `OASISRequest` body or header to switch providers
- `ProviderContext` handles resolution; controller stays thin

## Models
- `Avatar` implements `IAvatar`
- `AvatarLoginModel` — email + password
- `AvatarRegisterModel` — email + password + username + other required fields
- `AvatarUpdateModel` — subset of fields for updates

## Exclusions
- No Karma endpoints
- No `KarmaAkashicRecords` references
- No old `GetAndActivateDefaultStorageProvider` inline calls

## Acceptance Criteria
- [ ] All 6 endpoints return `OASISResult<T>` or `OASISResponse`
- [ ] JWT token returned on successful login
- [ ] Provider switching works via `OASISRequest` in body
- [ ] Controller uses `ProviderContext`, not manual activation
