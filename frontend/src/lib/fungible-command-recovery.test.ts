import { expect, it } from 'vitest'
import { isFungibleCommandSuperseded } from './fungible-command-recovery'

it('requires the exact operation and a strictly greater safe monotonic version', () => {
  const command = { operationId: 'original', version: 5 }
  expect(isFungibleCommandSuperseded(command, { operationId: 'original', version: 6 })).toBe(true)
  for (const observed of [
    { operationId: 'other', version: 6 },
    { operationId: 'original', version: 5 },
    { operationId: 'original', version: 4 },
    { operationId: 'original', version: Number.MAX_SAFE_INTEGER + 1 },
  ]) expect(isFungibleCommandSuperseded(command, observed)).toBe(false)
})
