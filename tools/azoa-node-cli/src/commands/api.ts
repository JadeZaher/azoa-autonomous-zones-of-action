import { readFileSync } from "node:fs";
import { AzoaCliError } from "../errors.js";
import { requireSession, resolveProfile } from "../context.js";
import type { InputSource } from "../io/input.js";
import { createRealInputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { mutateWithStepUpRetry } from "../mutate.js";

export interface ApiOptions {
  profile?: string;
  data?: string;
  json: boolean;
}

const ABSOLUTE_URL_PATTERN = /^[a-z][a-z0-9+.-]*:\/\//i;

function parseBody(data: string | undefined): unknown {
  if (data === undefined) return undefined;
  const isFile = data.startsWith("@");
  const raw = isFile ? readFileSync(data.slice(1), "utf8") : data;
  try {
    return JSON.parse(raw);
  } catch {
    // Never echo file contents into an error — the file may hold secrets the user didn't mean
    // to put in a terminal scrollback/log. For inline --data, a short prefix is still useful.
    if (isFile) throw AzoaCliError.usage(`--data file '${data.slice(1)}' does not contain valid JSON.`);
    throw AzoaCliError.usage(`--data is not valid JSON: ${raw.slice(0, 80)}`);
  }
}

/** Generic authenticated passthrough to any operator/node-admin route (plan.md §5/§8). */
export async function runApi(
  method: string,
  path: string,
  options: ApiOptions,
  input: InputSource = createRealInputSource(),
): Promise<unknown> {
  if (!path.startsWith("/api/")) {
    throw AzoaCliError.usage(`path must start with '/api/', got '${path}'.`);
  }
  if (ABSOLUTE_URL_PATTERN.test(path) || path.startsWith("//")) {
    throw AzoaCliError.usage(`path must be relative, got an absolute URL '${path}'.`);
  }
  // Re-check AFTER URL normalization: a path like "/api/../secret" starts with "/api/" as a raw
  // string but resolves outside it (dot-segment traversal). The base origin here is a throwaway
  // — only path normalization semantics matter, not an actual network target.
  const normalizedPath = new URL(path, "http://azoa-node-cli.invalid").pathname;
  if (!normalizedPath.startsWith("/api/")) {
    throw AzoaCliError.usage(`path '${path}' resolves to '${normalizedPath}', which is outside '/api/'.`);
  }

  const body = parseBody(options.data);
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);

  const data = await mutateWithStepUpRetry<unknown>(resolved, method, path, body, input);
  emit(options.json, JSON.stringify(data, null, 2), data);
  return data;
}
