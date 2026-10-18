import { AzoaCliError } from "../errors.js";
import { STEP_UP_HEADER, STEP_UP_HEADER_VALUE, type AzoaResult } from "./types.js";

export class StepUpRequiredError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "StepUpRequiredError";
  }
}

/**
 * Bare-status -> error-code fallback for responses with no parseable AZOAResult body (join-
 * report.md fix). A revoked/expired/malformed bearer is rejected by the ASP.NET Core JWT bearer
 * middleware itself, which emits its default challenge with an EMPTY body — before any
 * controller or AZOAResult envelope exists, for every endpoint, by design. Without this map, any
 * such response collapsed into NETWORK(5) regardless of status, so e.g. `whoami` after revoking
 * your own session exited 5 instead of the documented AUTH(3).
 */
const STATUS_FALLBACK_CODE: Record<number, string> = {
  401: "AUTH_FAILED",
  403: "PERMISSION_DENIED",
  404: "NOT_FOUND",
  409: "CONFLICT",
  423: "ADMIN_LOCKED",
  429: "RATE_LIMITED",
};

export interface RawResponse {
  status: number;
  headers: Headers;
  result: AzoaResult<unknown> | null;
}

export interface CallOptions {
  /** Set by `commands/api.ts` after its own redirect/origin validation; everything else uses defaults. */
  allowStepUpError?: boolean;
}

/**
 * Thin fetch wrapper: attaches the bearer only to the origin it was built
 * for (never forwarded cross-origin), never follows redirects, and unwraps
 * the AZOAResult envelope.
 */
export class AzoaHttpClient {
  private readonly originUrl: URL;

  constructor(
    private readonly origin: string,
    private readonly bearerProvider?: () => string | undefined,
  ) {
    this.originUrl = new URL(origin);
  }

  private resolveUrl(path: string): URL {
    const url = new URL(path, this.origin);
    if (url.origin !== this.originUrl.origin) {
      throw AzoaCliError.usage(`Refusing cross-origin request: ${url.origin} is not ${this.originUrl.origin}`);
    }
    return url;
  }

  async raw(method: string, path: string, body?: unknown): Promise<RawResponse> {
    const url = this.resolveUrl(path);
    const headers: Record<string, string> = {};
    if (body !== undefined) headers["content-type"] = "application/json";
    const token = this.bearerProvider?.();
    if (token) {
      // H1 defense in depth: re-assert the origin equality explicitly at the point the bearer is
      // attached, rather than relying solely on `resolveUrl`'s earlier throw. `resolveUrl` already
      // guarantees `url.origin === this.originUrl.origin` by this point (it throws otherwise), so
      // this can never actually fire today — it exists so a bearer can never leave this origin
      // even if that guarantee is ever weakened elsewhere.
      if (url.origin !== this.originUrl.origin) {
        throw AzoaCliError.usage(`Refusing to attach a bearer token to a cross-origin request: ${url.origin} is not ${this.originUrl.origin}`);
      }
      headers.authorization = `Bearer ${token}`;
    }
    let response: Response;
    try {
      response = await fetch(url, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        redirect: "manual",
      });
    } catch (err) {
      throw AzoaCliError.network(`Network error calling ${method} ${url.pathname}: ${(err as Error).message}`);
    }
    const text = await response.text();
    let result: AzoaResult<unknown> | null = null;
    if (text.length > 0) {
      try {
        result = JSON.parse(text) as AzoaResult<unknown>;
      } catch {
        result = null;
      }
    }
    return { status: response.status, headers: response.headers, result };
  }

  /** Throws AzoaCliError (or StepUpRequiredError when `allowStepUpError`) on any non-success envelope. */
  async call<T>(
    method: string,
    path: string,
    body?: unknown,
    options: CallOptions = {},
  ): Promise<{ status: number; headers: Headers; data: T }> {
    const { status, headers, result } = await this.raw(method, path, body);
    const isStepUp =
      status === 403 && headers.get(STEP_UP_HEADER) === STEP_UP_HEADER_VALUE;
    if (isStepUp) {
      const message = result?.message ?? "Step-up re-authentication required";
      if (options.allowStepUpError) throw new StepUpRequiredError(message);
      throw AzoaCliError.fromServerCode("STEP_UP_REQUIRED", message);
    }
    if (result?.isError) {
      // Prefer the envelope's `code` over the HTTP status for exit-code mapping (plan.md §8 / lane
      // A1 confirmed mapping in NodeAdminResults.StatusFor) — status is only a fallback below.
      throw AzoaCliError.fromServerCode(
        result.code ?? "AUTH_FAILED",
        result.message || `request failed (${status})`,
        result.retryAfterSeconds,
      );
    }
    if (status >= 400) {
      // No parseable AZOAResult body to read a `code` from (see STATUS_FALLBACK_CODE's comment) —
      // map the bare status to the same exit-code family the envelope path would have used
      // (Services/NodeAdmin/AGENTS.md §contract-deviations #7), rather than collapsing every
      // un-enveloped error into NETWORK.
      const fallbackCode = STATUS_FALLBACK_CODE[status];
      if (fallbackCode) {
        throw AzoaCliError.fromServerCode(fallbackCode, `HTTP ${status} calling ${method} ${path}`);
      }
      throw AzoaCliError.network(`HTTP ${status} calling ${method} ${path}`);
    }
    if (!result) {
      throw AzoaCliError.network(`Empty or non-JSON response (${status}) from ${method} ${path}`);
    }
    return { status, headers, data: result.result as T };
  }
}
