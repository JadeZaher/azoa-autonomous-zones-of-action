import { setupWorker } from 'msw/browser'
import { isApiRequest, trackApiCalls, type SliceNetwork } from './network'

/**
 * MSW for the `slices-browser` project: a real Service Worker in Chromium intercepts the SDK's
 * `fetch`. The worker script is served from `src/test/msw/public/` (Vite `publicDir`).
 */
export function createBrowserNetwork(): SliceNetwork {
  const worker = setupWorker()
  return trackApiCalls(
    worker,
    async (onUnhandled) => {
      await worker.start({
        serviceWorker: { url: '/mockServiceWorker.js' },
        quiet: true,
        onUnhandledRequest(request, print) {
          onUnhandled(request)
          // Vite serves every test module through this same page; only API calls are judged.
          if (isApiRequest(request)) print.error()
        },
      })
    },
    () => worker.stop(),
  )
}
