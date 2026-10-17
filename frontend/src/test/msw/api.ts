/**
 * The API origin slices run against and the AZOAResult envelope the .NET controllers return.
 * See `src/test/AGENTS.md` §msw.
 */

/** Never resolvable: a request MSW does not intercept fails loudly instead of reaching a real host. */
export const SLICE_API_ORIGIN = 'http://api.slice.test'

/** Absolute URL for an API path, as the SDK builds it. */
export function apiUrl(path: string): string {
  return `${SLICE_API_ORIGIN}${path}`
}

/** Mirrors `Models/Responses/AZOAResult.cs` serialized by ASP.NET (camelCase, nulls kept). */
export interface AzoaEnvelope<T> {
  isError: boolean
  message: string
  result: T | null
  code: string | null
  retryAfterSeconds: number | null
  detail: null
}

export function azoaOk<T>(result: T, message = 'Success'): AzoaEnvelope<T> {
  return { isError: false, message, result, code: null, retryAfterSeconds: null, detail: null }
}

export function azoaError(message: string, code: string | null = null): AzoaEnvelope<null> {
  return { isError: true, message, result: null, code, retryAfterSeconds: null, detail: null }
}
