import { execFile, type ExecFileException } from 'node:child_process'
import type { E2eEnvironment } from './environment'

/** Fixed `container_name` of the API service in docker-compose.e2e.yml. */
const apiContainer = 'azoa-e2e-api'

/** Mounted from `.generated/egress-probe/` (built by `scripts/e2e-stack.mjs` from `stack/egress-probe/`). */
const probeAssembly = '/e2e/probe/AzoaEgressProbe.dll'

/**
 * Real vendor origins the stack must never reach. `widget.identomat.com` is the host
 * `HostedSessionReconciliationController` hardcodes (defect D5).
 */
const realVendorOrigins = ['https://external-api.identomat.com', 'https://widget.identomat.com', 'https://api.stripe.com']

function executeContainer(
  runtime: string,
  args: string[],
  timeout: number,
): Promise<{ error: ExecFileException | null; stdout: string; stderr: string }> {
  return new Promise((resolve) => {
    execFile(runtime, args, { encoding: 'utf8', timeout, maxBuffer: 1024 * 1024, windowsHide: true }, (error, stdout, stderr) =>
      resolve({ error, stdout, stderr }),
    )
  })
}

async function containerRuntime(): Promise<string> {
  const configured = process.env.AZOA_E2E_CONTAINER_RUNTIME
  if (configured) return configured
  for (const candidate of ['docker', 'podman']) {
    const probe = await executeContainer(candidate, ['container', 'inspect', apiContainer], 5_000)
    if (!probe.error) return candidate
  }
  throw new Error(`Container ${apiContainer} was not found with docker or podman; start the stack with e2e-stack.mjs up.`)
}

/** Runs the .NET positive and negative trust controls; see `e2e/AGENTS.md` §hermetic. */
export async function proveEgressIsHermetic(environment: E2eEnvironment): Promise<void> {
  const upstreamPort = process.env.AZOA_E2E_IDENTOMAT_UPSTREAM_PORT ?? environment.identomatPort
  const fakeVendor = `https://${environment.vendorHost}:${upstreamPort}/__e2e/sessions`
  const result = await executeContainer(
    await containerRuntime(),
    ['exec', apiContainer, 'dotnet', probeAssembly, `+${fakeVendor}`, ...realVendorOrigins.map((origin) => `-${origin}`)],
    120_000,
  )
  if (result.error && (typeof result.error.code !== 'number' || result.error.killed || result.error.signal)) throw result.error
  const report = `${result.stdout ?? ''}${result.stderr ?? ''}`.trim()
  console.log(`[e2e] .NET egress probe in ${apiContainer}:\n${report}`)
  if (result.error)
    throw new Error(
      `the .NET egress probe reported ${result.error.code} violation(s); a real vendor was reachable, or the fake vendor ` +
        `was not (check the trust mount, AZOA_E2E_VENDOR_HOST and the host firewall for 9443/9444):\n${report}`,
    )
}
