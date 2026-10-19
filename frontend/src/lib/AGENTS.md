# Frontend shared library

## Runtime API origins

`API_URL` is the public API origin used by browsers. The dynamic root layout reads it at request time through `resolveServerApiUrl()` and injects only that origin into `window.__RUNTIME_CONFIG__`. `readInitialApiUrl()` keeps the same public origin during SSR and in the browser; an internal container hostname must never enter that injected configuration.

`INTERNAL_API_URL` is an optional server-to-server origin for frontend health probes and the operator BFF. `resolveInternalApiUrl()` falls back to the public origin when absent. In separate compose containers, `API_URL=http://localhost:5200` reaches the host-published API from the user's browser while `INTERNAL_API_URL=http://azoa-api:5000` reaches the API service from the frontend container. Container loopback points to the frontend itself, so a single localhost origin cannot serve both callers. Railway may continue using the public HTTPS origin when no internal origin is configured.

Both origins reject credentials, non-HTTP(S) schemes, paths, query strings and fragments. Production requires public HTTPS. The existing `AZOA_ALLOW_INSECURE_LOCAL_API=true` opt-in permits only private/local HTTP for local container stacks; adding an internal origin does not bypass this requirement. Internal-origin values are never included in browser configuration.

The module stays free of React/SDK imports to avoid a cycle when both the root layout and browser SDK singleton use it. Plain server environment values are read per request; public `NEXT_PUBLIC_*` values remain build-time constants. The installed Next.js environment-variable guide documents this distinction in `node_modules/next/dist/docs/01-app/02-guides/environment-variables.md`.
