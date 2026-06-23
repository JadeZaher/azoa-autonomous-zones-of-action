/**
 * Cross-platform encoding utilities.
 *
 * Pure JavaScript implementations of base64, base58, and base32.
 * No platform APIs (btoa/atob/Buffer) are used — works in browser,
 * React Native (Hermes), Lynx, and Node without polyfills.
 */

// ---------------------------------------------------------------------------
// Base64
// ---------------------------------------------------------------------------

const B64 =
  "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

export function base64Encode(bytes: Uint8Array): string {
  let result = "";
  const len = bytes.length;

  for (let i = 0; i < len; i += 3) {
    const b0 = bytes[i]!;
    const b1 = i + 1 < len ? bytes[i + 1]! : 0;
    const b2 = i + 2 < len ? bytes[i + 2]! : 0;

    result += B64[b0 >> 2]!;
    result += B64[((b0 & 0x03) << 4) | (b1 >> 4)]!;
    result += i + 1 < len ? B64[((b1 & 0x0f) << 2) | (b2 >> 6)]! : "=";
    result += i + 2 < len ? B64[b2 & 0x3f]! : "=";
  }

  return result;
}

const B64_LOOKUP = (() => {
  const lut = new Uint8Array(256).fill(255);
  for (let i = 0; i < B64.length; i++) lut[B64.charCodeAt(i)] = i;
  return lut;
})();

export function base64Decode(str: string): Uint8Array {
  const s = str.replace(/=/g, "");
  const outputLen = Math.floor((s.length * 6) / 8);
  const output = new Uint8Array(outputLen);

  let idx = 0;
  for (let i = 0; i < s.length; i += 4) {
    const c0 = B64_LOOKUP[s.charCodeAt(i)]!;
    const c1 = B64_LOOKUP[s.charCodeAt(i + 1)]!;
    const c2 = i + 2 < s.length ? B64_LOOKUP[s.charCodeAt(i + 2)]! : 0;
    const c3 = i + 3 < s.length ? B64_LOOKUP[s.charCodeAt(i + 3)]! : 0;

    if (c0 === 255 || c1 === 255) {
      throw new Error(`base64Decode: invalid character at position ${i}`);
    }

    output[idx++] = (c0 << 2) | (c1 >> 4);
    if (idx < outputLen) output[idx++] = ((c1 & 0x0f) << 4) | (c2 >> 2);
    if (idx < outputLen) output[idx++] = ((c2 & 0x03) << 6) | c3;
  }

  return output;
}

// ---------------------------------------------------------------------------
// Base58
// ---------------------------------------------------------------------------

const B58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

const B58_LOOKUP = new Uint8Array(256).fill(255);
for (let i = 0; i < B58.length; i++) {
  B58_LOOKUP[B58.charCodeAt(i)] = i;
}

export function base58Encode(bytes: Uint8Array): string {
  let leadingZeros = 0;
  while (leadingZeros < bytes.length && bytes[leadingZeros] === 0) {
    leadingZeros++;
  }

  const size = Math.ceil(bytes.length * 1.365) + 1;
  const digits = new Uint8Array(size);

  let length = 0;
  for (let i = leadingZeros; i < bytes.length; i++) {
    let carry: number = bytes[i]!;
    let j = 0;
    for (let k = size - 1; carry !== 0 || j < length; k--, j++) {
      carry += 256 * (digits[k] ?? 0);
      digits[k] = carry % 58;
      carry = Math.floor(carry / 58);
    }
    length = j;
  }

  let firstNonZero = size - length;
  while (firstNonZero < size && digits[firstNonZero] === 0) {
    firstNonZero++;
  }

  let result = "1".repeat(leadingZeros);
  for (let i = firstNonZero; i < size; i++) {
    result += B58[digits[i]!]!;
  }

  return result;
}

export function base58Decode(str: string): Uint8Array {
  if (str.length === 0) return new Uint8Array(0);

  let leadingZeros = 0;
  while (leadingZeros < str.length && str[leadingZeros] === "1") {
    leadingZeros++;
  }

  const size = Math.ceil(str.length * 0.733) + 1;
  const bytes = new Uint8Array(size);

  let length = 0;
  for (let i = leadingZeros; i < str.length; i++) {
    const value = B58_LOOKUP[str.charCodeAt(i)]!;
    if (value === 255) {
      throw new Error(
        `base58Decode: invalid character '${str[i]}' at position ${i}`
      );
    }

    let carry: number = value;
    let j = 0;
    for (let k = size - 1; carry !== 0 || j < length; k--, j++) {
      carry += 58 * (bytes[k] ?? 0);
      bytes[k] = carry & 0xff;
      carry >>= 8;
    }
    length = j;
  }

  let firstNonZero = size - length;
  while (firstNonZero < size && bytes[firstNonZero] === 0) {
    firstNonZero++;
  }

  const output = new Uint8Array(leadingZeros + (size - firstNonZero));
  output.set(bytes.subarray(firstNonZero), leadingZeros);

  return output;
}

// ---------------------------------------------------------------------------
// Base32
// ---------------------------------------------------------------------------

const B32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

const B32_LOOKUP = new Uint8Array(256).fill(255);
for (let i = 0; i < B32.length; i++) {
  B32_LOOKUP[B32.charCodeAt(i)] = i;
}

export function base32Encode(bytes: Uint8Array): string {
  let result = "";
  let buffer = 0;
  let bitsLeft = 0;

  for (let i = 0; i < bytes.length; i++) {
    buffer = (buffer << 8) | bytes[i]!;
    bitsLeft += 8;

    while (bitsLeft >= 5) {
      bitsLeft -= 5;
      result += B32[(buffer >> bitsLeft) & 0x1f]!;
    }
  }

  if (bitsLeft > 0) {
    result += B32[(buffer << (5 - bitsLeft)) & 0x1f]!;
  }

  while (result.length % 8 !== 0) {
    result += "=";
  }

  return result;
}

export function base32Decode(str: string): Uint8Array {
  const s = str.toUpperCase().replace(/=/g, "");

  const outputLen = Math.floor((s.length * 5) / 8);
  const output = new Uint8Array(outputLen);

  let buffer = 0;
  let bitsLeft = 0;
  let idx = 0;

  for (let i = 0; i < s.length; i++) {
    const value = B32_LOOKUP[s.charCodeAt(i)]!;
    if (value === 255) {
      throw new Error(
        `base32Decode: invalid character '${s[i]}' at position ${i}`
      );
    }

    buffer = (buffer << 5) | value;
    bitsLeft += 5;

    if (bitsLeft >= 8) {
      bitsLeft -= 8;
      output[idx++] = (buffer >> bitsLeft) & 0xff;
    }
  }

  return output;
}
