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
  /** The server's typed refusal code (`AZOAResult.code`, e.g. `KYB_REQUIRED`), when it sent one. */
  serverCode?: string;
  /** The server's own message without the method/path prefix, when it sent one. */
  serverMessage?: string;
}

export class SdkError extends Error {
  readonly code: SdkErrorCode;
  readonly chain?: string;
  readonly cause?: Error;
  readonly status?: number;
  readonly method?: string;
  readonly path?: string;
  readonly detail?: SdkErrorDetail;
  readonly serverCode?: string;
  readonly serverMessage?: string;

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
    this.serverCode = options?.serverCode;
    this.serverMessage = options?.serverMessage;
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
      ...(this.serverCode !== undefined ? { serverCode: this.serverCode } : {}),
      ...(this.serverMessage !== undefined ? { serverMessage: this.serverMessage } : {}),
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

/** Copy for a person: the server's own message when it sent one, else the SDK message without its `METHOD /path:` prefix. */
export function userFacingMessage(error: SdkError): string {
  return error.serverMessage?.trim() || error.message.replace(/^[A-Z]+ \S+: /, "");
}

/**
 * True when `error` is the optimistic-concurrency conflict an action
 * publish/unpublish/run-start returns after losing a race on `ActionDefinition.Version`
 * (final-hardening F6). The backend has no distinct HTTP status for this case
 * (plain 400 `AZOAResult` with a "conflict" message), so detection is by
 * message substring. Callers should reload the action and retry.
 */
export function isActionConflict(error: SdkError): boolean {
  return (
    error.code === SdkErrorCode.API_ERROR &&
    /(publish|unpublish|action run) conflict/i.test(error.message)
  );
}

/** Server code for a refusal that needs the tenant's business verification (D-6). */
export const KYB_REQUIRED = "KYB_REQUIRED";

/** True when the node refused because the acting tenant's business verification is missing, lapsed or not approved. */
export function isKybRequired(error: unknown): boolean {
  if (!(error instanceof SdkError)) return false;
  return error.serverCode === KYB_REQUIRED || error.message.includes(KYB_REQUIRED);
}

/** Server code: the accepted NFT stage costs no longer cover current costs; the payer quotes and accepts again. */
export const NFT_STAGE_COSTS_CHANGED = "NFT_STAGE_COSTS_CHANGED";

/** True when the node refused to prepare an NFT stage because its accepted costs changed (F-M11). */
export function isNftStageCostsChanged(error: unknown): boolean {
  return error instanceof SdkError && error.serverCode === NFT_STAGE_COSTS_CHANGED;
}

/** Server code: `RecentNodeOperatorSession` refused a mutating operator action on a stale operator login (X-7). */
export const RECENT_OPERATOR_LOGIN_REQUIRED = "RECENT_OPERATOR_LOGIN_REQUIRED";

/** True when the node refused an operator action because the caller's own login is more than 10 minutes old. */
export function isRecentOperatorLoginRequired(error: unknown): boolean {
  return error instanceof SdkError && error.serverCode === RECENT_OPERATOR_LOGIN_REQUIRED;
}
