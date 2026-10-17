import { spawnSync } from 'node:child_process'
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

function containerRuntime(): string {
  const configured = process.env.AZOA_E2E_CONTAINER_RUNTIME
  if (configured) return configured
  for (const candidate of ['docker', 'podman']) {
    const probe = spawnSync(candidate, ['container', 'inspect', apiContainer], { stdio: 'ignore' })
    if (!probe.error && probe.status === 0) return candidate
  }
  throw new Error(`Container ${apiContainer} was not found with docker or podman; start the stack with e2e-stack.mjs up.`)
}

/**
 * Proves, with .NET's own `HttpClient` running inside the API container, that the per-run CA is the
 * only trust root: the fake vendor must be reached (positive control: CA mounted, host gateway
 * open) and every real vendor must fail. The probe inherits the container's `SSL_CERT_FILE` /
 * `SSL_CERT_DIR`, exactly as the API process does. See `e2e/AGENTS.md` §hermetic.
 */
export function proveEgressIsHermetic(environment: E2eEnvironment): void {
  const fakeVendor = `https://${environment.vendorHost}:${environment.identomatPort}/__e2e/sessions`
  const result = spawnSync(
    containerRuntime(),
    ['exec', apiContainer, 'dotnet', probeAssembly, `+${fakeVendor}`, ...realVendorOrigins.map((origin) => `-${origin}`)],
    { encoding: 'utf8', timeout: 120_000 },
  )
  if (result.error) throw result.error
  const report = `${result.stdout ?? ''}${result.stderr ?? ''}`.trim()
  console.log(`[e2e] .NET egress probe in ${apiContainer}:\n${report}`)
  if (result.status !== 0)
    throw new Error(
      `the .NET egress probe reported ${result.status} violation(s); a real vendor was reachable, or the fake vendor ` +
        `was not (check the trust mount, AZOA_E2E_VENDOR_HOST and the host firewall for 9443/9444):\n${report}`,
    )
}
