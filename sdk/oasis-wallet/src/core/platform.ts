/**
 * Cross-platform crypto and environment detection.
 *
 * Handles differences between browser, React Native (Hermes engine),
 * Lynx, and Node.js without importing any platform-specific modules.
 */

/* eslint-disable @typescript-eslint/no-explicit-any */
declare const navigator: { product?: string } | undefined;
declare const window: unknown;
declare const document: unknown;

// ---------------------------------------------------------------------------
// Platform detection
// ---------------------------------------------------------------------------

export type Platform = "browser" | "react-native" | "node" | "unknown";

/**
 * Detect the current JavaScript runtime environment.
 *
 * Detection order matters — React Native sets `navigator.product` but also
 * exposes a partial `window`, so it must be checked before browser detection.
 */
export function getPlatform(): Platform {
  // React Native: Hermes / JSC sets navigator.product = "ReactNative"
  if (
    typeof navigator !== "undefined" &&
    navigator.product === "ReactNative"
  ) {
    return "react-native";
  }

  // Browser: has both window and document
  if (
    typeof window !== "undefined" &&
    typeof document !== "undefined"
  ) {
    return "browser";
  }

  // Node.js: has process with node version info
  if (
    typeof process !== "undefined" &&
    process.versions?.node != null
  ) {
    return "node";
  }

  return "unknown";
}

// ---------------------------------------------------------------------------
// Cryptographically secure random bytes
// ---------------------------------------------------------------------------

/**
 * Generate `length` cryptographically secure random bytes.
 *
 * - Browser / Node 19+ / Lynx: uses `globalThis.crypto.getRandomValues`
 * - React Native: requires the `react-native-get-random-values` polyfill
 *   imported **before** this function is called (typically the first import
 *   in your entry file).
 *
 * @throws {Error} when no secure random source is available.
 */
export function getRandomBytes(length: number): Uint8Array {
  if (
    typeof globalThis !== "undefined" &&
    globalThis.crypto != null &&
    typeof globalThis.crypto.getRandomValues === "function"
  ) {
    const bytes = new Uint8Array(length);
    globalThis.crypto.getRandomValues(bytes);
    return bytes;
  }

  throw new Error(
    "crypto.getRandomValues is not available in this environment. " +
      "In React Native, add `import 'react-native-get-random-values'` as the " +
      "first import in your entry file (e.g. index.js or App.tsx)."
  );
}

// ---------------------------------------------------------------------------
// Feature flags derived from platform
// ---------------------------------------------------------------------------

/**
 * Whether the current environment has a native TextEncoder / TextDecoder.
 * React Native (Hermes) supports these since RN 0.70+.
 */
export function hasTextEncoding(): boolean {
  return (
    typeof globalThis !== "undefined" &&
    typeof (globalThis as Record<string, unknown>)["TextEncoder"] ===
      "function"
  );
}

/**
 * Whether the current environment exposes the Web Crypto subtle API.
 * Useful for hardware-accelerated hashing / ECDSA if available.
 */
export function hasSubtleCrypto(): boolean {
  return (
    typeof globalThis !== "undefined" &&
    globalThis.crypto != null &&
    typeof globalThis.crypto.subtle?.digest === "function"
  );
}
