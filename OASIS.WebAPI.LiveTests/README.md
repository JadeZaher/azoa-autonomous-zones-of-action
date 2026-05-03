# 🔬 OASIS.WebAPI.LiveTests

A standalone console harness for hitting the **live** OASIS WebAPI with real HTTP requests, driven by JSONL payload files.

## How it works

1. **Discover** — Scans `live-tests/` for `.jsonl` files (one per controller).
2. **Parse** — Each line in a JSONL file is a single test case (method, path, headers, body, assertions).
3. **Execute** — Suites run in parallel (configurable), cases within a suite run sequentially for chaining.
4. **Report** — Results compiled into `live-test-results.md` in the repo root (LLM-friendly markdown).

## Quick start

```bash
# Run against localhost (default)
dotnet run --project OASIS.WebAPI.LiveTests

# Run against a deployed environment
dotnet run --project OASIS.WebAPI.LiveTests -- --url https://api.oasis.example.com

# Customize parallelism and output
dotnet run --project OASIS.WebAPI.LiveTests -- -u https://api.oasis.example.com -p 8 -o results.md
```

## Configuration

Edit `appsettings.LiveTests.json` or override with CLI flags / environment variables:

| Setting | CLI Flag | Env Var | Default |
|---------|----------|---------|---------|
| BaseUrl | `-u`, `--url` | `OASIS_TEST_BaseUrl` | `https://localhost:5001` |
| MaxParallelSuites | `-p`, `--parallel` | `OASIS_TEST_MaxParallelSuites` | `4` |
| PayloadDirectory | `-d`, `--dir` | `OASIS_TEST_TestDiscovery__PayloadDirectory` | `live-tests` |
| ResultsPath | `-o`, `--output` | `OASIS_TEST_Output__ResultsPath` | `../live-test-results.md` |

## JSONL Test Case Format

```json
{
  "id": "register_avatar",
  "description": "Register a new avatar",
  "method": "POST",
  "path": "/api/avatar/register",
  "headers": { "Content-Type": "application/json" },
  "body": { "username": "tester", "email": "test@example.com", "password": "Test123!" },
  "expectedStatus": 200,
  "extract": { "avatarId": "result.id" },
  "saveAs": "avatar1"
}
```

### Template substitution

Use `{{namespace.variable}}` anywhere (path, headers, body). Values are extracted from prior responses via `extract` + `saveAs`.

```json
{
  "path": "/api/avatar/{{avatar1.avatarId}}",
  "headers": { "Authorization": "Bearer {{auth1.token}}" }
}
```

### Extraction paths

Simple dot-notation supported: `result.id`, `result.0.name`, `data.items.2.token`.

### Status assertions

- `"expectedStatus": 200` — exact match
- `"expectedStatusRange": "2xx"` — category match (`2xx`, `3xx`, `4xx`, `5xx`)
- Omit both — no status assertion (pass regardless)

## Suite files

| File | Controller | Cases |
|------|------------|-------|
| `AvatarController.jsonl` | Avatar register/login/CRUD/wallets | 11 |
| `HolonController.jsonl` | Holon CRUD/interact lifecycle | 9 |
| `BlockchainOperationController.jsonl` | Operation query by avatar | 6 |
| `STARODKController.jsonl` | ODK create/get/generate/deploy/delete | 8 |

Add new `.jsonl` files to `live-tests/` — they are picked up automatically.
