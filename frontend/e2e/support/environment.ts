import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'

/** Everything a journey needs to reach the stack and impersonate its vendors. See `e2e/AGENTS.md`. */
export interface E2eEnvironment {
  apiUrl: string
  appUrl: string
  /** Hostname the API container dials to reach the host-run fake vendors (in the test CA's SAN list). */
  vendorHost: string
  identomatPort: number
  stripePort: number
  identomatCompanyKey: string
  identomatCallbackKey: string
  identomatKycConfig: string
  identomatKybConfig: string
  stripeSecretKey: string
  stripeWebhookSecret: string
  operatorUsername: string
  operatorPassword: string
  generatedDirectory: string
}

export const e2eRoot = path.resolve(__dirname, '..')
const generatedDirectory = path.join(e2eRoot, '.generated')

/** Fixed in docker-compose.e2e.yml; the password is the generated per-run secret. */
const operatorUsername = 'node-operator'

/**
 * Values the stack script generated for this run. `process.env` wins over the file, exactly as in
 * `scripts/e2e-stack.mjs`, so CI (or a developer) can inject its own and both sides agree.
 */
function generatedValues(): Record<string, string> {
  const file = path.join(generatedDirectory, 'e2e.env')
  if (!existsSync(file))
    throw new Error('frontend/e2e/.generated/e2e.env is missing: run `node e2e/scripts/e2e-stack.mjs up` first.')
  return Object.fromEntries(
    readFileSync(file, 'utf8')
      .split(/\r?\n/)
      .filter((line) => line.includes('='))
      .map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]),
  )
}

export function loadE2eEnvironment(): E2eEnvironment {
  const generated = generatedValues()
  const value = (name: string) => {
    const resolved = process.env[name] ?? generated[name]
    if (!resolved) throw new Error(`${name} is not set; run \`node e2e/scripts/e2e-stack.mjs prepare\`.`)
    return resolved
  }
  return {
    // Dedicated E2E ports; AZOA_FRONTEND_HOST_PORT belongs to the dev stack and is never read here.
    apiUrl: process.env.AZOA_E2E_API_URL ?? `http://localhost:${process.env.AZOA_E2E_API_PORT ?? '5200'}`,
    appUrl: process.env.AZOA_E2E_APP_URL ?? `http://localhost:${process.env.AZOA_E2E_FRONTEND_PORT ?? '3200'}`,
    vendorHost: process.env.AZOA_E2E_VENDOR_HOST ?? 'host.docker.internal',
    identomatPort: Number(process.env.AZOA_E2E_IDENTOMAT_PORT ?? 9443),
    stripePort: Number(process.env.AZOA_E2E_STRIPE_PORT ?? 9444),
    identomatCompanyKey: value('AZOA_E2E_IDENTOMAT_COMPANY_KEY'),
    identomatCallbackKey: value('AZOA_E2E_IDENTOMAT_CALLBACK_KEY'),
    identomatKycConfig: value('AZOA_E2E_IDENTOMAT_KYC_CONFIG'),
    identomatKybConfig: value('AZOA_E2E_IDENTOMAT_KYB_CONFIG'),
    stripeSecretKey: value('AZOA_E2E_STRIPE_SECRET_KEY'),
    stripeWebhookSecret: value('AZOA_E2E_STRIPE_WEBHOOK_SECRET'),
    operatorUsername,
    operatorPassword: value('AZOA_E2E_OPERATOR_PASSWORD'),
    generatedDirectory,
  }
}
