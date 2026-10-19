---
type: verification
---

# Verification — 2026-10-18

The feature branch includes the reviewed implementation `1b2b6e23`, release checkpoint `d89e3295`, release typing fix `f474863b` via merge `538c5ccd`, common lint-scope fix `d6a9a53c`, and verification fixes `2d1b4043`. No push, main merge or Railway configuration was performed by this lane.

## Collected final sweep before live provider diagnosis

The entire source-fix batch was independently accepted before execution. The SDK's borrowed dependency junction was removed as a link only and replaced by its own locked installation. Frontend and SDK lockfiles remain unchanged relative to the release checkpoint.

| Gate | Result | Ignored evidence under `.omc/research/` |
| --- | --- | --- |
| API Debug build | Exit 0, zero errors; existing nullability warnings remain | `tenant-ai-api-build-final.log` |
| Focused designer, HTTP policy, route-inventory and cross-tenant tests | 54 passed / 0 failed | `tenant-ai-backend-tests-final.log` |
| Merge registration guard | Exit 0 | `tenant-ai-merge-check-final.log` |
| SDK typecheck and build | Both exit 0 | `tenant-ai-sdk-typecheck-final.log`, `tenant-ai-sdk-build-final.log` |
| SDK API/privacy tests | 34 passed / 0 failed | `tenant-ai-sdk-tests-final.log` |
| Frontend typecheck | Exit 0 | `tenant-ai-frontend-typecheck-final.log` |
| Frontend lint | Exit 0; 0 errors, 117 existing warnings | `tenant-ai-frontend-lint-final.log` |
| Action-builder UI tests | 64 passed / 0 failed | `tenant-ai-frontend-tests-final.log` |
| Frontend production build | Exit 0 | `tenant-ai-frontend-build-final.log` |
| Frontend production dependency audit | Exit 0, all severity counts zero | `tenant-ai-frontend-audit-production-final.log` |
| SDK production dependency audit | Exit 0, all severity counts zero | `tenant-ai-sdk-audit-production-final.log` |
| Frontend all/development dependency audit | Exit 1, seven high findings from the existing development-tool chain | `tenant-ai-frontend-audit-final.log` |

This is a focused feature verification batch, not the entire repository integration suite or a complete human usability study. The release coordinator owns those independent release gates.

## Development dependency finding

The seven affected packages are propagated manifestations of the braces advisory [GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm), rather than seven independent advisories. Pinned paths include:

- `tailwindcss@3.4.19` → `chokidar@3.6.0` → `braces@3.0.3` (`~3.0.2`).
- `tailwindcss@3.4.19` → `fast-glob@3.3.3` → `micromatch@4.0.8` → `braces@3.0.3` (`^3.0.3`).
- `eslint-config-next@16.4.2` → `@next/eslint-plugin-next@16.4.2` → its nested `fast-glob@3.3.1` → `micromatch@4.0.8` → `braces@3.0.3`.

The audit affects braces `<=3.0.3`; the registry's current latest version was still `3.0.3`. There is no newer compatible braces override to apply. npm's suggested Tailwind `4.3.3` fix is a major migration, so no forced audit fix or incompatible override was made. Both production-only audits are clean. The all/development audit remains explicitly non-green and needs a separately reviewed tooling migration or upstream patch.

## Real bounded provider evidence

The ignored smoke reads the authorized literal Pi credential inside its process, submits only a synthetic cooperative brief and reports counts/status only. It has no persistence/execution dependencies. No credential, raw provider output or prompt/response body was printed, stored in research, passed on argv or sent to a browser.

1. Initial request: exactly one OpenRouter request using `anthropic/claude-sonnet-4.6`, HTTP 200, designer returned `AI_INVALID_REPLY`, no draft. The service did not short-circuit the unknown jurisdiction.
2. Separately authorized diagnostic request: exactly one request, HTTP 200, finish reason `stop`, reasoning length 0, content length 5,670, Markdown fence detected. Only these shape diagnostics were retained; the content remained in process memory.

The JSON-object response preference was not a native strict structured-output requirement. The follow-up source batch requests a closed native schema with `strict=true` and `provider.require_parameters=true`, preserving privacy/no-fallback settings and every independent typed/provenance/DAG/readiness check. Fences remain refusals; no permissive extraction or unstructured retry was added. Official OpenRouter structured-output documentation, actual Sonnet 4.6 endpoint metadata and Anthropic supported-schema documentation were captured and filtered under ignored research before implementing this change.

The native-schema batch received independent static acceptance. One compiled focused batch passed **59/59** tests, including the added fence/unsupported-provider cases (`tenant-ai-structured-backend-tests.log`). No frontend code changed after its green collected sweep.

3. Final authorized verification request: exactly one HTTP 200 OpenRouter request; finish reason `stop`, reasoning length 0, content length 3,501, typed reply accepted, zero unexpected fields, ten follow-up questions and eight concerns after server checks. `readyForDraft=false` and `draft=null`; no persistence or execution dependencies. The smoke exited 0 (`tenant-ai-live-provider-structured-final.log`). Total real synthetic provider requests across all three attempts: **3**. No further requests were made.

The real model/service path is now verified. This does not establish full authenticated UI behavior or legal/economic correctness of a generated draft. The AI feature remains disabled by default; deployment enablement and a complete guarded tenant UI drill remain coordinator/owner work.
