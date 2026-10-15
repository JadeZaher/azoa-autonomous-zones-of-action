# User API — Specification

## Goal
Provide a clean `UserController` that handles user registration, authentication, and CRUD using the unified `ProviderContext`.

## Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/user/register` | Register a new user |
| POST | `/api/user/login` | Login, returns JWT |
| GET | `/api/user/{id}` | Get user by id (with provider override support) |
| GET | `/api/user` | Get all users |
| PUT | `/api/user/{id}` | Update user |
| DELETE | `/api/user/{id}` | Delete user |

## Authentication
- JWT Bearer scheme
- `Authorize` attribute on GET / PUT / DELETE
- Register / Login are anonymous

## Provider Overrides
- Every mutating endpoint accepts an optional `AZOARequest` body or header to switch providers
- `ProviderContext` handles resolution; controller stays thin

## Models
- `User` implements `IUser`
- `UserLoginModel` — email + password
- `UserRegisterModel` — email + password + username + other required fields
- `UserUpdateModel` — subset of fields for updates

## Exclusions
- No Karma endpoints
- No `KarmaAkashicRecords` references
- No old `GetAndActivateDefaultStorageProvider` inline calls

## Acceptance Criteria
- [ ] All 6 endpoints return `AZOAResult<T>` or `AZOAResponse`
- [ ] JWT token returned on successful login
- [ ] Provider switching works via `AZOARequest` in body
- [ ] Controller uses `ProviderContext`, not manual activation
