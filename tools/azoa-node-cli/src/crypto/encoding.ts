import { randomBytes } from "node:crypto";

/** base64url (no padding) encode. */
export function toBase64Url(buf: Buffer): string {
  return buf.toString("base64").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** base64url (no padding) decode. */
export function fromBase64Url(value: string): Buffer {
  const padded = value.replace(/-/g, "+").replace(/_/g, "/");
  return Buffer.from(padded, "base64");
}

/** Cryptographically random token, hex-encoded. */
export function randomHexToken(byteLength: number): string {
  return randomBytes(byteLength).toString("hex");
}
