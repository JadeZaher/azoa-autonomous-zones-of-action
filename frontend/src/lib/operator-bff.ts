import { NextRequest, NextResponse } from "next/server";
import { resolveInternalApiUrl } from "@/lib/runtime-config";
import {
    OPERATOR_REQUEST_HEADER,
    type AzoaEnvelope,
    type OperatorSessionResponse,
    operatorErrorMessage,
} from "@/lib/operator-contracts";

export const OPERATOR_COOKIE_NAME = "azoa_operator_session";
export const OPERATOR_UPSTREAM_TIMEOUT_MS = 8_000;

const NO_STORE_HEADERS = { "Cache-Control": "no-store" };
const MAX_REQUEST_BYTES = 32_768;
const MAX_RESPONSE_BYTES = 262_144;
const MAX_RETRY_AFTER_SECONDS = 3_600;

interface OperatorClaims {
    exp?: number;
    sub?: string;
    jti?: string;
    auth_time?: number;
    operator_revision?: number;
    scope?: string | string[];
    token_use?: string;
    unique_name?: string;
    name?: string;
}

function decodeClaims(token: string): OperatorClaims | null {
    try {
        const parts = token.split(".");
        if (parts.length !== 3) return null;
        return JSON.parse(
            Buffer.from(parts[1], "base64url").toString("utf8"),
        ) as OperatorClaims;
    } catch {
        return null;
    }
}

function hasOperatorClaims(token: string): boolean {
    const claims = decodeClaims(token);
    const nowSeconds = Math.floor(Date.now() / 1000);
    if (
        !claims ||
        claims.token_use !== "node_operator" ||
        typeof claims.sub !== "string" ||
        !claims.sub ||
        typeof claims.jti !== "string" ||
        !claims.jti ||
        !Number.isFinite(claims.auth_time) ||
        claims.auth_time! > nowSeconds + 60 ||
        !Number.isFinite(claims.operator_revision) ||
        claims.operator_revision! < 1 ||
        !Number.isFinite(claims.exp) ||
        claims.exp! <= nowSeconds ||
        claims.exp! > nowSeconds + 30 * 60 + 60
    )
        return false;
    const scopes = Array.isArray(claims.scope)
        ? claims.scope
        : typeof claims.scope === "string"
          ? claims.scope.split(/[ ,]+/)
          : [];
    return scopes.includes("operator:admin") && scopes.includes("node:govern");
}

export function operatorCookieOptions(expiresAt?: Date) {
    return {
        httpOnly: true,
        secure: process.env.NODE_ENV === "production",
        sameSite: "strict" as const,
        path: "/api/operator",
        expires: expiresAt,
    };
}

export function clearOperatorCookie(response: NextResponse): void {
    response.cookies.set(OPERATOR_COOKIE_NAME, "", {
        ...operatorCookieOptions(new Date(0)),
        maxAge: 0,
    });
}

/** Normalizes `PUBLIC_ORIGIN` to an exact `scheme://host[:port]` origin, or null when it is not one. */
function configuredPublicOrigin(value: string | undefined): string | null {
    if (!value) return null;
    try {
        const url = new URL(value.trim());
        if (
            !["http:", "https:"].includes(url.protocol) ||
            url.username ||
            url.password ||
            url.search ||
            url.hash ||
            url.pathname !== "/"
        )
            return null;
        return url.origin;
    } catch {
        return null;
    }
}

/** A single-valued forwarded header; a comma-joined chain is ambiguous and yields `undefined`. */
function singleHeader(request: NextRequest, name: string): string | null | undefined {
    const value = request.headers.get(name);
    if (value === null) return null;
    const trimmed = value.trim();
    return !trimmed || trimmed.includes(",") ? undefined : trimmed;
}

/**
 * The public origin the browser is talking to, or null when it cannot be determined unambiguously.
 * See `app/operator/AGENTS.md` §Same-origin check behind a proxy.
 */
export function expectedPublicOrigin(request: NextRequest): string | null {
    if (process.env.PUBLIC_ORIGIN !== undefined && process.env.PUBLIC_ORIGIN !== "")
        return configuredPublicOrigin(process.env.PUBLIC_ORIGIN);
    const internal = new URL(request.url);
    const forwardedHost = singleHeader(request, "x-forwarded-host");
    const forwardedProto = singleHeader(request, "x-forwarded-proto");
    const host = singleHeader(request, "host");
    if (forwardedHost === undefined || forwardedProto === undefined || host === undefined)
        return null;
    const publicHost = forwardedHost ?? host;
    if (!publicHost) return internal.origin;
    const proto = (forwardedProto ?? internal.protocol.replace(/:$/, "")).toLowerCase();
    if (proto !== "http" && proto !== "https") return null;
    try {
        const url = new URL(`${proto}://${publicHost}`);
        const defaultPort = proto === "https" ? 443 : 80;
        const lowered = publicHost.toLowerCase();
        if (
            (url.host !== lowered && `${url.host}:${defaultPort}` !== lowered) ||
            url.username ||
            url.password
        )
            return null;
        return url.origin;
    } catch {
        return null;
    }
}

