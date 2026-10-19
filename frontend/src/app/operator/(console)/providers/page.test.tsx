import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { operatorRequest } from '@/lib/operator-client'
import type { KycProviderProfileResponse } from '@/lib/operator-contracts'
import OperatorProvidersPage from './page'

vi.mock('@/lib/operator-client', () => ({
  operatorRequest: vi.fn(),
  OperatorRequestError: class extends Error {},
}))
vi.mock('@/components/operator/hosted-session-recovery', () => ({ HostedSessionRecovery: () => null }))

const profile: KycProviderProfileResponse = {
  providerKey: 'identomat', displayName: 'Identomat', adapterKey: 'identomat',
  enabled: false, available: true, apiKeyConfigured: false, webhookSecretConfigured: false,
  readinessCode: 'DISABLED', requiredConfigurationKeys: [], missingConfigurationKeys: [],
  policyVersion: 'identomat-hosted-v1', assuranceLevel: 'document-and-liveness',
  version: 0, trustRevision: 0, updatedAt: '1970-01-01T00:00:00+00:00',
}

describe('provider policy freshness', () => {
  beforeEach(() => vi.mocked(operatorRequest).mockResolvedValue([profile]))

  it('explains an unconfigured built-in profile without presenting its sentinel as a real update', async () => {
    render(<OperatorProvidersPage />)

    expect(await screen.findByText('Policy has not been configured yet')).toBeInTheDocument()
    expect(screen.queryByText(/^Updated /)).not.toBeInTheDocument()
  })

  it('keeps a persisted disabled policy timestamp visible', async () => {
    vi.mocked(operatorRequest).mockResolvedValue([{ ...profile, version: 2, updatedAt: '2026-10-18T12:00:00Z' }])
    render(<OperatorProvidersPage />)

    expect(await screen.findByText(/^Updated /)).toHaveTextContent('2026')
    expect(screen.queryByText('Policy has not been configured yet')).not.toBeInTheDocument()
  })
})
