import { createServer, type IncomingMessage, type ServerResponse, type Server } from "node:http";
import { randomBytes, randomUUID } from "node:crypto";
import { fingerprintOf, verifySignature } from "../../src/crypto/keys.js";
import { enrollMessage, addKeyMessage, buildCanonicalMessage } from "../../src/crypto/canonical.js";
import { fromBase64Url, toBase64Url } from "../../src/crypto/encoding.js";
import { computeRowHash, GENESIS_PREV_HASH, type AuditRow } from "../../src/audit/chain.js";
import { STEP_UP_HEADER, STEP_UP_HEADER_VALUE } from "../../src/http/types.js";
import { base32Encode, totp } from "./totp.js";
import { buildOtpauthUri } from "./otpauth.js";

/**
 * In-process node:http server implementing plan.md §3/§8 just far enough to
 * exercise the CLI's protocol code with real Ed25519 verification and real
 * TOTP — not a stand-in for lane A's actual backend. Status/code mapping,
 * field shapes and the lockout/mfa-reset/sessions-filter behaviors below are
 * cross-checked against the real controllers/managers (lane A1 f4d5a77e,
 * `Services/NodeAdmin/AGENTS.md` §contract-deviations) rather than guessed.
 */
export interface FakeKey {
  publicKey: string;
  fingerprint: string;
  label: string;
  revokedAt?: string;
}

export interface FakeAdmin {
  id: string;
  username: string;
  displayName: string;
  status: "pending_enrollment" | "pending_activation" | "active" | "suspended" | "revoked";
  roleIds: string[];
  keys: FakeKey[];
  totpSecret?: Buffer;
  totpLastStep?: number;
  /** Second-factor-only lockout counters (signature failures are never counted — see AGENTS.md §auth). */
  failedSecondFactorAttempts: number;
  lockedUntil?: number;
  createdBy: string;
  createdAt: string;
  updatedAt: string;
}

interface FakeChallenge {
  purpose: "root_login" | "admin_login";
  nonce: string;
  expiresAt: string;
  subjectFingerprint: string;
  consumed: boolean;
}

interface FakeEnrollmentToken {
  adminId: string;
  consumed: boolean;
}

interface FakeEnrollment {
  adminId: string;
  publicKey: string;
  fingerprint: string;
  label: string;
  totpSecretPending: Buffer;
  consumed: boolean;
}

interface FakeSession {
  sid: string;
  principal: string;
  permissions: string[];
  authTime: number;
  revoked: boolean;
}

export interface FakeNodeServerOptions {
  audience: string;
  rootPublicKeys?: string[];
  recentWindowMs?: number;
  /** Default matches the real 15-minute lockout; tests only need the error/Retry-After shape, not the wait. */
  lockDurationMs?: number;
}

/** plan.md §8 error-code catalog + lane A1 deviations -> HTTP status (NodeAdminResults.StatusFor). */
const STATUS_FOR_CODE: Record<string, number> = {
  AUTH_FAILED: 401,
  ADMIN_LOCKED: 423,
  PERMISSION_DENIED: 403,
  STEP_UP_REQUIRED: 403,
  DOMINANCE_REQUIRED: 403,
  ESCALATION_REFUSED: 403,
  SELF_ACTION_REFUSED: 403,
  NOT_FOUND: 404,
  LAST_OWNER: 409,
  USERNAME_TAKEN: 409,
  ROLE_IN_USE: 409,
  ROLE_BUILT_IN: 409,
  ENROLLMENT_ALREADY_STARTED: 409,
  FINGERPRINT_MISMATCH: 409,
  CONFLICT: 409,
  AUDIT_UNAVAILABLE: 503,
  NODE_ADMIN_UNAVAILABLE: 503,
  // ENROLLMENT_INVALID and VALIDATION_FAILED are deliberately absent: the real
  // StatusFor switch has no case for them either, so they fall to the 400 default.
};

function statusForCode(code: string): number {
  return STATUS_FOR_CODE[code] ?? 400;
}

