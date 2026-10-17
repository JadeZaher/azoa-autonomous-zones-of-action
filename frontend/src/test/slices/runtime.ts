import { SLICE_API_ORIGIN } from '../msw/api'

/**
 * Runs before any slice module imports `@/lib/azoa`: the SDK singleton reads the API origin
 * from the server-injected runtime config at import time (`src/lib/runtime-config.ts`).
 */
window.__RUNTIME_CONFIG__ = { apiUrl: SLICE_API_ORIGIN }
