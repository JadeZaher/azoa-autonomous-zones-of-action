import { describe, expect, it } from 'vitest'
import { SdkError, SdkErrorCode } from 'azoa-sdk'
import { PAYMENTS_NOT_CONFIGURED_MESSAGE, refusalMessage } from '../refusal-messages'

const refusal = (status: number, serverMessage: string, serverCode?: string) =>
  new SdkError(SdkErrorCode.API_ERROR, `POST /api/x: ${serverMessage}`, { status, serverMessage, serverCode })

describe('refusalMessage turns live-QA refusals into honest copy', () => {
  it.each([
    ['payments unconfigured (M3)', refusal(503, 'Adding funds is not configured on this node.', 'PAYMENTS_NOT_CONFIGURED'), PAYMENTS_NOT_CONFIGURED_MESSAGE],
    ['a rail outage stays transient', refusal(503, 'Adding funds is not available on this node right now.', 'RAIL_UNAVAILABLE'), 'The provider is unavailable at the moment. Try again shortly.'],
    ['KYC policy prefix never leaks config keys (L2)', refusal(400, 'KYC_POLICY_UNAVAILABLE: Identomat is not configured. Missing configuration: Kyc__Identomat__CompanyKey, Kyc__Identomat__ConfigId'),
      'Identity verification is not available on this node yet. The node operator still has to finish setting it up.'],
    ['other refusals drop the config-key tail (L2)', refusal(409, 'The provider is not ready. Missing configuration: Kyb__ConfigId'), 'The provider is not ready.'],
    ['server text wins over the method/path prefix', refusal(422, 'The amount is below the minimum.'), 'The amount is below the minimum.'],
  ] as const)('%s', (_name, error, expected) => {
    expect(refusalMessage(error, 'fallback')).toBe(expected)
  })
})
