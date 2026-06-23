const HEX_CHARS = "0123456789abcdef";

export function toHex(bytes: Uint8Array): string {
  let hex = "";
  for (let i = 0; i < bytes.length; i++) {
    const b = bytes[i]!;
    hex += HEX_CHARS[b >> 4]! + HEX_CHARS[b & 0x0f]!;
  }
  return hex;
}

export function fromHex(hex: string): Uint8Array {
  if (hex.length % 2 !== 0) throw new Error("Invalid hex string length");
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < bytes.length; i++) {
    const hi = hex.charCodeAt(i * 2);
    const lo = hex.charCodeAt(i * 2 + 1);
    bytes[i] = (hexVal(hi) << 4) | hexVal(lo);
  }
  return bytes;
}

function hexVal(charCode: number): number {
  if (charCode >= 48 && charCode <= 57) return charCode - 48; // 0-9
  if (charCode >= 97 && charCode <= 102) return charCode - 87; // a-f
  if (charCode >= 65 && charCode <= 70) return charCode - 55; // A-F
  throw new Error(`Invalid hex character: ${String.fromCharCode(charCode)}`);
}

export function concatBytes(...arrays: Uint8Array[]): Uint8Array {
  let totalLength = 0;
  for (const arr of arrays) totalLength += arr.length;
  const result = new Uint8Array(totalLength);
  let offset = 0;
  for (const arr of arrays) {
    result.set(arr, offset);
    offset += arr.length;
  }
  return result;
}

export function equalsBytes(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) return false;
  }
  return true;
}
