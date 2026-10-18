import { AzoaCliError } from "../errors.js";
import { STEP_UP_HEADER, STEP_UP_HEADER_VALUE, type AzoaResult } from "./types.js";

export class StepUpRequiredError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "StepUpRequiredError";
  }
}

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
    if (token && url.origin === this.originUrl.origin) {
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
      throw AzoaCliError.fromServerCode(result.code ?? "AUTH_FAILED", result.message || `request failed (${status})`);
    }
    if (status >= 400) {
      throw AzoaCliError.network(`HTTP ${status} calling ${method} ${path}`);
    }
    if (!result) {
      throw AzoaCliError.network(`Empty or non-JSON response (${status}) from ${method} ${path}`);
    }
    return { status, headers, data: result.result as T };
  }
}
