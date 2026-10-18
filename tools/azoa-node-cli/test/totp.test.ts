import { describe, expect, it } from "vitest";
import { base32Decode, base32Encode, hotp, totp } from "./fixtures/totp.js";

// RFC 6238 Appendix B, SHA1 column (8-digit codes at step=30).
const RFC6238_SECRET = Buffer.from("12345678901234567890", "ascii");
const RFC6238_SHA1_VECTORS: Array<[number, string]> = [
  [59, "94287082"],
  [1111111109, "07081804"],
  [1111111111, "14050471"],
  [1234567890, "89005924"],
  [2000000000, "69279037"],
];

describe("TOTP (RFC 6238 SHA1 test vectors, used by the fake node server)", () => {
  it.each(RFC6238_SHA1_VECTORS)("T=%i -> %s", (time, expected) => {
    expect(totp(RFC6238_SECRET, time, 30, 8)).toBe(expected);
  });

  it("truncates to 6 digits for the node-admin wire format", () => {
    const code = totp(RFC6238_SECRET, 59, 30, 6);
    expect(code).toMatch(/^\d{6}$/);
    expect(code).toBe("287082"); // last 6 digits of the RFC vector's 8-digit code
  });

  it("round-trips base32 encode/decode (used for the enrollment secret)", () => {
    const secret = Buffer.from("this is a 20 byte secret!!");
    const encoded = base32Encode(secret);
    expect(base32Decode(encoded).equals(secret)).toBe(true);
  });

  it("hotp is deterministic per counter and differs across adjacent counters", () => {
    const secret = Buffer.from("another secret");
    expect(hotp(secret, 42)).toBe(hotp(secret, 42));
    expect(hotp(secret, 42)).not.toBe(hotp(secret, 43));
  });
});
