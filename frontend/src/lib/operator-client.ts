"use client";

import {
    OPERATOR_REQUEST_HEADER,
    type OperatorResponse,
    operatorErrorMessage,
} from "@/lib/operator-contracts";

export class OperatorRequestError extends Error {
    constructor(
        message: string,
        readonly status: number,
        readonly code?: string,
    ) {
        super(message);
        this.name = "OperatorRequestError";
    }
}

export async function operatorRequest<T>(
    path: string,
    init?: { method?: "GET" | "PUT" | "POST" | "DELETE"; body?: unknown },
): Promise<T> {
    const method = init?.method ?? "GET";
    const response = await fetch(`/api/operator/${path}`, {
        method,
        cache: "no-store",
        credentials: "same-origin",
        headers: {
            Accept: "application/json",
            ...(method === "GET" ? {} : { [OPERATOR_REQUEST_HEADER]: "1" }),
            ...(init?.body ? { "Content-Type": "application/json" } : {}),
        },
        body: init?.body ? JSON.stringify(init.body) : undefined,
    });
    const payload = (await response.json().catch(() => null)) as
        | OperatorResponse<T>
        | { message?: string; code?: string }
        | null;
    if (!response.ok) {
        const code = payload && "code" in payload ? payload.code : undefined;
        if (
            code === "RECENT_OPERATOR_LOGIN_REQUIRED" &&
            typeof window !== "undefined"
        ) {
            window.dispatchEvent(new Event("azoa:operator-step-up-required"));
        }
        if (response.status === 401 && typeof window !== "undefined") {
            window.dispatchEvent(new Event("azoa:operator-session-expired"));
        }
        const message =
            payload && "message" in payload && payload.message
                ? payload.message
                : operatorErrorMessage(response.status);
        throw new OperatorRequestError(message, response.status, code);
    }
    if (!payload || !("result" in payload)) {
        throw new OperatorRequestError(
            "The node returned an invalid operator response.",
            502,
        );
    }
    return payload.result;
}
