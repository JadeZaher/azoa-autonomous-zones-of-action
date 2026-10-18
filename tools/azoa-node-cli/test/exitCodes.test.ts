import { describe, expect, it } from "vitest";
import { AzoaCliError, EXIT_CODES } from "../src/errors.js";

/** plan.md §8 error-code catalog -> task spec's exit-code classes, table-driven. */
const CASES: Array<[string, number]> = [
  ["AUTH_FAILED", EXIT_CODES.AUTH],
  ["STEP_UP_REQUIRED", EXIT_CODES.AUTH],
  ["PERMISSION_DENIED", EXIT_CODES.PERMISSION],
  ["DOMINANCE_REQUIRED", EXIT_CODES.PERMISSION],
  ["ESCALATION_REFUSED", EXIT_CODES.PERMISSION],
  ["LAST_OWNER", EXIT_CODES.PERMISSION],
  ["SELF_ACTION_REFUSED", EXIT_CODES.PERMISSION],
  ["ENROLLMENT_ALREADY_STARTED", EXIT_CODES.USAGE],
  ["ENROLLMENT_INVALID", EXIT_CODES.AUTH],
  ["FINGERPRINT_MISMATCH", EXIT_CODES.AUTH],
  ["ADMIN_LOCKED", EXIT_CODES.AUTH],
  ["USERNAME_TAKEN", EXIT_CODES.USAGE],
  ["ROLE_IN_USE", EXIT_CODES.USAGE],
  ["ROLE_BUILT_IN", EXIT_CODES.USAGE],
  ["UNKNOWN_PERMISSION", EXIT_CODES.USAGE],
  ["VALIDATION_FAILED", EXIT_CODES.USAGE],
  ["AUDIT_UNAVAILABLE", EXIT_CODES.NETWORK],
];

describe("server error code -> CLI exit code mapping", () => {
  it.each(CASES)("%s -> exit %i", (code, expectedExit) => {
    const err = AzoaCliError.fromServerCode(code, "server message");
    expect(err.exitCode).toBe(expectedExit);
    expect(err.code).toBe(code);
    expect(err.message).toContain("server message");
  });

  it("falls back to the network exit class for an unrecognized server code", () => {
    const err = AzoaCliError.fromServerCode("SOMETHING_NEW_FROM_THE_SERVER", "oops");
    expect(err.exitCode).toBe(EXIT_CODES.NETWORK);
  });

  it("usage/network/audit-integrity helpers set the documented exit classes", () => {
    expect(AzoaCliError.usage("x").exitCode).toBe(EXIT_CODES.USAGE);
    expect(AzoaCliError.network("x").exitCode).toBe(EXIT_CODES.NETWORK);
    expect(AzoaCliError.auditIntegrity("x").exitCode).toBe(EXIT_CODES.AUDIT_INTEGRITY);
  });
});
