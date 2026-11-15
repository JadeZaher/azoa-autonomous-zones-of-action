// Idempotency-key lifecycle for money requests, lifted from the AZOA frontend's `lib/persisted-request-key.ts`.
// See `src/money/AGENTS.md` §request-keys.
import { SdkError, SdkErrorCode } from "../core/errors.js";
import { getRandomBytes } from "../core/platform.js";

/** The subset of Web Storage a key store needs; `localStorage` satisfies it. */
export interface KeyStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

/** Subject-bound persistence for saved requests: one slot per scope. */
export interface KeyStore {
  read(scope: string): string | null;
  write(scope: string, value: string): void;
  remove(scope: string): void;
}

/** A caller-stable key saved before the request leaves, so a lost response resumes instead of charging twice. */
export interface SavedRequest {
  requestKey: string;
  amountMinorUnits: number;
  currencyCode?: string;
  currencyDecimals?: number;
  /** A caller reference sent with the key (e.g. a sale quote id), so a resume replays the identical request. */
  reference?: string;
  /** The server's id for this key (e.g. a checkout intent id), once an answer named it. */
  serverId?: string;
  /** The server holds this request for an operator's decision (`needs_review`): neither resumed as if lost nor replaced. */
  needsReview?: true;
}

/** `ready`: send with this key. `pending`: an unresolved key for a different request must be resolved first. */
export type RequestClaim = { kind: "ready"; request: SavedRequest } | { kind: "pending"; pending: SavedRequest };

export interface KeyStoreOptions {
  /** The host session's subject (e.g. its user id); hashed into every storage key so a shared device never crosses users. */
  subject: string;
  /** Defaults to `localStorage` when the runtime has one, else memory that lives as long as this store. */
  storage?: KeyStorage;
}

/** Storage scopes the SDK's own flows use. */
export const MONEY_SCOPES = { checkout: "checkout", cashOut: "cash-out", sale: "sale" } as const;

/** Binds a storage to one subject; two stores over the same storage and subject see the same saved requests. */
export function createKeyStore({ subject, storage }: KeyStoreOptions): KeyStore {
  if (typeof subject !== "string" || subject.trim() === "") {
    throw new SdkError(SdkErrorCode.INVALID_INPUT, "createKeyStore needs the session's subject so saved requests never cross users.");
  }
  const backing = storage ?? defaultStorage();
  const subjectHash = cyrb53(subject);
  const keyFor = (scope: string) => `azoa-sdk:money:${scope}:${subjectHash}`;
  return {
    read: scope => backing.getItem(keyFor(scope)),
    write: (scope, value) => backing.setItem(keyFor(scope), value),
    remove: scope => backing.removeItem(keyFor(scope)),
  };
}

/** A `KeyStorage` held in memory; it survives a remount only while the same instance is reused. */
export function memoryStorage(): KeyStorage {
  const values = new Map<string, string>();
  return {
    getItem: key => values.get(key) ?? null,
    setItem: (key, value) => void values.set(key, value),
    removeItem: key => void values.delete(key),
  };
}

/** Reuses the saved key for the same request; never replaces an unresolved key for a different one. Persists before returning. */
export function claimRequestKey(store: KeyStore, scope: string, amountMinorUnits: number, reference?: string, denomination?: { currencyCode: string; currencyDecimals?: number }): RequestClaim {
  if (!Number.isSafeInteger(amountMinorUnits) || amountMinorUnits <= 0) {
    throw new SdkError(SdkErrorCode.INVALID_INPUT, "amountMinorUnits must be a positive integer.");
  }
  const saved = readSavedRequest(store, scope, true);
  if (saved) {
    return saved.amountMinorUnits === amountMinorUnits && saved.reference === reference
      ? { kind: "ready", request: saved }
      : { kind: "pending", pending: saved };
  }
  const fresh: SavedRequest = { requestKey: newRequestKey(), amountMinorUnits, ...denomination, ...(reference !== undefined ? { reference } : {}) };
  store.write(scope, JSON.stringify(fresh));
  // A store that silently dropped the write must not let the request leave unkeyed.
  if (readSavedRequest(store, scope)?.requestKey !== fresh.requestKey) {
    throw new SdkError(SdkErrorCode.UNSUPPORTED_OPERATION, "The request key could not be saved on this device, so the request was not sent.");
  }
  return { kind: "ready", request: fresh };
}

