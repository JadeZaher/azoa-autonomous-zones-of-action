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

function isPrivateBrowserHost(hostname: string): boolean {
  const normalized = hostname.toLowerCase().replace(/^\[|\]$/g, '')
  if (
    normalized === 'localhost' ||
    normalized === '::1' ||
    normalized.endsWith('.localhost') ||
    normalized.endsWith('.internal') ||
    !normalized.includes('.')
  ) {
    return true
  }

  const octets = normalized.split('.').map(Number)
  if (
    octets.length !== 4 ||
    octets.some((part) => !Number.isInteger(part) || part < 0 || part > 255)
  ) {
    return false
  }

  return (
    octets[0] === 10 ||
    octets[0] === 127 ||
    (octets[0] === 169 && octets[1] === 254) ||
    (octets[0] === 172 && octets[1] >= 16 && octets[1] <= 31) ||
    (octets[0] === 192 && octets[1] === 168)
  )
}

function normalizeApiUrl(value: string, allowInsecureLocal: boolean): string {
  let url: URL
  try {
    url = new URL(value)
  } catch {
    throw new Error('API_URL must be an absolute HTTP(S) URL.')
  }

  if (
    !['http:', 'https:'].includes(url.protocol) ||
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    url.pathname !== '/'
  ) {
    throw new Error('API_URL must be a credential-free HTTP(S) origin.')
  }

  if (process.env.NODE_ENV === 'production') {
    const isPrivateHost = isPrivateBrowserHost(url.hostname)
    const isExplicitLocalDevelopment =
      allowInsecureLocal && isPrivateHost && url.protocol === 'http:'
    if (!isExplicitLocalDevelopment && (url.protocol !== 'https:' || isPrivateHost)) {
      throw new Error('Production API_URL must be a public HTTPS origin.')
    }
  }

  return url.origin
}

/** Server-side only: resolve the API URL from the live process environment. */
export function resolveServerApiUrl(): string {
  const isProduction = process.env.NODE_ENV === 'production'
  const configured =
    process.env.API_URL || (!isProduction ? process.env.NEXT_PUBLIC_API_URL : undefined)
  if (!configured) {
    if (isProduction) throw new Error('API_URL is required in Production.')
    return DEFAULT_API_URL
  }

  return normalizeApiUrl(configured, process.env.AZOA_ALLOW_INSECURE_LOCAL_API === 'true')
}

/**
 * SSR-safe read of the API base URL.
 * - In the browser: reads the value the server injected at request time.
 * - During SSR/build (no `window`): falls back to the live process env so
 *   server-rendered output is still correct.
 */
export function readInitialApiUrl(): string {
  if (typeof window === 'undefined') return resolveServerApiUrl()
  const configured = window.__RUNTIME_CONFIG__?.apiUrl
  if (configured) return configured
  if (process.env.NODE_ENV === 'production') {
    throw new Error('The server did not provide a production API URL.')
  }
  return DEFAULT_API_URL
}
