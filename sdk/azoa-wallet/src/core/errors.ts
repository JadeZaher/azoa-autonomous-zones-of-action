export enum SdkErrorCode {
  NETWORK_ERROR = "NETWORK_ERROR",
  SIGNING_ERROR = "SIGNING_ERROR",
  INVALID_ADDRESS = "INVALID_ADDRESS",
  INSUFFICIENT_FUNDS = "INSUFFICIENT_FUNDS",
  DEX_ERROR = "DEX_ERROR",
  API_ERROR = "API_ERROR",
  PROVIDER_NOT_FOUND = "PROVIDER_NOT_FOUND",
  UNSUPPORTED_OPERATION = "UNSUPPORTED_OPERATION",
  UNKNOWN = "UNKNOWN",
  AUTH_EXPIRED = "AUTH_EXPIRED",
  INVALID_INPUT = "INVALID_INPUT",
}

/**
 * Verbose, server-provided error detail. Mirrors the .NET `ErrorDetail` and is
 * only populated when the backend is running with debug mode enabled
 * (`AZOA:DebugErrors`). In production this is always `undefined`.
 */
export interface SdkErrorDetail {
  type?: string;
  message?: string;
  stackTrace?: string;
  inner?: SdkErrorDetail;
}

export interface SdkErrorOptions {
  chain?: string;
  cause?: Error;
  /** HTTP status code, when the failure came from an API response. */
  status?: number;
  /** HTTP method of the failing request. */
  method?: string;
  /** Request path of the failing request. */
  path?: string;
  /** Server-side exception chain, surfaced when backend debug mode is on. */
  detail?: SdkErrorDetail;
}

export class SdkError extends Error {
  readonly code: SdkErrorCode;
  readonly chain?: string;
  readonly cause?: Error;
  readonly status?: number;
  readonly method?: string;
  readonly path?: string;
  readonly detail?: SdkErrorDetail;

  constructor(
    code: SdkErrorCode,
    message: string,
    options?: SdkErrorOptions
  ) {
    super(message);
    this.name = "SdkError";
    this.code = code;
    this.chain = options?.chain;
    this.cause = options?.cause;
    this.status = options?.status;
    this.method = options?.method;
    this.path = options?.path;
    this.detail = options?.detail;
  }

  /**
   * Make the error fully serializable. `Error.message`/`stack` are
   * non-enumerable, so a plain `JSON.stringify(err)` silently drops them —
   * which is why raw error dumps previously showed only `{name, code}`. This
   * guarantees structured logs and error dumps carry the real diagnostics.
   */
  toJSON(): Record<string, unknown> {
    return {
      name: this.name,
      code: this.code,
      message: this.message,
      ...(this.chain !== undefined ? { chain: this.chain } : {}),
      ...(this.status !== undefined ? { status: this.status } : {}),
      ...(this.method !== undefined ? { method: this.method } : {}),
      ...(this.path !== undefined ? { path: this.path } : {}),
      ...(this.detail !== undefined ? { detail: this.detail } : {}),
      ...(this.cause !== undefined
        ? { cause: { name: this.cause.name, message: this.cause.message } }
        : {}),
    };
  }

  /**
   * Multi-line, human/LLM-friendly rendering of everything known about the
   * failure — including the server-side exception chain when available.
   */
  debugString(): string {
    const lines = [`${this.name} [${this.code}]: ${this.message}`];
    if (this.method !== undefined || this.path !== undefined)
      lines.push(`  request: ${this.method ?? "?"} ${this.path ?? "?"}`);
    if (this.status !== undefined) lines.push(`  status:  ${this.status}`);
    if (this.chain !== undefined) lines.push(`  chain:   ${this.chain}`);
    if (this.detail) {
      lines.push(
        `  server:  ${this.detail.type ?? "Error"}: ${this.detail.message ?? ""}`
      );
      if (this.detail.stackTrace) {
        lines.push(
          this.detail.stackTrace
            .split("\n")
            .map((l) => `    ${l.trim()}`)
            .join("\n")
        );
      }
      let inner = this.detail.inner;
      while (inner) {
        lines.push(
          `  caused by: ${inner.type ?? "Error"}: ${inner.message ?? ""}`
        );
        inner = inner.inner;
      }
    }
    if (this.cause)
      lines.push(`  cause:   ${this.cause.name}: ${this.cause.message}`);
    return lines.join("\n");
  }
}

/**
 * True when `error` is the optimistic-concurrency conflict a quest
 * publish/unpublish/run-start returns after losing a race on `Quest.Version`
 * (final-hardening F6). The backend has no distinct HTTP status for this case
 * (plain 400 `AZOAResult` with a "conflict" message), so detection is by
 * message substring. Callers should reload the quest and retry.
 */
export function isQuestConflict(error: SdkError): boolean {
  return (
    error.code === SdkErrorCode.API_ERROR &&
    /(publish|unpublish|quest run) conflict/i.test(error.message)
  );
}
