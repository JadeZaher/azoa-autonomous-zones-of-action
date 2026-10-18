import { readFileSync } from "node:fs";
import { AzoaCliError } from "../errors.js";
import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { StepUpRequiredError } from "../http/client.js";
import type { InputSource } from "../io/input.js";
import { createRealInputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { runLogin } from "./login.js";

export interface ApiOptions {
  profile?: string;
  data?: string;
  json: boolean;
}

const ABSOLUTE_URL_PATTERN = /^[a-z][a-z0-9+.-]*:\/\//i;

function parseBody(data: string | undefined): unknown {
  if (data === undefined) return undefined;
  const raw = data.startsWith("@") ? readFileSync(data.slice(1), "utf8") : data;
  try {
    return JSON.parse(raw);
  } catch {
    throw AzoaCliError.usage(`--data is not valid JSON: ${raw.slice(0, 80)}`);
  }
}

/** Generic authenticated passthrough to any operator/node-admin route (plan.md §5/§8). */
export async function runApi(method: string, path: string, options: ApiOptions, input: InputSource = createRealInputSource()): Promise<unknown> {
  if (!path.startsWith("/api/")) {
    throw AzoaCliError.usage(`path must start with '/api/', got '${path}'.`);
  }
  if (ABSOLUTE_URL_PATTERN.test(path) || path.startsWith("//")) {
    throw AzoaCliError.usage(`path must be relative, got an absolute URL '${path}'.`);
  }

  const body = parseBody(options.data);
  let resolved = resolveProfile(options.profile);
  requireSession(resolved);
  let client = httpClientFor(resolved);

  try {
    const { data } = await client.call<unknown>(method, path, body, { allowStepUpError: true });
    emit(options.json, JSON.stringify(data, null, 2), data);
    return data;
  } catch (err) {
    if (!(err instanceof StepUpRequiredError)) throw err;
    await runLogin({ profile: resolved.name, json: options.json }, input);
    resolved = resolveProfile(options.profile);
    client = httpClientFor(resolved);
    const { data } = await client.call<unknown>(method, path, body);
    emit(options.json, JSON.stringify(data, null, 2), data);
    return data;
  }
}