const ROOT_PERMISSIONS = ["admins.read", "admins.manage", "roles.read", "roles.manage", "audit.read"];
// Built-in role ids equal their names (lane A1 deviation #8) — no synthetic prefix.
const BUILT_IN_ROLES: Record<string, string[]> = {
  "node-owner": ["admins.read", "admins.manage", "roles.read", "roles.manage", "audit.read"],
  auditor: ["audit.read"],
};
const SECOND_FACTOR_LOCK_THRESHOLD = 5;

export class FakeNodeServer {
  readonly server: Server;
  /** Mutable: tests set this to the real listen() origin after binding (or to a mismatched value for H1 tests). */
  audience: string;
  rootPublicKeys: Set<string>;
  recentWindowMs: number;
  lockDurationMs: number;
  /** Test-only hook: when set, /enroll/start returns this fingerprint instead of the real one
   * (internal bookkeeping still uses the real value), simulating a tampered/misbehaving response. */
  forceEnrollFingerprint?: string;

  readonly admins = new Map<string, FakeAdmin>();
  readonly challenges = new Map<string, FakeChallenge>();
  readonly enrollmentTokens = new Map<string, FakeEnrollmentToken>();
  readonly enrollments = new Map<string, FakeEnrollment>();
  readonly sessions = new Map<string, FakeSession>();
  readonly roles = new Map<string, { id: string; name: string; builtIn: boolean; permissions: string[]; revision: number }>();
  readonly auditRows: AuditRow[] = [];
  private auditSeq = 0;

  requestLog: { method: string; path: string }[] = [];

  constructor(options: FakeNodeServerOptions) {
    this.audience = options.audience;
    this.rootPublicKeys = new Set(options.rootPublicKeys ?? []);
    this.recentWindowMs = options.recentWindowMs ?? 10 * 60 * 1000;
    this.lockDurationMs = options.lockDurationMs ?? 15 * 60 * 1000;
    for (const [name, permissions] of Object.entries(BUILT_IN_ROLES)) {
      this.roles.set(name, { id: name, name, builtIn: true, permissions, revision: 1 });
    }
    this.server = createServer((req, res) => this.handle(req, res));
  }

  async listen(): Promise<string> {
    await new Promise<void>((resolve) => this.server.listen(0, "127.0.0.1", resolve));
    const address = this.server.address();
    if (!address || typeof address === "string") throw new Error("failed to bind fake server");
    return `http://127.0.0.1:${address.port}`;
  }

  async close(): Promise<void> {
    await new Promise<void>((resolve) => this.server.close(() => resolve()));
  }

  private appendAudit(actor: string, action: string, target: string, outcome: string, detail: unknown): void {
    this.auditSeq += 1;
    const prevHash = this.auditRows.at(-1)?.hash ?? GENESIS_PREV_HASH;
    const row: Omit<AuditRow, "hash"> = {
      seq: this.auditSeq,
      at: expiresAtString(new Date()),
      actor,
      action,
      target,
      outcome,
      detail: detail ? JSON.stringify(detail) : "",
      prevHash,
    };
    const hash = computeRowHash(row);
    this.auditRows.push({ ...row, hash });
  }

  private sessionFor(req: IncomingMessage): FakeSession | null {
    const header = req.headers.authorization;
    if (!header?.startsWith("Bearer ")) return null;
    const token = header.slice("Bearer ".length);
    const session = this.sessions.get(token);
    if (!session || session.revoked) return null;
    return session;
  }

  private isRecent(session: FakeSession): boolean {
    return Date.now() - session.authTime <= this.recentWindowMs;
  }

  private async handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const url = new URL(req.url ?? "/", this.audience);
    this.requestLog.push({ method: req.method ?? "GET", path: url.pathname });
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(chunk as Buffer);
    const rawBody = Buffer.concat(chunks).toString("utf8");
    let body: Record<string, unknown> = {};
    if (rawBody.length > 0) {
      try {
        body = JSON.parse(rawBody);
      } catch {
        return this.sendError(res, "VALIDATION_FAILED", "Invalid JSON body");
      }
    }

