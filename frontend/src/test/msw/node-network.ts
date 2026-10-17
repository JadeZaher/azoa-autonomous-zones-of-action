import { setupServer } from 'msw/node'
import { isApiRequest, trackApiCalls, type SliceNetwork } from './network'

/** MSW for the jsdom `slices` project: intercepts the SDK's `fetch` inside Node. */
export function createNodeNetwork(): SliceNetwork {
  const server = setupServer()
  return trackApiCalls(
    server,
    async (onUnhandled) => {
      server.listen({
        onUnhandledRequest(request, print) {
          onUnhandled(request)
          if (isApiRequest(request)) print.error()
        },
      })
      installJsdomSignalBridge()
    },
    () => server.close(),
  )
}

/**
 * jsdom replaces the global `AbortController`, so the SDK's per-request timeout signal is a jsdom
 * object that Node's fetch (undici, inside MSW's interceptor) rejects. Even `new Request().signal`
 * is jsdom-built here, so a foreign signal cannot be told apart and is dropped for every call:
 * request timeouts are exercised by the Chromium project instead. Wrapping OUTSIDE the intercepted
 * `fetch` keeps MSW in the path; `server.close()` restores the original. See `src/test/AGENTS.md` §msw.
 */
function installJsdomSignalBridge(): void {
  const interceptedFetch = globalThis.fetch
  globalThis.fetch = (input, init) => interceptedFetch(input, init?.signal ? { ...init, signal: undefined } : init)
}
