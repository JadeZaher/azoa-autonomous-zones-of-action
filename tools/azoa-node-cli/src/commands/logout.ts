import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { AzoaCliError } from "../errors.js";
import { StepUpRequiredError } from "../http/client.js";
import { createRealInputSource, type InputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { mutateWithStepUpRetry } from "../mutate.js";
import { clearSession } from "../storage/session.js";

export interface LogoutOptions {
  profile?: string;
  json: boolean;
}

export async function runLogout(options: LogoutOptions, input: InputSource = createRealInputSource()): Promise<void> {
  const resolved = resolveProfile(options.profile);
  const session = requireSession(resolved);
  const client = httpClientFor(resolved);
  try {
    await client.call("POST", "/api/node-admin/auth/revoke", undefined, { allowStepUpError: true });
  } catch (error) {
    if (error instanceof StepUpRequiredError) {
      const { data: sessions } = await client.call<{ handle: string; current: boolean }[]>("GET", "/api/node-admin/sessions");
      const original = sessions.find((row) => row.current);
      if (!original?.handle) throw AzoaCliError.network("The node did not identify the current session; logout was not completed.");
      await mutateWithStepUpRetry(resolved, "POST", `/api/node-admin/sessions/${encodeURIComponent(original.handle)}/revoke`, undefined, input);
      const refreshed = resolveProfile(resolved.name);
      if (refreshed.session?.accessToken !== session.accessToken)
        await httpClientFor(refreshed).call("POST", "/api/node-admin/auth/revoke");
    } else if (!(error instanceof AzoaCliError) || error.code !== "AUTH_FAILED") {
      throw error;
    }
  }
  clearSession(resolved.name);
  emit(options.json, `Logged out '${resolved.name}' (${session.principal}).`, { profile: resolved.name });
}