export function requireSameOriginMutation(
    request: NextRequest,
): NextResponse | null {
    const origin = request.headers.get("origin");
    const expectedOrigin = expectedPublicOrigin(request);
    const intent = request.headers.get(OPERATOR_REQUEST_HEADER);
    if (!expectedOrigin || origin !== expectedOrigin || intent !== "1") {
        return NextResponse.json(
            { message: "The operator request could not be verified." },
            { status: 403, headers: NO_STORE_HEADERS },
        );
    }
    return null;
}

export async function readSmallJson(
    request: NextRequest,
): Promise<Record<string, unknown> | null> {
    const declaredLength = Number(request.headers.get("content-length") ?? "0");
    if (Number.isFinite(declaredLength) && declaredLength > MAX_REQUEST_BYTES)
        return null;
    try {
        const body = await request.json();
        if (!body || Array.isArray(body) || typeof body !== "object")
            return null;
        if (JSON.stringify(body).length > MAX_REQUEST_BYTES) return null;
        return body as Record<string, unknown>;
    } catch {
        return null;
    }
}

async function readUpstreamEnvelope<T>(
    response: Response,
): Promise<AzoaEnvelope<T> | null> {
    if (
        !response.headers
            .get("content-type")
            ?.toLowerCase()
            .includes("application/json")
    )
        return null;
    const declaredLength = Number(
        response.headers.get("content-length") ?? "0",
    );
    if (Number.isFinite(declaredLength) && declaredLength > MAX_RESPONSE_BYTES)
        return null;
    try {
        if (!response.body) return null;
        const reader = response.body.getReader();
        const chunks: Uint8Array[] = [];
        let total = 0;
        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            total += value.byteLength;
            if (total > MAX_RESPONSE_BYTES) {
                await reader.cancel();
                return null;
            }
            chunks.push(value);
        }
        const bytes = new Uint8Array(total);
        let offset = 0;
        for (const chunk of chunks) {
            bytes.set(chunk, offset);
            offset += chunk.byteLength;
        }
        return JSON.parse(new TextDecoder().decode(bytes)) as AzoaEnvelope<T>;
    } catch {
        return null;
    }
}

function boundedRetryAfterSeconds(value: string | null): string | null {
    if (!value || !/^\d+$/.test(value)) return null;
    const seconds = Number(value);
    if (!Number.isSafeInteger(seconds) || seconds < 1) return null;
    return String(Math.min(seconds, MAX_RETRY_AFTER_SECONDS));
}

export async function createOperatorSession(
    username: string,
    password: string,
): Promise<NextResponse> {
    try {
        const response = await fetch(
            new URL("/api/operator/session", resolveInternalApiUrl()),
            {
                method: "POST",
                cache: "no-store",
                redirect: "error",
                headers: {
                    Accept: "application/json",
                    "Content-Type": "application/json",
                },
                body: JSON.stringify({ username, password }),
                signal: AbortSignal.timeout(OPERATOR_UPSTREAM_TIMEOUT_MS),
            },
        );
        const envelope =
            await readUpstreamEnvelope<OperatorSessionResponse>(response);
        const session = envelope?.result;
        if (
            !response.ok ||
            envelope?.isError ||
            !session ||
            typeof session.accessToken !== "string" ||
            session.accessToken.length > 4_096 ||
            !hasOperatorClaims(session.accessToken)
        ) {
            const status =
                response.status === 429
                    ? 429
                    : response.status >= 500
                      ? 503
                      : 401;
            const result = NextResponse.json(
                {
                    message:
                        status === 429
                            ? operatorErrorMessage(429)
                            : status === 503
                              ? "The node operator service is unavailable. Check node readiness and try again."
                              : "The username or password was not accepted.",
                },
                { status, headers: NO_STORE_HEADERS },
            );
            const retryAfter = boundedRetryAfterSeconds(
                response.headers.get("retry-after"),
            );
            if (status === 429 && retryAfter)
                result.headers.set("Retry-After", retryAfter);
            return result;
        }

        const expiresAt = new Date(session.expiresAt);
        const tokenClaims = decodeClaims(session.accessToken);
        const tokenExpiresAt = tokenClaims?.exp
            ? new Date(tokenClaims.exp * 1000)
            : null;
        if (
            !Number.isFinite(expiresAt.getTime()) ||
            expiresAt <= new Date() ||
            !tokenExpiresAt ||
            Math.abs(expiresAt.getTime() - tokenExpiresAt.getTime()) > 1_000
        ) {
            return NextResponse.json(
                { message: "The operator session could not be established." },
                { status: 401, headers: NO_STORE_HEADERS },
            );
        }

        const result = NextResponse.json(
            {
                authenticated: true,
                username: session.username,
                expiresAt: expiresAt.toISOString(),
            },
            { headers: NO_STORE_HEADERS },
        );
        result.cookies.set(
            OPERATOR_COOKIE_NAME,
            session.accessToken,
            operatorCookieOptions(expiresAt),
        );
        return result;
    } catch {
        return NextResponse.json(
            {
                message:
                    "The node operator service is unavailable. Check node readiness and try again.",
            },
            { status: 503, headers: NO_STORE_HEADERS },
        );
    }
}

