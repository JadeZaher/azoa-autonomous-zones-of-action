import { SdkError, SdkErrorCode } from "../core/errors.js";
import type { StartWorkflowOptions } from "./types.js";

/** Validate and encode the persisted workflow admission intent. */
export function workflowStartQuery(options: StartWorkflowOptions): string {
  if (!options?.idempotencyKey?.trim() || options.idempotencyKey.length > 200) {
    throw new SdkError(SdkErrorCode.INVALID_INPUT, "A persisted idempotencyKey of 1–200 characters is required");
  }
  if (options.acknowledgeEconomicEffects && !options.expectedPublishedHash?.trim()) {
    throw new SdkError(SdkErrorCode.INVALID_INPUT, "Economic consent requires the preview's published version hash");
  }
  const query = new URLSearchParams();
  query.set("acknowledgeEconomicEffects", String(options.acknowledgeEconomicEffects ?? false));
  if (options.expectedPublishedHash) query.set("expectedPublishedHash", options.expectedPublishedHash);
  return `?${query.toString()}`;
}
