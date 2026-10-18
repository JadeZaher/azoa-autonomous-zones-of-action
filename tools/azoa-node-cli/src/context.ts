import { AzoaCliError } from "./errors.js";
import { getGlobalNodeOverride } from "./globalNodeOverride.js";
import { AzoaHttpClient } from "./http/client.js";
import { getProfile, resolveProfileName, type Profile } from "./storage/config.js";
import { loadSession, type StoredSession } from "./storage/session.js";

export interface ResolvedProfile {
  name: string;
  profile: Profile;
  session: StoredSession | null;
}

export function resolveProfile(explicitName: string | undefined, nodeOverride?: string): ResolvedProfile {
  const name = resolveProfileName(explicitName);
  const profile = getProfile(name);
  const session = loadSession(name);
  const effectiveOverride = nodeOverride ?? getGlobalNodeOverride();
  return { name, profile: effectiveOverride ? { ...profile, node: effectiveOverride } : profile, session };
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