    try {
      await this.route(req.method ?? "GET", url, body, req, res);
    } catch (err) {
      this.sendError(res, "VALIDATION_FAILED", (err as Error).message);
    }
  }

  private async route(
    method: string,
    url: URL,
    body: Record<string, unknown>,
    req: IncomingMessage,
    res: ServerResponse,
  ): Promise<void> {
    const path = url.pathname;

    if (method === "POST" && path === "/api/node-admin/auth/challenge") return this.handleChallenge(body, res);
    if (method === "POST" && path === "/api/node-admin/auth/session") return this.handleSession(body, res);
    if (method === "POST" && path === "/api/node-admin/auth/revoke") return this.handleRevokeSelf(req, res);
    if (method === "GET" && path === "/api/node-admin/me") return this.handleMe(req, res);
    if (method === "POST" && path === "/api/node-admin/admins") return this.handleCreateAdmin(req, body, res);
    if (method === "GET" && path === "/api/node-admin/admins") return this.handleListAdmins(req, res);
    if (method === "POST" && path === "/api/node-admin/enroll/start") return this.handleEnrollStart(body, res);
    if (method === "POST" && path === "/api/node-admin/enroll/complete") return this.handleEnrollComplete(body, res);
    if (method === "GET" && path === "/api/example/protected") return this.handleProtectedExample(req, res);
    if (method === "GET" && path === "/api/node-admin/audit") return this.handleAuditList(req, url, res);
    if (method === "GET" && path === "/api/node-admin/audit/verify") return this.handleAuditVerifyEndpoint(req, res);
    if (method === "GET" && path === "/api/node-admin/sessions") return this.handleSessionsList(req, url, res);
    const sessionRevokeMatch = path.match(/^\/api\/node-admin\/sessions\/([^/]+)\/revoke$/);
    if (method === "POST" && sessionRevokeMatch) return this.handleSessionRevoke(req, sessionRevokeMatch[1]!, res);

    const adminMatch = path.match(/^\/api\/node-admin\/admins\/([^/]+)(\/.*)?$/);
    if (adminMatch) return this.handleAdminSubroute(method, adminMatch[1]!, adminMatch[2] ?? "", req, body, res);

    this.sendError(res, "NOT_FOUND", `no fake route for ${method} ${path}`);
  }

  private handleChallenge(body: Record<string, unknown>, res: ServerResponse): void {
    const purpose = body.purpose as "root_login" | "admin_login";
    const fingerprint = String(body.fingerprint ?? "");
    const challengeId = randomUUID();
    const nonce = toBase64Url(randomBytes(32));
    const expiresAt = expiresAtString(new Date(Date.now() + 60_000));
    this.challenges.set(challengeId, { purpose, nonce, expiresAt, subjectFingerprint: fingerprint, consumed: false });
    this.sendOk(res, { challengeId, nonce, expiresAt, audience: this.audience });
  }

  private handleSession(body: Record<string, unknown>, res: ServerResponse): void {
    const challengeId = String(body.challengeId ?? "");
    const challenge = this.challenges.get(challengeId);
    if (!challenge || challenge.consumed || new Date(challenge.expiresAt).getTime() < Date.now()) {
      this.appendAudit("anonymous", "auth.session", challengeId, "failed", { reason: "challenge_invalid" });
      return this.sendError(res, "AUTH_FAILED", "Authentication failed");
    }
    challenge.consumed = true;

    const publicKey = String(body.publicKey ?? "");
    const signature = String(body.signature ?? "");
    const fingerprint = fingerprintOf(fromBase64Url(publicKey));
    if (fingerprint !== challenge.subjectFingerprint) {
      this.appendAudit("anonymous", "auth.session", challengeId, "failed", { reason: "fingerprint_mismatch" });
      return this.sendError(res, "AUTH_FAILED", "Authentication failed");
    }

    const message = buildCanonicalMessage([challenge.purpose, this.audience, challengeId, challenge.nonce, challenge.expiresAt]);
    if (!verifySignature(publicKey, message, signature)) {
      this.appendAudit("anonymous", "auth.session", challengeId, "failed", { reason: "bad_signature" });
      return this.sendError(res, "AUTH_FAILED", "Authentication failed");
    }

    if (challenge.purpose === "root_login") {
      if (!this.rootPublicKeys.has(publicKey)) {
        this.appendAudit("anonymous", "auth.session", challengeId, "failed", { reason: "unknown_root_key" });
        return this.sendError(res, "AUTH_FAILED", "Authentication failed");
      }
      const principal = `root:${fingerprint}`;
      const sid = randomUUID();
      this.sessions.set(sid, { sid, principal, permissions: ROOT_PERMISSIONS, authTime: Date.now(), revoked: false });
      this.appendAudit(principal, "auth.session", principal, "ok", { amr: ["ed25519", "root"] });
      return this.sendOk(res, {
        accessToken: sid,
        expiresAt: expiresAtString(new Date(Date.now() + 15 * 60_000)),
        sid,
        principal,
        permissions: ROOT_PERMISSIONS,
      });
    }

    const admin = [...this.admins.values()].find((a) => a.keys.some((k) => k.fingerprint === fingerprint && !k.revokedAt));
    if (!admin || admin.status !== "active") {
      this.appendAudit("anonymous", "auth.session", fingerprint, "failed", { reason: "unknown_or_inactive_admin" });
      return this.sendError(res, "AUTH_FAILED", "Authentication failed");
    }

    // AGENTS.md §auth: lockout is reachable only after a valid signature (no oracle), and counts
    // only second-factor failures. Checked before TOTP verification, same as the real manager.
    const now = Date.now();
    if (admin.lockedUntil !== undefined && admin.lockedUntil > now) {
      this.appendAudit(`admin:${admin.id}`, "auth.session", admin.id, "failed", { reason: "locked" });
      return this.sendError(res, "ADMIN_LOCKED", "This administrator is temporarily locked after repeated failed sign-ins.", {
        retryAfterSeconds: Math.max(1, Math.ceil((admin.lockedUntil - now) / 1000)),
      });
    }

    const totpCode = String(body.totp ?? "");
    if (!admin.totpSecret || !this.verifyTotp(admin, totpCode)) {
      admin.failedSecondFactorAttempts += 1;
      if (admin.failedSecondFactorAttempts >= SECOND_FACTOR_LOCK_THRESHOLD) {
        admin.lockedUntil = now + this.lockDurationMs;
        admin.failedSecondFactorAttempts = 0;
        this.appendAudit(`admin:${admin.id}`, "auth.lock", admin.id, "ok", { reason: "second_factor_failures" });
      }
      this.appendAudit(`admin:${admin.id}`, "auth.session", admin.id, "failed", { reason: "bad_totp" });
      return this.sendError(res, "AUTH_FAILED", "Authentication failed");
    }
    admin.failedSecondFactorAttempts = 0;
    admin.lockedUntil = undefined;

    const principal = `admin:${admin.id}`;
    const permissions = this.permissionsForAdmin(admin);
    const sid = randomUUID();
    this.sessions.set(sid, { sid, principal, permissions, authTime: Date.now(), revoked: false });
    this.appendAudit(principal, "auth.session", principal, "ok", { amr: ["ed25519", "totp"] });
    this.sendOk(res, {
      accessToken: sid,
      expiresAt: expiresAtString(new Date(Date.now() + 15 * 60_000)),
      sid,
      principal,
      permissions,
      admin: { id: admin.id, username: admin.username, roles: admin.roleIds },
    });
  }

  private verifyTotp(admin: FakeAdmin, code: string): boolean {
    if (!admin.totpSecret || code.length === 0) return false;
    const now = Math.floor(Date.now() / 1000);
    for (const delta of [0, -1, 1]) {
      const step = Math.floor(now / 30) + delta;
      if (admin.totpLastStep !== undefined && step <= admin.totpLastStep) continue;
      if (totp(admin.totpSecret, (now + delta * 30), 30, 6) === code) {
        admin.totpLastStep = step;
        return true;
      }
    }
    return false;
  }

  private permissionsForAdmin(admin: FakeAdmin): string[] {
    const perms = new Set<string>();
    for (const roleId of admin.roleIds) {
      const role = this.roles.get(roleId);
      if (role) for (const p of role.permissions) perms.add(p);
    }
    return [...perms];
  }

  private requireAuth(req: IncomingMessage, res: ServerResponse): FakeSession | null {
    const session = this.sessionFor(req);
    if (!session) {
      this.sendError(res, "AUTH_FAILED", "Authentication required");
      return null;
    }
    return session;
  }

  private requirePermission(req: IncomingMessage, res: ServerResponse, permission: string): FakeSession | null {
    const session = this.requireAuth(req, res);
    if (!session) return null;
    if (!session.permissions.includes(permission)) {
      this.sendError(res, "PERMISSION_DENIED", `missing permission ${permission}`);
      return null;
    }
    return session;
  }

  private handleRevokeSelf(req: IncomingMessage, res: ServerResponse): void {
    const session = this.requireAuth(req, res);
    if (!session) return;
    session.revoked = true;
    this.sendOk(res, {});
  }

  private handleMe(req: IncomingMessage, res: ServerResponse): void {
    const session = this.requireAuth(req, res);
    if (!session) return;
    const isRoot = session.principal.startsWith("root:");
    this.sendOk(res, {
      principal: session.principal,
      root: isRoot ? { fingerprint: session.principal.slice("root:".length) } : undefined,
      admin: isRoot ? undefined : this.adminDtoByPrincipal(session.principal),
      permissions: session.permissions,
      session: { sid: session.sid, authTime: new Date(session.authTime).toISOString(), expiresAt: expiresAtString(new Date(session.authTime + 15 * 60_000)) },
    });
  }

  private adminDtoByPrincipal(principal: string): unknown {
    const id = principal.slice("admin:".length);
    const admin = this.admins.get(id);
    return admin && this.adminDto(admin);
  }

  private adminDto(admin: FakeAdmin) {
    return {
      id: admin.id,
      username: admin.username,
      displayName: admin.displayName,
      status: admin.status,
      roles: admin.roleIds.map((id) => ({ id, name: this.roles.get(id)?.name ?? id })),
      permissions: this.permissionsForAdmin(admin),
      createdBy: admin.createdBy,
      createdAt: admin.createdAt,
      updatedAt: admin.updatedAt,
      keys: admin.keys,
      mfaEnrolled: Boolean(admin.totpSecret),
    };
  }

  private handleCreateAdmin(req: IncomingMessage, body: Record<string, unknown>, res: ServerResponse): void {
    const session = this.requirePermission(req, res, "admins.manage");
    if (!session) return;
    const id = randomUUID();
    const now = new Date().toISOString();
    const admin: FakeAdmin = {
      id,
      username: String(body.username),
      displayName: String(body.displayName ?? body.username),
      status: "pending_enrollment",
      roleIds: Array.isArray(body.roleIds) ? (body.roleIds as string[]) : [],
      keys: [],
      failedSecondFactorAttempts: 0,
      createdBy: session.principal,
      createdAt: now,
      updatedAt: now,
    };
    this.admins.set(id, admin);
    const token = randomUUID().replace(/-/g, "");
    this.enrollmentTokens.set(token, { adminId: id, consumed: false });
    this.appendAudit(session.principal, "admins.create", id, "ok", { username: admin.username });
    this.sendOk(res, {
      admin: this.adminDto(admin),
      enrollmentToken: token,
      enrollmentExpiresAt: expiresAtString(new Date(Date.now() + 4 * 60 * 60_000)),
    });
  }

  private handleListAdmins(req: IncomingMessage, res: ServerResponse): void {
    const session = this.requirePermission(req, res, "admins.read");
    if (!session) return;
    this.sendOk(res, [...this.admins.values()].map((a) => this.adminDto(a)));
  }

  private handleEnrollStart(body: Record<string, unknown>, res: ServerResponse): void {
    const token = String(body.enrollmentToken ?? "");
    const record = this.enrollmentTokens.get(token);
    if (!record || record.consumed) {
      return this.sendError(res, "ENROLLMENT_INVALID", "Enrollment token invalid, expired, or already used");
    }
    const admin = this.admins.get(record.adminId)!;
    if (admin.keys.length > 0) {
      return this.sendError(res, "ENROLLMENT_ALREADY_STARTED", "This enrollment already bound a key");
    }
    const publicKey = String(body.publicKey ?? "");
    const fingerprint = fingerprintOf(fromBase64Url(publicKey));
    const secret = randomBytes(20);
    const enrollmentId = randomUUID();
    this.enrollments.set(enrollmentId, {
      adminId: admin.id,
      publicKey,
      fingerprint,
      label: String(body.label ?? "cli"),
      totpSecretPending: secret,
      consumed: false,
    });
    const secretBase32 = base32Encode(secret);
    // Lane A1 contract deviation #1: also echoes the server's real audience and the fingerprint
    // it derived — the CLI treats both as an H1-style check before proceeding.
    this.sendOk(res, {
      enrollmentId,
      otpauthUri: buildOtpauthUri("fake.node", admin.username, secretBase32),
      totpSecretBase32: secretBase32,
      audience: this.audience,
      fingerprint: this.forceEnrollFingerprint ?? fingerprint,
    });
  }

  private handleEnrollComplete(body: Record<string, unknown>, res: ServerResponse): void {
    const enrollmentId = String(body.enrollmentId ?? "");
    const enrollment = this.enrollments.get(enrollmentId);
    if (!enrollment || enrollment.consumed) {
      return this.sendError(res, "ENROLLMENT_INVALID", "Enrollment invalid, expired, or already used");
    }
    const totpCode = String(body.totp ?? "");
    const now = Math.floor(Date.now() / 1000);
    const codeOk = [0, -1, 1].some((delta) => totp(enrollment.totpSecretPending, now + delta * 30, 30, 6) === totpCode);
    if (!codeOk) {
      return this.sendError(res, "ENROLLMENT_INVALID", "Enrollment invalid, expired, or already used");
    }
    const message = enrollMessage(this.audience, enrollmentId);
    if (!verifySignature(enrollment.publicKey, message, String(body.signature ?? ""))) {
      return this.sendError(res, "ENROLLMENT_INVALID", "Enrollment invalid, expired, or already used");
    }
    enrollment.consumed = true;
    const admin = this.admins.get(enrollment.adminId)!;
    admin.status = "pending_activation";
    admin.keys.push({ publicKey: enrollment.publicKey, fingerprint: enrollment.fingerprint, label: enrollment.label });
    admin.totpSecret = enrollment.totpSecretPending;
    admin.updatedAt = new Date().toISOString();
    for (const [tok, rec] of this.enrollmentTokens) {
      if (rec.adminId === admin.id) rec.consumed = true;
      void tok;
    }
    this.appendAudit(`admin:${admin.id}`, "enroll.complete", admin.id, "ok", {});
    // NodeAdminEnrollCompleteView shape (lane A1): { adminId, status, fingerprint } — not { admin }.
    this.sendOk(res, { adminId: admin.id, status: admin.status, fingerprint: enrollment.fingerprint });
  }

  private handleAdminSubroute(
    method: string,
    id: string,
    sub: string,
    req: IncomingMessage,
    body: Record<string, unknown>,
    res: ServerResponse,
  ): void {
    const admin = this.admins.get(id);
    if (!admin) return this.sendError(res, "NOT_FOUND", "unknown admin");

    if (method === "GET" && sub === "") {
      const session = this.requirePermission(req, res, "admins.read");
      if (!session) return;
      return this.sendOk(res, this.adminDto(admin));
    }
    if (method === "POST" && sub === "/activate") {
      const session = this.requirePermission(req, res, "admins.manage");
      if (!session) return;
      if (admin.status !== "pending_activation") {
        return this.sendError(res, "VALIDATION_FAILED", "admin is not pending_activation");
      }
      const fingerprint = String(body.fingerprint ?? "");
      if (!admin.keys.some((k) => k.fingerprint === fingerprint)) {
        return this.sendError(res, "FINGERPRINT_MISMATCH", "fingerprint does not match the enrolled key");
      }
      admin.status = "active";
      admin.updatedAt = new Date().toISOString();
      this.appendAudit(session.principal, "admins.activate", id, "ok", { fingerprint });
      return this.sendOk(res, { admin: this.adminDto(admin) });
    }
    if (method === "POST" && (sub === "/suspend" || sub === "/reactivate" || sub === "/revoke")) {
      const session = this.requirePermission(req, res, "admins.manage");
      if (!session) return;
      admin.status = sub === "/suspend" ? "suspended" : sub === "/revoke" ? "revoked" : "active";
      admin.updatedAt = new Date().toISOString();
      this.appendAudit(session.principal, `admins.${sub.slice(1)}`, id, "ok", {});
      return this.sendOk(res, { admin: this.adminDto(admin) });
    }
    if (method === "POST" && sub === "/revoke-sessions") {
      const session = this.requirePermission(req, res, "admins.manage");
      if (!session) return;
      for (const s of this.sessions.values()) {
        if (s.principal === `admin:${id}`) s.revoked = true;
      }
      this.appendAudit(session.principal, "admins.revoke-sessions", id, "ok", {});
      return this.sendOk(res, {});
    }
    if (method === "PUT" && sub === "/roles") {
      const session = this.requirePermission(req, res, "roles.manage");
      if (!session) return;
      admin.roleIds = Array.isArray(body.roleIds) ? (body.roleIds as string[]) : [];
      admin.updatedAt = new Date().toISOString();
      this.appendAudit(session.principal, "admins.roles", id, "ok", { roleIds: admin.roleIds });
      return this.sendOk(res, { admin: this.adminDto(admin) });
    }
    if (method === "POST" && sub === "/keys") {
      const session = this.requireAuth(req, res);
      if (!session) return;
      if (!this.isRecent(session)) {
        return this.sendError(res, "STEP_UP_REQUIRED", "Step-up re-authentication required");
      }
      const publicKey = String(body.publicKey ?? "");
      const fingerprint = fingerprintOf(fromBase64Url(publicKey));
      const message = addKeyMessage(this.audience, id, fingerprint);
      if (!verifySignature(publicKey, message, String(body.signature ?? ""))) {
        return this.sendError(res, "AUTH_FAILED", "Authentication failed");
      }
      admin.keys.push({ publicKey, fingerprint, label: String(body.label ?? "cli") });
      admin.updatedAt = new Date().toISOString();
      this.appendAudit(session.principal, "admins.keys.add", id, "ok", { fingerprint });
      return this.sendOk(res, { admin: this.adminDto(admin) });
    }
    if (method === "DELETE" && sub.startsWith("/keys/")) {
      const session = this.requirePermission(req, res, "admins.manage");
      if (!session) return;
      const fp = decodeURIComponent(sub.slice("/keys/".length));
      const key = admin.keys.find((k) => k.fingerprint === fp);
      if (key) key.revokedAt = new Date().toISOString();
      this.appendAudit(session.principal, "admins.keys.revoke", id, "ok", { fingerprint: fp });
      return this.sendOk(res, {});
    }
    if (method === "POST" && sub === "/mfa/reset") {
      const session = this.requirePermission(req, res, "admins.manage");
      if (!session) return;
      // Lane A1 contract deviations #2/#10: revokes every key + session, returns the admin to
      // pending_enrollment, and only hands a fresh enrollment token to a ROOT caller.
      for (const key of admin.keys) key.revokedAt ??= new Date().toISOString();
      for (const s of this.sessions.values()) if (s.principal === `admin:${id}`) s.revoked = true;
      for (const rec of this.enrollmentTokens.values()) if (rec.adminId === id) rec.consumed = true;
      for (const enr of this.enrollments.values()) if (enr.adminId === id) enr.consumed = true;
      admin.status = "pending_enrollment";
      admin.totpSecret = undefined;
      admin.totpLastStep = undefined;
      admin.updatedAt = new Date().toISOString();

      const isRoot = session.principal.startsWith("root:");
      let enrollmentToken: string | null = null;
      let enrollmentExpiresAt: string | null = null;
      if (isRoot) {
        enrollmentToken = randomUUID().replace(/-/g, "");
        enrollmentExpiresAt = expiresAtString(new Date(Date.now() + 4 * 60 * 60_000));
        this.enrollmentTokens.set(enrollmentToken, { adminId: id, consumed: false });
      }
      this.appendAudit(session.principal, "admins.mfa.reset", id, "ok", { issuedToken: isRoot });
      return this.sendOk(res, { admin: this.adminDto(admin), enrollmentToken, enrollmentExpiresAt });
    }
    this.sendError(res, "NOT_FOUND", `no fake route for ${method} /admins/${id}${sub}`);
  }

  private handleProtectedExample(req: IncomingMessage, res: ServerResponse): void {
    const session = this.requireAuth(req, res);
    if (!session) return;
    if (!this.isRecent(session)) {
      return this.sendError(res, "STEP_UP_REQUIRED", "Step-up re-authentication required");
    }
    this.sendOk(res, { ok: true, principal: session.principal });
  }

  private handleAuditList(req: IncomingMessage, url: URL, res: ServerResponse): void {
    const session = this.requirePermission(req, res, "audit.read");
    if (!session) return;
    const after = Number(url.searchParams.get("afterSeq") ?? "0");
    const limit = Math.min(Number(url.searchParams.get("limit") ?? "50"), 500);
    const rows = this.auditRows.filter((r) => r.seq > after).slice(0, limit);
    const head = this.auditRows.at(-1);
    this.sendOk(res, { rows, head: head ? { seq: head.seq, hash: head.hash } : { seq: 0, hash: GENESIS_PREV_HASH } });
  }

  private handleAuditVerifyEndpoint(req: IncomingMessage, res: ServerResponse): void {
    const session = this.requirePermission(req, res, "audit.read");
    if (!session) return;
    let prevHash = GENESIS_PREV_HASH;
    for (const row of this.auditRows) {
      const recomputed = computeRowHash({ ...row, prevHash });
      if (recomputed !== row.hash) {
        return this.sendOk(res, { ok: false, verifiedThroughSeq: row.seq - 1, headHash: prevHash, firstBrokenSeq: row.seq });
      }
      prevHash = row.hash;
    }
    this.sendOk(res, { ok: true, verifiedThroughSeq: this.auditRows.at(-1)?.seq ?? 0, headHash: prevHash });
  }

  /** `?principal=` (full `admin:<id>`/`root:<fp>` string), not a bare admin id — see NodeAdminManager.ListSessionsAsync. */
  private handleSessionsList(req: IncomingMessage, url: URL, res: ServerResponse): void {
    const session = this.requireAuth(req, res);
    if (!session) return;
    const requested = url.searchParams.get("principal");
    const subject = requested && requested.trim().length > 0 ? requested.trim() : session.principal;
    if (subject !== session.principal && !session.permissions.includes("admins.read")) {
      return this.sendError(res, "PERMISSION_DENIED", "Listing another principal's sessions requires admins.read.");
    }
    this.sendOk(
      res,
      [...this.sessions.values()]
        .filter((s) => s.principal === subject)
        .map((s) => ({
          sid: s.sid,
          principal: s.principal,
          authTime: new Date(s.authTime).toISOString(),
          amr: [],
          expiresAt: expiresAtString(new Date(s.authTime + 15 * 60_000)),
          revokedAt: s.revoked ? new Date().toISOString() : undefined,
          ip: "127.0.0.1",
          current: s.sid === session.sid,
        })),
    );
  }

  private handleSessionRevoke(req: IncomingMessage, sid: string, res: ServerResponse): void {
    const session = this.requireAuth(req, res);
    if (!session) return;
    const target = this.sessions.get(sid);
    if (target) target.revoked = true;
    this.sendOk(res, {});
  }

  private sendOk(res: ServerResponse, result: unknown, status = 200): void {
    res.statusCode = status;
    res.setHeader("content-type", "application/json");
    res.end(JSON.stringify({ result, isError: false, message: "Success" }));
  }

  private sendError(res: ServerResponse, code: string, message: string, options: { retryAfterSeconds?: number } = {}): void {
    res.statusCode = statusForCode(code);
    if (code === "STEP_UP_REQUIRED") res.setHeader(STEP_UP_HEADER, STEP_UP_HEADER_VALUE);
    if (options.retryAfterSeconds !== undefined) res.setHeader("Retry-After", String(options.retryAfterSeconds));
    res.setHeader("content-type", "application/json");
    res.end(JSON.stringify({ result: null, isError: true, message, code, retryAfterSeconds: options.retryAfterSeconds }));
  }
}

/** .NET "yyyy-MM-ddTHH:mm:ss.fffffffK" UTC format (plan.md §8 H2). */
export function expiresAtString(date: Date): string {
  const iso = date.toISOString(); // 2026-10-18T17:00:00.000Z
  const [base, msZ] = iso.split(".");
  const ms = msZ!.replace("Z", "");
  const sevenDigitFraction = ms.padEnd(7, "0");
  return `${base}.${sevenDigitFraction}+00:00`;
}
