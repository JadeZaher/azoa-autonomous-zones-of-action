'use client'

import { SdkError, SdkErrorCode, err, ok, type Result } from 'azoa-sdk'
import { OperatorRequestError, operatorRequest } from '@/lib/operator-client'

/**
 * One operator console call through the BFF, shaped like an SDK `Result` so boards written against
 * the SDK keep their `isOk` / `isRecentOperatorLoginRequired` handling. See `src/app/operator/AGENTS.md` §money-boards.
 */
export async function operatorResult<T>(path: string, init?: { method?: 'GET' | 'POST' | 'PUT'; body?: unknown }): Promise<Result<T, SdkError>> {
  try {
    return ok(await operatorRequest<T>(path, init))
  } catch (cause) {
    if (cause instanceof OperatorRequestError) {
      return err(new SdkError(SdkErrorCode.API_ERROR, cause.message, { status: cause.status, serverCode: cause.code, serverMessage: cause.message }))
    }
    return err(new SdkError(SdkErrorCode.NETWORK_ERROR, 'The node operator service could not be reached.'))
  }
}
