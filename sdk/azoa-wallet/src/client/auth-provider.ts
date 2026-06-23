import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";
import type { SessionManager } from "./session.js";
import { AzoaApiClient } from "../api/client.js";

/**
 * OAuth-compatible auth provider using AZOA avatars as the identity source.
 *
 * Enables external apps to use AZOA as their authentication backend:
 * ```ts
 * const auth = azoa.createAuthProvider({ appName: "MyApp" });
 *
 * // Login flow
 * const session = await auth.login(email, password);
 *
 * // Check auth state
 * if (auth.isAuthenticated) {
 *   const profile = await auth.getProfile();
 * }
 *
 * // Use as a token provider for other services
 * const token = auth.getAccessToken();
 * ```
 */
export interface AuthProviderConfig {
  /** Display name of the consuming application. */
  appName?: string;
}

export interface AuthProfile {
  avatarId: string;
  username: string;
  email: string;
  title?: string;
  firstName?: string;
  lastName?: string;
  isActive: boolean;
}

export class AzoaAuthProvider {
  private readonly api: AzoaApiClient;
  private readonly session: SessionManager;
  constructor(api: AzoaApiClient, session: SessionManager, config?: AuthProviderConfig) {
    this.api = api;
    this.session = session;
    // appName reserved for future OAuth metadata exchange
    void config?.appName;
  }

  get isAuthenticated(): boolean {
    return this.session.isAuthenticated;
  }

  get avatarId(): string | null {
    return this.session.avatarId;
  }

  /** Login with email/password. Returns the session state. */
  async login(email: string, password: string): Promise<Result<{ avatarId: string; token: string }, SdkError>> {
    const result = await this.session.login(this.api, email, password);
    if (!result.ok) return result;

    const state = result.value;
    if (!state.token || !state.avatarId) {
      return err(new SdkError(SdkErrorCode.API_ERROR, "Login succeeded but no token/avatarId returned"));
    }

    return ok({ avatarId: state.avatarId, token: state.token });
  }

  /** Register a new avatar account. */
  async register(params: {
    email: string;
    password: string;
    username: string;
    firstName?: string;
    lastName?: string;
  }): Promise<Result<{ avatarId: string; token: string }, SdkError>> {
    const result = await this.session.register(this.api, params);
    if (!result.ok) return result;

    const session = result.value.session;
    if (!session.token || !session.avatarId) {
      return err(new SdkError(SdkErrorCode.API_ERROR, "Registration succeeded but session is incomplete"));
    }

    return ok({ avatarId: session.avatarId, token: session.token });
  }

  /** Get the authenticated avatar's profile. */
  async getProfile(): Promise<Result<AuthProfile, SdkError>> {
    if (!this.session.avatarId) {
      return err(new SdkError(SdkErrorCode.API_ERROR, "Not authenticated. Call login() first."));
    }

    const result = await this.api.getAvatar(this.session.avatarId);
    if (!result.ok) {
      const status = result.error.status;
      if (status === 401 || status === 403 || status === 404) {
        await this.session.logout().catch(() => {});
      }
      return result;
    }

    const avatar = result.value;
    return ok({
      avatarId: avatar.id,
      username: avatar.username,
      email: avatar.email,
      title: avatar.title,
      firstName: avatar.firstName,
      lastName: avatar.lastName,
      isActive: avatar.isActive,
    });
  }

  /** Get the current JWT access token (for passing to other services). */
  getAccessToken(): string | null {
    return this.session.token;
  }

  /** Logout and clear session. */
  async logout(): Promise<void> {
    await this.session.logout();
  }
}
