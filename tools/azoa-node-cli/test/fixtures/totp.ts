import { createHmac } from "node:crypto";

/**
 * RFC 4226 HOTP + RFC 6238 TOTP (SHA1), used only by test infrastructure (the
 * fake node server below, and tests that need to produce a "user typed this
 * code" value). The real CLI never computes TOTP — the human reads it off
 * their authenticator app.
 */
export function hotp(secret: Buffer, counter: number, digits = 6): string {
  const counterBuf = Buffer.alloc(8);
  counterBuf.writeBigUInt64BE(BigInt(counter));
  const hmac = createHmac("sha1", secret).update(counterBuf).digest();
  const offset = hmac[hmac.length - 1]! & 0x0f;
  const binary =
    ((hmac[offset]! & 0x7f) << 24) |
    ((hmac[offset + 1]! & 0xff) << 16) |
    ((hmac[offset + 2]! & 0xff) << 8) |
    (hmac[offset + 3]! & 0xff);
  const str = String(binary % 10 ** digits);
  return str.padStart(digits, "0");
}

export function totp(secret: Buffer, unixSeconds: number, step = 30, digits = 6): string {
  return hotp(secret, Math.floor(unixSeconds / step), digits);
}

/** base32 decode (RFC 4648, no padding) — enough for TOTP secrets. */
export function base32Decode(input: string): Buffer {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  const clean = input.toUpperCase().replace(/=+$/, "");
  let bits = "";
  for (const char of clean) {
    const idx = alphabet.indexOf(char);
    if (idx === -1) throw new Error(`Invalid base32 character: ${char}`);
    bits += idx.toString(2).padStart(5, "0");
  }
  const bytes: number[] = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) {
    bytes.push(parseInt(bits.slice(i, i + 8), 2));
  }
  return Buffer.from(bytes);
}

export function base32Encode(buf: Buffer): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const byte of buf) bits += byte.toString(2).padStart(8, "0");
  let output = "";
  for (let i = 0; i + 5 <= bits.length; i += 5) {
    output += alphabet[parseInt(bits.slice(i, i + 5), 2)];
  }
  const remainder = bits.length % 5;
  if (remainder !== 0) {
    const last = bits.slice(bits.length - remainder).padEnd(5, "0");
    output += alphabet[parseInt(last, 2)];
  }
  return output;
}
