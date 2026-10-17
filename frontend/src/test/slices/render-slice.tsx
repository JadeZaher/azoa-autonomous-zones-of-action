import type { ReactElement } from 'react'
import { render, type RenderResult } from '@testing-library/react'
import userEvent, { type UserEvent } from '@testing-library/user-event'
import type { HttpHandler } from 'msw'
import { azoa } from '@/lib/azoa'
import type { SliceNetwork } from '../msw/network'
import { currentNetwork } from './lifecycle'

/** Same id the backend test principal uses (`TestAuthHandler.DefaultUserId`). */
export const SLICE_TENANT_ID = 'a1111111-1111-1111-1111-111111111111'

export interface SliceSession {
  userId: string
  /** Bearer token the SDK will send; defaults to an unsigned JWT-shaped token for `userId`. */
  token: string
}

export interface SliceOptions {
  /** Fake APIs (handler factories from `src/test/msw/handlers/`) the surface may call. */
  apis: ReadonlyArray<{ handlers: HttpHandler[] }>
  /** `'anonymous'` mounts signed out; otherwise the SDK is signed in as this tenant owner. */
  session?: Partial<SliceSession> | 'anonymous'
}

export interface SliceHarness extends RenderResult {
  user: UserEvent
  network: SliceNetwork
  session: SliceSession | null
}

/**
 * Mounts one feature surface with the REAL SDK client (`@/lib/azoa`); only the network is
 * fake. See `src/test/AGENTS.md` §slices for the pattern and the migration path.
 */
export async function renderSlice(ui: ReactElement, options: SliceOptions): Promise<SliceHarness> {
  const network = currentNetwork()
  for (const api of options.apis) network.use(...api.handlers)

  let session: SliceSession | null = null
  if (options.session !== 'anonymous') {
    const userId = options.session?.userId ?? SLICE_TENANT_ID
    session = { userId, token: options.session?.token ?? unsignedToken(userId) }
    localStorage.setItem('azoa_token', session.token)
    localStorage.setItem('azoa_user_id', session.userId)
    await azoa.session.restore()
  }

  const user = userEvent.setup()
  return { ...render(ui), user, network, session }
}

/** JWT-shaped (header.payload.signature) so the SDK can read `sub`; never verified by a fake API. */
function unsignedToken(userId: string): string {
  const encode = (value: object) =>
    btoa(JSON.stringify(value)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_')
  return `${encode({ alg: 'none', typ: 'JWT' })}.${encode({ sub: userId, token_use: 'login' })}.slice`
}
