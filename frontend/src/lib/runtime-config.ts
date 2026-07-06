/**
 * Runtime (not build-time) API base URL resolution.
 *
 * `NEXT_PUBLIC_*` vars are inlined into the client bundle at `next build`
 * time, so baking `NEXT_PUBLIC_API_URL` into the Docker image (see
 * frontend/Dockerfile) makes the value dead at runtime — an operator can't
 * repoint the UI at a different API host without rebuilding the image.
 *
 * Fix: the value is read server-side from a plain (non-`NEXT_PUBLIC_`) env
 * var at REQUEST time (Server Components re-read `process.env` on every
 * render — Next.js does not inline non-public vars into the bundle), then
 * injected into the page as `window.__RUNTIME_CONFIG__` via a small inline
 * script rendered by the root layout. The client singleton (azoa.ts) reads
 * that global instead of a build-time constant.
 *
 * This module is intentionally free of React/SDK imports so both the SDK
 * singleton (azoa.ts) and the server layout can use it without an import
 * cycle — same split as networks.ts / debug.ts.
 */

declare global {
  interface Window {
    __RUNTIME_CONFIG__?: { apiUrl?: string }
  }
}

const DEFAULT_API_URL = 'http://localhost:5000'

/** Server-side only: resolve the API URL from the live process environment. */
export function resolveServerApiUrl(): string {
  return process.env.API_URL || process.env.NEXT_PUBLIC_API_URL || DEFAULT_API_URL
}

/**
 * SSR-safe read of the API base URL.
 * - In the browser: reads the value the server injected at request time.
 * - During SSR/build (no `window`): falls back to the live process env so
 *   server-rendered output is still correct.
 */
export function readInitialApiUrl(): string {
  if (typeof window === 'undefined') return resolveServerApiUrl()
  return window.__RUNTIME_CONFIG__?.apiUrl || DEFAULT_API_URL
}
