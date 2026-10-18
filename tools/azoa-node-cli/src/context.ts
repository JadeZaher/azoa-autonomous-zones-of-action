import { AzoaCliError } from "./errors.js";
import { AzoaHttpClient } from "./http/client.js";
import { getProfile, resolveProfileName, type Profile } from "./storage/config.js";
import { loadSession, type StoredSession } from "./storage/session.js";

export interface ResolvedProfile {
  name: string;
  profile: Profile;
  session: StoredSession | null;
}

/**
 * H1 security fix: a profile's `node` origin is never overridable per-invocation once it has a
 * session. An earlier `--node` global flag let an authenticated command be redirected to an
 * attacker-controlled origin while still attaching the real bearer token (and defeated the H1
 * audience pin on login, since the override controlled both sides of that comparison). There is
 * no override mechanism anymore — `profile.node` is always exactly what `profile add`/`enroll`
 * pinned, read straight from `~/.azoa/config.json`.
 */
export function resolveProfile(explicitName: string | undefined): ResolvedProfile {
  const name = resolveProfileName(explicitName);
  const profile = getProfile(name);
  const session = loadSession(name);
  return { name, profile, session };
}

export function requireSession(resolved: ResolvedProfile): StoredSession {
  if (!resolved.session) {
    throw new AzoaCliError(
      `No session for profile '${resolved.name}'. Run 'azoa-node login --profile ${resolved.name}' first.`,
      3,
      "AUTH_FAILED",
    );
  }
  return resolved.session;
}

export function httpClientFor(resolved: ResolvedProfile): AzoaHttpClient {
  return new AzoaHttpClient(resolved.profile.node, () => resolved.session?.accessToken);
}
