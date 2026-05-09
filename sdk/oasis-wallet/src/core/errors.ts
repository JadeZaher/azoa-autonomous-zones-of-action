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
}

export class SdkError extends Error {
  readonly code: SdkErrorCode;
  readonly chain?: string;
  readonly cause?: Error;

  constructor(
    code: SdkErrorCode,
    message: string,
    options?: { chain?: string; cause?: Error }
  ) {
    super(message);
    this.name = "SdkError";
    this.code = code;
    this.chain = options?.chain;
    this.cause = options?.cause;
  }
}
