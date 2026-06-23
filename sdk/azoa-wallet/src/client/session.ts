import type { Result } from "../core/result.js";
import { ok } from "../core/result.js";
import type { SdkError } from "../core/errors.js";
import { AzoaApiClient } from "../api/client.js";
import type { AvatarResponse } from "../api/client.js";

/**
 * Pluggable storage backend for session tokens.
 * Implement this for your platform:
 * - Browser: localStorage adapter
 * - React Native: AsyncStorage or SecureStore adapter
 * - Node: in-memory or file-based
 */
export interface SessionStorage {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
  remove(key: string): Promise<void>;
}

/** In-memory storage for environments without persistence. */
export class MemorySessionStorage implements SessionStorage {
  private store = new Map<string, string>();
  async get(key: string) { return this.store.get(key) ?? null; }
  async set(key: string, value: string) { this.store.set(key, value); }
  async remove(key: string) { this.store.delete(key); }
}

export interface SessionState {
  token: string | null;
  avatarId: string | null;
  isAuthenticated: boolean;
}

export interface SessionManagerConfig {
  storage?: SessionStorage;
  tokenKey?: string;
  avatarIdKey?: string;
  onSessionChange?: (state: SessionState) => void;
}

const DEFAULT_TOKEN_KEY = "azoa_token";
const DEFAULT_AVATAR_KEY = "azoa_avatar_id";

export class SessionManager {
  private readonly storage: SessionStorage;
  private readonly tokenKey: string;
  private readonly avatarIdKey: string;
  private readonly onChange?: (state: SessionState) => void;

  private _token: string | null = null;
  private _avatarId: string | null = null;

  constructor(config?: SessionManagerConfig) {
    this.storage = config?.storage ?? new MemorySessionStorage();
    this.tokenKey = config?.tokenKey ?? DEFAULT_TOKEN_KEY;
    this.avatarIdKey = config?.avatarIdKey ?? DEFAULT_AVATAR_KEY;
    this.onChange = config?.onSessionChange;
  }

  get token(): string | null { return this._token; }
  get avatarId(): string | null { return this._avatarId; }
  get isAuthenticated(): boolean { return this._token != null; }

  /** Restore session from storage (call on app start). */
  async restore(): Promise<SessionState> {
    this._token = await this.storage.get(this.tokenKey);
    this._avatarId = await this.storage.get(this.avatarIdKey);
    const state = this.state();
    this.onChange?.(state);
    return state;
  }

  /** Login and persist session. */
  async login(
    api: AzoaApiClient,
    email: string,
    password: string
  ): Promise<Result<SessionState, SdkError>> {
    const result = await api.login(email, password);
    if (!result.ok) return result;

    const token = result.value;
    const avatarId = this.extractAvatarIdFromJwt(token);

    this._token = token;
    this._avatarId = avatarId;

    await this.storage.set(this.tokenKey, token);
    if (avatarId) await this.storage.set(this.avatarIdKey, avatarId);

    const state = this.state();
    this.onChange?.(state);
    return ok(state);
  }

  /** Register, then login. */
  async register(
    api: AzoaApiClient,
    params: { email: string; password: string; username: string }
  ): Promise<Result<{ avatar: AvatarResponse; session: SessionState }, SdkError>> {
    const regResult = await api.register(params);
    if (!regResult.ok) return regResult;

    const loginResult = await this.login(api, params.email, params.password);
    if (!loginResult.ok) return loginResult;

    return ok({ avatar: regResult.value, session: loginResult.value });
  }

  /** Clear session. */
  async logout(): Promise<void> {
    this._token = null;
    this._avatarId = null;
    await this.storage.remove(this.tokenKey);
    await this.storage.remove(this.avatarIdKey);
    this.onChange?.(this.state());
  }

  /** Get a token-refresh callback suitable for AzoaApiConfig.onTokenRefresh. */
  createRefreshCallback(): () => Promise<string> {
    return async () => {
      if (this._token) return this._token;
      throw new Error("No session token available. Call login() first.");
    };
  }

  private state(): SessionState {
    return {
      token: this._token,
      avatarId: this._avatarId,
      isAuthenticated: this._token != null,
    };
  }

  private extractAvatarIdFromJwt(token: string): string | null {
    try {
      const parts = token.split(".");
      if (parts.length !== 3) return null;
      // Decode the payload (base64url → standard base64)
      const payload = parts[1]!.replace(/-/g, "+").replace(/_/g, "/");
      const padded = payload + "=".repeat((4 - (payload.length % 4)) % 4);
      const decoded = new TextDecoder().decode(this.base64DecodePayload(padded));
      const claims = JSON.parse(decoded) as Record<string, unknown>;
      return (claims["sub"] as string) ?? (claims["nameid"] as string) ?? null;
    } catch {
      return null;
    }
  }

  private base64DecodePayload(b64: string): Uint8Array {
    const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    const lookup = new Uint8Array(256).fill(255);
    for (let i = 0; i < chars.length; i++) lookup[chars.charCodeAt(i)] = i;
    const s = b64.replace(/=/g, "");
    const out = new Uint8Array(Math.floor((s.length * 3) / 4));
    let idx = 0;
    for (let i = 0; i < s.length; i += 4) {
      const a = lookup[s.charCodeAt(i)]!;
      const b = lookup[s.charCodeAt(i + 1)]!;
      const c = i + 2 < s.length ? lookup[s.charCodeAt(i + 2)]! : 0;
      const d = i + 3 < s.length ? lookup[s.charCodeAt(i + 3)]! : 0;
      out[idx++] = (a << 2) | (b >> 4);
      if (i + 2 < s.length) out[idx++] = ((b & 0xf) << 4) | (c >> 2);
      if (i + 3 < s.length) out[idx++] = ((c & 0x3) << 6) | d;
    }
    return out.slice(0, idx);
  }
}
