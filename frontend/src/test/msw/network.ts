import type { RequestHandler } from 'msw'
import { SLICE_API_ORIGIN } from './api'

/** One API call the SDK made during a slice test. */
export interface ApiCall {
  method: string
  path: string
  authorization: string | null
}

/**
 * The MSW surface slices need, identical in Node (`setupServer`) and Chromium (`setupWorker`).
 * See `src/test/AGENTS.md` §msw.
 */
export interface SliceNetwork {
  start(): Promise<void>
  use(...handlers: RequestHandler[]): void
  reset(): void
  stop(): void
  /** API calls since the last reset. */
  readonly calls: readonly ApiCall[]
  /** API calls no handler answered since the last reset: SDK↔API drift, reported per test. */
  readonly unhandled: readonly ApiCall[]
}

/** The lifecycle-event and handler surface `setupServer` and `setupWorker` share. */
export interface MswController {
  use(...handlers: RequestHandler[]): void
  resetHandlers(...handlers: RequestHandler[]): void
  events: {
    on(event: 'request:start', listener: (args: { request: Request }) => void): void
  }
}

/** Builds the shared bookkeeping around a concrete MSW controller. */
export function trackApiCalls(controller: MswController, start: (onUnhandled: (request: Request) => void) => Promise<void>, stop: () => void): SliceNetwork {
  let calls: ApiCall[] = []
  let unhandled: ApiCall[] = []
  const describe = (request: Request): ApiCall => {
    const url = new URL(request.url)
    return { method: request.method, path: `${url.pathname}${url.search}`, authorization: request.headers.get('Authorization') }
  }

  return {
    async start() {
      controller.events.on('request:start', ({ request }) => {
        if (isApiRequest(request)) calls.push(describe(request))
      })
      await start((request) => {
        if (isApiRequest(request)) unhandled.push(describe(request))
      })
    },
    use: (...handlers) => controller.use(...handlers),
    reset() {
      controller.resetHandlers()
      calls = []
      unhandled = []
    },
    stop,
    get calls() {
      return calls
    },
    get unhandled() {
      return unhandled
    },
  }
}

/** Only calls to the slice API origin are the SDK's business; Vite modules and assets pass through. */
export function isApiRequest(request: Request): boolean {
  return new URL(request.url).origin === SLICE_API_ORIGIN
}
