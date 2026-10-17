import type { E2eEnvironment } from '../environment'
import { closeServer } from './http'
import { startIdentomat } from './identomat'
import { startStripe } from './stripe'

export type { IdentomatSessionRecord, IdentomatVerdict } from './identomat'
export type { StripeCheckoutSessionRecord } from './stripe'

/** Starts every fake vendor the stack is configured to call; returns the matching stop. */
export async function startFakeVendors(environment: E2eEnvironment): Promise<{ close(): Promise<void> }> {
  const servers = await Promise.all([startIdentomat(environment), startStripe(environment)])
  return { close: async () => void (await Promise.all(servers.map(closeServer))) }
}