export async function operatorUpstreamFetch(
    path: string,
    token: string,
    init?: { method?: "GET" | "PUT" | "POST"; body?: Record<string, unknown> },
): Promise<Response> {
    const platformGenesis = path === "platform-token" || path === "platform-token/genesis"
        || /^platform-token\/creation\/[0-9a-f]{32}(?:\/(?:quote|accept-quote|continue|replace|refresh))?$/i.test(path);
    // Round-4 console routes arrive as an exact upstream `/api/...` path from `operator-routes.ts`.
    const upstreamPath = path.startsWith("/api/") ? path : `/api/${platformGenesis ? "admin" : "operator"}/${path}`;
    return fetch(new URL(upstreamPath, resolveInternalApiUrl()), {
        method: init?.method ?? "GET",
        cache: "no-store",
        redirect: "error",
        headers: {
            Accept: "application/json",
            Authorization: `Bearer ${token}`,
            ...(init?.body ? { "Content-Type": "application/json" } : {}),
        },
        body: init?.body ? JSON.stringify(init.body) : undefined,
        signal: AbortSignal.timeout(OPERATOR_UPSTREAM_TIMEOUT_MS),
    });
}

/** Upstream refusal codes the console may see, each with fixed copy: upstream text itself is never forwarded. */
const FORWARDED_UPSTREAM_CODES: Record<string, string> = {
    ETHEREUM_RECOVERY_UNAVAILABLE:
        "Ethereum is not enabled on this node, so there are no preparations to recover.",
};

export async function sanitizedOperatorResponse(
    response: Response,
    options?: { bare?: boolean },
): Promise<NextResponse> {
    if (!response.ok) {
        const upstreamCode = (await readUpstreamEnvelope<unknown>(response.clone()))?.code;
        if (typeof upstreamCode === "string" && FORWARDED_UPSTREAM_CODES[upstreamCode]) {
            return NextResponse.json(
                { code: upstreamCode, message: FORWARDED_UPSTREAM_CODES[upstreamCode] },
                { status: response.status, headers: NO_STORE_HEADERS },
            );
        }
        const authRequirement = response.headers.get("x-azoa-auth-requirement");
        const recentLoginRequired =
            response.status === 403 &&
            authRequirement === "recent-operator-login";
        const result = NextResponse.json(
            recentLoginRequired
                ? {
                      code: "RECENT_OPERATOR_LOGIN_REQUIRED",
                      message:
                          "Sign in again to confirm this sensitive operator action.",
                  }
                : { message: operatorErrorMessage(response.status) },
            { status: response.status, headers: NO_STORE_HEADERS },
        );
        if (response.status === 401 || recentLoginRequired)
            clearOperatorCookie(result);
        const retryAfter = boundedRetryAfterSeconds(
            response.headers.get("retry-after"),
        );
        if (retryAfter && response.status === 429)
            result.headers.set("Retry-After", retryAfter);
        return result;
    }

    const parsed = await readUpstreamEnvelope<unknown>(response);
    // A bare upstream body (routes marked `bare` in operator-routes.ts) is wrapped so the console sees one envelope shape.
    const envelope = options?.bare && parsed !== null ? { isError: false, result: parsed as unknown } : parsed;
    // Anything that is not a real `AZOAResult` (e.g. a bare body on a route not marked `bare`) is refused, never read as empty.
    if (!isAzoaEnvelope(envelope) || envelope.isError) {
        return NextResponse.json(
            { message: "The node returned an invalid operator response." },
            { status: 502, headers: NO_STORE_HEADERS },
        );
    }
    return NextResponse.json(
        { result: envelope.result ?? null },
        { headers: NO_STORE_HEADERS },
    );
}

/** True for a serialized `AZOAResult`: an object whose `isError` is a boolean (ASP.NET always writes it). */
function isAzoaEnvelope(value: unknown): value is AzoaEnvelope<unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value)
        && typeof (value as { isError?: unknown }).isError === "boolean";
}
