import { describe, it, expect } from "vitest";
import { SdkError, SdkErrorCode } from "../../src/core/errors.js";

describe("SdkError", () => {
  it("creates error with code and message", () => {
    const error = new SdkError(SdkErrorCode.NETWORK_ERROR, "connection refused");
    expect(error.code).toBe(SdkErrorCode.NETWORK_ERROR);
    expect(error.message).toBe("connection refused");
    expect(error.name).toBe("SdkError");
    expect(error.chain).toBeUndefined();
    expect(error.cause).toBeUndefined();
  });

  it("includes chain context", () => {
    const error = new SdkError(SdkErrorCode.SIGNING_ERROR, "bad key", { chain: "algorand" });
    expect(error.chain).toBe("algorand");
  });

  it("wraps cause error", () => {
    const cause = new Error("original");
    const error = new SdkError(SdkErrorCode.UNKNOWN, "wrapped", { cause });
    expect(error.cause).toBe(cause);
  });

  it("is an instance of Error", () => {
    const error = new SdkError(SdkErrorCode.API_ERROR, "test");
    expect(error).toBeInstanceOf(Error);
  });

  it("carries HTTP diagnostics and server detail", () => {
    const error = new SdkError(SdkErrorCode.API_ERROR, "boom", {
      status: 500,
      method: "GET",
      path: "/api/bridge/history",
      detail: { type: "System.InvalidOperationException", message: "db down" },
    });
    expect(error.status).toBe(500);
    expect(error.method).toBe("GET");
    expect(error.path).toBe("/api/bridge/history");
    expect(error.detail?.message).toBe("db down");
  });

  it("toJSON exposes the non-enumerable message plus diagnostics", () => {
    const error = new SdkError(SdkErrorCode.API_ERROR, "the real reason", {
      status: 500,
      method: "GET",
      path: "/api/bridge/history",
    });
    // Plain JSON.stringify drops Error.message — toJSON must restore it.
    const json = JSON.parse(JSON.stringify(error)) as Record<string, unknown>;
    expect(json.message).toBe("the real reason");
    expect(json.code).toBe("API_ERROR");
    expect(json.status).toBe(500);
    expect(json.path).toBe("/api/bridge/history");
  });

  it("debugString renders the server exception chain", () => {
    const error = new SdkError(SdkErrorCode.API_ERROR, "GET /api/x: failed", {
      status: 500,
      method: "GET",
      path: "/api/x",
      detail: {
        type: "System.Exception",
        message: "outer",
        inner: { type: "System.IO.IOException", message: "inner cause" },
      },
    });
    const s = error.debugString();
    expect(s).toContain("GET /api/x");
    expect(s).toContain("status:  500");
    expect(s).toContain("System.Exception: outer");
    expect(s).toContain("caused by: System.IO.IOException: inner cause");
  });

  it("has all expected error codes", () => {
    expect(SdkErrorCode.NETWORK_ERROR).toBe("NETWORK_ERROR");
    expect(SdkErrorCode.SIGNING_ERROR).toBe("SIGNING_ERROR");
    expect(SdkErrorCode.INVALID_ADDRESS).toBe("INVALID_ADDRESS");
    expect(SdkErrorCode.INSUFFICIENT_FUNDS).toBe("INSUFFICIENT_FUNDS");
    expect(SdkErrorCode.DEX_ERROR).toBe("DEX_ERROR");
    expect(SdkErrorCode.API_ERROR).toBe("API_ERROR");
    expect(SdkErrorCode.PROVIDER_NOT_FOUND).toBe("PROVIDER_NOT_FOUND");
    expect(SdkErrorCode.UNSUPPORTED_OPERATION).toBe("UNSUPPORTED_OPERATION");
    expect(SdkErrorCode.UNKNOWN).toBe("UNKNOWN");
  });
});
