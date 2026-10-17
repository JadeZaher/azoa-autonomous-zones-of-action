import { proveEgressIsHermetic } from './support/egress-probe'
import { loadE2eEnvironment } from './support/environment'
import { startFakeVendors } from './support/fake-vendors'

/**
 * Starts the fake vendors for the whole run (they must outlive every worker), fails fast when the
 * stack is not up, and proves the API cannot reach a real vendor. The returned function is the
 * global teardown. See `e2e/AGENTS.md` §run.
 */
export default async function globalSetup(): Promise<(() => Promise<void>) | undefined> {
  if (process.env.AZOA_E2E_SHELL_ONLY === '1') return undefined

  const environment = loadE2eEnvironment()
  const vendors = await startFakeVendors(environment)
  try {
    const health = await fetch(`${environment.apiUrl}/health`)
    if (!health.ok) throw new Error(`API health at ${environment.apiUrl} answered ${health.status}`)
    proveEgressIsHermetic(environment)
  } catch (error) {
    await vendors.close()
    throw new Error(
      `E2E preconditions failed: ${error instanceof Error ? error.message : error}. ` +
        'Start the stack with `node e2e/scripts/e2e-stack.mjs up` (or set AZOA_E2E_SHELL_ONLY=1 for shell checks).',
    )
  }
  return () => vendors.close()
}
