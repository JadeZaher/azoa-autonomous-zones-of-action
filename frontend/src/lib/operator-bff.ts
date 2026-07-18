import { NextRequest, NextResponse } from "next/server";
import { resolveServerApiUrl } from "@/lib/runtime-config";
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

export function requireSameOriginMutation(
    request: NextRequest,
): NextResponse | null {
    const origin = request.headers.get("origin");
    const expectedOrigin = new URL(request.url).origin;
    const intent = request.headers.get(OPERATOR_REQUEST_HEADER);
    if (origin !== expectedOrigin || intent !== "1") {
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
            new URL("/api/operator/session", resolveServerApiUrl()),
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
    return fetch(new URL(`/api/operator/${path}`, resolveServerApiUrl()), {
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

export async function sanitizedOperatorResponse(
    response: Response,
): Promise<NextResponse> {
    if (!response.ok) {
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

    const envelope = await readUpstreamEnvelope<unknown>(response);
    if (!envelope || envelope.isError) {
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
