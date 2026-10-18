import { describe, expect, it } from "vitest";
import { AzoaCliError, EXIT_CODES } from "../src/errors.js";

/**
 * plan.md §8 error-code catalog -> task spec's exit-code classes. Trimmed per coordinator
 * review (561adf31): one representative per exit class, not the whole ~17-entry table — each
 * additional code only re-proves the same `CODE_TO_EXIT[code] ?? NETWORK` lookup, which the
 * fallback test below already covers structurally.
 */
const REPRESENTATIVE_CASES: Array<[string, number]> = [
  ["AUTH_FAILED", EXIT_CODES.AUTH],
  ["PERMISSION_DENIED", EXIT_CODES.PERMISSION],
  ["VALIDATION_FAILED", EXIT_CODES.USAGE],
  ["AUDIT_UNAVAILABLE", EXIT_CODES.NETWORK],
  ["NOT_FOUND", EXIT_CODES.USAGE], // confirmed against the real backend's StatusFor switch, not guessed
  ["RATE_LIMITED", EXIT_CODES.NETWORK], // newest code (lane A1 08384168) — proves it was wired in, not just assumed
];

describe("server error code -> CLI exit code mapping", () => {
  it.each(REPRESENTATIVE_CASES)("%s -> exit %i", (code, expectedExit) => {
    const err = AzoaCliError.fromServerCode(code, "server message");
    expect(err.exitCode).toBe(expectedExit);
    expect(err.code).toBe(code);
    expect(err.message).toContain("server message");
  });

  it("formats ADMIN_LOCKED's retryAfterSeconds into the message instead of just naming the code", () => {
    const err = AzoaCliError.fromServerCode("ADMIN_LOCKED", "locked", 5400); // 1h30m — exercises the hours branch
    expect(err.exitCode).toBe(EXIT_CODES.AUTH);
    expect(err.message).toMatch(/1h 30m/);
  });

  it("falls back to the network exit class for an unrecognized server code", () => {
    const err = AzoaCliError.fromServerCode("SOMETHING_NEW_FROM_THE_SERVER", "oops");
    expect(err.exitCode).toBe(EXIT_CODES.NETWORK);
  });

  it("usage/network/auditIntegrity helpers set the documented exit classes", () => {
    expect(AzoaCliError.usage("x").exitCode).toBe(EXIT_CODES.USAGE);
    expect(AzoaCliError.network("x").exitCode).toBe(EXIT_CODES.NETWORK);
    expect(AzoaCliError.auditIntegrity("x").exitCode).toBe(EXIT_CODES.AUDIT_INTEGRITY);
  });
});
