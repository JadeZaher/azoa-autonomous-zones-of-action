/** Wire envelope used by every `api/node-admin/*` (and legacy operator) response. */
export interface AzoaResult<T> {
  result: T | null;
  isError: boolean;
  message: string;
  code?: string;
  retryAfterSeconds?: number;
}

export const STEP_UP_HEADER = "x-azoa-auth-requirement";
export const STEP_UP_HEADER_VALUE = "recent-node-admin-login";
