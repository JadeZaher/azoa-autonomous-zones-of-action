/** Runtime public and internal API origins; see AGENTS.md §Runtime API origins. */

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

function normalizeApiUrl(value: string, allowInsecureLocal: boolean, setting = 'API_URL'): string {
  let url: URL
  try {
    url = new URL(value)
  } catch {
    throw new Error(`${setting} must be an absolute HTTP(S) URL.`)
  }

  if (
    !['http:', 'https:'].includes(url.protocol) ||
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    url.pathname !== '/'
  ) {
    throw new Error(`${setting} must be a credential-free HTTP(S) origin.`)
  }

  if (process.env.NODE_ENV === 'production') {
    const isPrivateHost = isPrivateBrowserHost(url.hostname)
    const isExplicitLocalDevelopment =
      allowInsecureLocal && isPrivateHost && url.protocol === 'http:'
    if (!isExplicitLocalDevelopment && (url.protocol !== 'https:' || isPrivateHost)) {
      throw new Error(`Production ${setting} must be a public HTTPS origin.`)
    }
  }

  return url.origin
}

/** Resolve the public API origin injected into browser runtime configuration. */
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

/** Resolve the server-to-server API origin, falling back to the public origin. */
export function resolveInternalApiUrl(): string {
  const configured = process.env.INTERNAL_API_URL
  if (!configured) return resolveServerApiUrl()
  return normalizeApiUrl(configured, process.env.AZOA_ALLOW_INSECURE_LOCAL_API === 'true', 'INTERNAL_API_URL')
}

/** Read the public API origin from browser runtime configuration or SSR environment. */
export function readInitialApiUrl(): string {
  if (typeof window === 'undefined') return resolveServerApiUrl()
  const configured = window.__RUNTIME_CONFIG__?.apiUrl
  if (configured) return configured
  if (process.env.NODE_ENV === 'production') {
    throw new Error('The server did not provide a production API URL.')
  }
  return DEFAULT_API_URL
}