/** Records the server id an answer gave for the saved key, so a later visit can resolve it by state. */
export function rememberServerId(store: KeyStore, scope: string, requestKey: string, serverId: string): void {
  const saved = readSavedRequest(store, scope);
  if (saved?.requestKey !== requestKey) return;
  // An answer that names the server's record supersedes an earlier "held for review".
  const { needsReview: _held, ...rest } = saved;
  store.write(scope, JSON.stringify({ ...rest, serverId }));
}

/** Records that the server holds the saved key's request for an operator's decision, so later visits do not offer it as lost. */
export function markNeedsReview(store: KeyStore, scope: string, requestKey: string): void {
  const saved = readSavedRequest(store, scope);
  if (saved?.requestKey === requestKey) store.write(scope, JSON.stringify({ ...saved, needsReview: true }));
}

export function readSavedRequest(store: KeyStore, scope: string, strict = false): SavedRequest | null {
  try {
    const raw = store.read(scope);
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) throw new TypeError("Unreadable saved request.");
    const value = parsed as Partial<SavedRequest>;
    if (typeof value.requestKey !== "string" || !/^[A-Za-z0-9._:-]{1,128}$/u.test(value.requestKey)
      || !Number.isSafeInteger(value.amountMinorUnits) || (value.amountMinorUnits as number) <= 0
      || (value.reference !== undefined && (typeof value.reference !== "string" || !value.reference))
      || (value.serverId !== undefined && (typeof value.serverId !== "string" || !value.serverId))
      || (value.needsReview !== undefined && value.needsReview !== true)
      || (value.currencyCode !== undefined && (typeof value.currencyCode !== "string" || !/^[A-Za-z][A-Za-z0-9._-]{0,15}$/u.test(value.currencyCode)))
      || (value.currencyDecimals !== undefined && (!Number.isInteger(value.currencyDecimals) || value.currencyDecimals < 0 || value.currencyDecimals > 18)))
      throw new TypeError("Unreadable saved request.");
    return { requestKey: value.requestKey, amountMinorUnits: value.amountMinorUnits as number,
      ...(value.reference !== undefined ? { reference: value.reference } : {}),
      ...(value.serverId !== undefined ? { serverId: value.serverId } : {}),
      ...(value.needsReview ? { needsReview: true as const } : {}),
      ...(value.currencyCode !== undefined ? { currencyCode: value.currencyCode } : {}),
      ...(value.currencyDecimals !== undefined ? { currencyDecimals: value.currencyDecimals } : {}),
    };
  } catch {
    if (strict) throw new SdkError(SdkErrorCode.UNSUPPORTED_OPERATION, "An earlier request could not be read safely. Keep its saved data and check again before starting another.");
    return null;
  }
}

/** Forgets the key once the server gave a definite answer for it; with `requestKey`, only while that key is still the saved one. */
export function clearSavedRequest(store: KeyStore, scope: string, requestKey?: string): void {
  if (requestKey !== undefined && readSavedRequest(store, scope)?.requestKey !== requestKey) return;
  store.remove(scope);
}

/** A fresh RFC 4122 v4 id; falls back to `getRandomValues` where `randomUUID` is missing (non-secure origins). */
export function newRequestKey(): string {
  const cryptoApi = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
  if (typeof cryptoApi?.randomUUID === "function") return cryptoApi.randomUUID();
  const bytes = getRandomBytes(16);
  bytes[6] = (bytes[6]! & 0x0f) | 0x40;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function defaultStorage(): KeyStorage {
  try {
    const candidate = (globalThis as { localStorage?: KeyStorage }).localStorage;
    if (candidate && typeof candidate.getItem === "function") return candidate;
  } catch {
    // Sandboxed frames throw SecurityError on access; fall back to memory.
  }
  return memoryStorage();
}

// cyrb53 (bryc, public domain): 53-bit string hash for namespacing only, not a security boundary.
// https://github.com/bryc/code/blob/master/jshash/experimental/cyrb53.js
function cyrb53(text: string): string {
  let h1 = 0xdeadbeef;
  let h2 = 0x41c6ce57;
  for (let index = 0; index < text.length; index++) {
    const code = text.charCodeAt(index);
    h1 = Math.imul(h1 ^ code, 2654435761);
    h2 = Math.imul(h2 ^ code, 1597334677);
  }
  h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507);
  h1 ^= Math.imul(h2 ^ (h2 >>> 13), 3266489909);
  h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507);
  h2 ^= Math.imul(h1 ^ (h1 >>> 13), 3266489909);
  return (4294967296 * (2097151 & h2) + (h1 >>> 0)).toString(36);
}
