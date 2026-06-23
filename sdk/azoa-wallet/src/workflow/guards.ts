/**
 * Synchronous input guards for the workflow run driver.
 *
 * These mirror the private `assertUuid` in `api/client.ts:569-576` exactly —
 * same UUID regex, same `SdkError(INVALID_INPUT)` throw, same path-traversal
 * intent — but live here because the API client's copy is module-private. The
 * guards throw SYNCHRONOUSLY on bad input (the one sanctioned throw in the SDK's
 * otherwise Result-only contract), so a malformed id never reaches `fetch`.
 */

import { SdkError, SdkErrorCode } from "../core/errors.js";

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Throws `SdkError(INVALID_INPUT)` unless `id` is a UUID. Prevents path traversal. */
export function assertUuid(id: string, paramName: string): void {
  if (typeof id !== "string" || !UUID_RE.test(id)) {
    throw new SdkError(
      SdkErrorCode.INVALID_INPUT,
      `Invalid ${paramName}: expected UUID format (received "${
        typeof id === "string" && id.length > 50 ? id.slice(0, 50) + "…" : String(id)
      }")`
    );
  }
}

/**
 * Throws `SdkError(INVALID_INPUT)` unless `value` is a non-empty string.
 *
 * Used for the `gateId` signal argument: the backend (`QuestSignalRequest.GateId`,
 * `Models/Requests/QuestRequests.cs`) types it as a plain string, NOT a UUID — a
 * contract-confirmed relaxation of plan D6's `assertUuid(gateId)`. We still guard
 * it so an empty/whitespace gate id can never be interpolated into the URL body.
 */
export function assertNonEmptyString(value: string, paramName: string): void {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new SdkError(
      SdkErrorCode.INVALID_INPUT,
      `Invalid ${paramName}: expected a non-empty string`
    );
  }
}
