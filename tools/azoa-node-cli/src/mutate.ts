import { runLogin } from "./commands/login.js";
import { httpClientFor, resolveProfile, type ResolvedProfile } from "./context.js";
import { StepUpRequiredError } from "./http/client.js";
import { createRealInputSource, type InputSource } from "./io/input.js";

/**
 * Single seam every mutating command goes through for a `*Recent` (step-up)
 * route: try with `allowStepUpError`, and on `STEP_UP_REQUIRED` transparently
 * re-run the stored profile's login flow once, then retry the original call
 * exactly once (a second step-up failure propagates as a normal error — no
 * retry loop). Implemented once here rather than per command, closing the
 * gap noted after the first contract-alignment pass (see src/AGENTS.md).
 */
export async function mutateWithStepUpRetry<T>(
  resolved: ResolvedProfile,
  method: string,
  path: string,
  body?: unknown,
  input: InputSource = createRealInputSource(),
): Promise<T> {
  try {
    const { data } = await httpClientFor(resolved).call<T>(method, path, body, { allowStepUpError: true });
    return data;
  } catch (err) {
    if (!(err instanceof StepUpRequiredError)) throw err;
    await runLogin({ profile: resolved.name, json: false, quiet: true }, input);
    const refreshed = resolveProfile(resolved.name);
    const { data } = await httpClientFor(refreshed).call<T>(method, path, body);
    return data;
  }
}
