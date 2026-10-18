import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";
import type { SessionManager } from "./session.js";
import { AzoaApiClient } from "../api/client.js";

/**
 * OAuth-compatible auth provider using AZOA users as the identity source.
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
  userId: string;
  username: string;
  email: string;
  title?: string;
  firstName?: string;
  lastName?: string;
  isActive: boolean;
  /** Opt-in: this user appears in another caller's search results only when true. */
  discoverable?: boolean;
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

  get userId(): string | null {
    return this.session.userId;
  }

  /** Login with email/password. Returns the session state. */
  async login(email: string, password: string): Promise<Result<{ userId: string; token: string }, SdkError>> {
    const result = await this.session.login(this.api, email, password);
    if (!result.ok) return result;

    const state = result.value;
    if (!state.token || !state.userId) {
      return err(new SdkError(SdkErrorCode.API_ERROR, "Login succeeded but no token/userId returned"));
    }

    return ok({ userId: state.userId, token: state.token });
  }

  /** Register a new user account. */
  async register(params: {
    email: string;
    password: string;
    username: string;
    firstName?: string;
    lastName?: string;
  }): Promise<Result<{ userId: string; token: string }, SdkError>> {
    const result = await this.session.register(this.api, params);
    if (!result.ok) return result;

    const session = result.value.session;
    if (!session.token || !session.userId) {
      return err(new SdkError(SdkErrorCode.API_ERROR, "Registration succeeded but session is incomplete"));
    }

    return ok({ userId: session.userId, token: session.token });
  }

  /** Get the authenticated user's profile. */
  async getProfile(): Promise<Result<AuthProfile, SdkError>> {
    if (!this.session.userId) {
      return err(new SdkError(SdkErrorCode.API_ERROR, "Not authenticated. Call login() first."));
    }

    const result = await this.api.getUser(this.session.userId);
    if (!result.ok) {
      const status = result.error.status;
      if (status === 401 || status === 403 || status === 404) {
        await this.session.logout().catch(() => {});
      }
      return result;
    }

    const user = result.value;
    return ok({
      userId: user.id,
      username: user.username,
      email: user.email,
      title: user.title,
      firstName: user.firstName,
      lastName: user.lastName,
      isActive: user.isActive,
      discoverable: user.discoverable,
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

  /**
   * Server-side "logout everywhere": invalidates every live JWT for this
   * user (bumps its AuthNotBefore watermark), then clears the local
   * session. Use this over {@link logout} for a "log out of all devices"
   * action or when the token may have leaked.
   */
  async logoutEverywhere(): Promise<Result<boolean, SdkError>> {
    const result = await this.api.logoutEverywhere();
    if (!result.ok) return result;

    await this.session.logout();
    return result;
  }
}
