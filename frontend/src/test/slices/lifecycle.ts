import { cleanup } from '@testing-library/react'
import { afterAll, afterEach, beforeAll, expect } from 'vitest'
import { azoa } from '@/lib/azoa'
import type { SliceNetwork } from '../msw/network'

let network: SliceNetwork | null = null

/** The network the current project installed; `renderSlice` registers handlers on it. */
export function currentNetwork(): SliceNetwork {
  if (!network) throw new Error('No slice network installed: load a src/test/slices/setup-*.ts file first.')
  return network
}

/**
 * One lifecycle for both slice projects. An API call no handler answered fails the test that
 * made it, which is how SDK↔API route drift surfaces. See `src/test/AGENTS.md` §slices.
 */
export function installSliceNetwork(created: SliceNetwork): void {
  network = created

  beforeAll(async () => {
    await created.start()
  })

  afterEach(async () => {
    cleanup()
    const unhandled = [...created.unhandled]
    created.reset()
    await azoa.session.logout()
    localStorage.clear()
    expect(unhandled, 'the SDK called API routes no handler models (route drift or a missing handler)').toEqual([])
  })

  afterAll(() => {
    created.stop()
  })
}
