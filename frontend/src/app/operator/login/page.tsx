"use client";

import { FormEvent, useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import {
    ArrowLeft,
    Loader2,
    LockKeyhole,
    ShieldCheck,
    UserRound,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { OPERATOR_REQUEST_HEADER } from "@/lib/operator-contracts";

export default function OperatorLoginPage() {
    const router = useRouter();
    const [username, setUsername] = useState("");
    const [password, setPassword] = useState("");
    const [submitting, setSubmitting] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [returnTo, setReturnTo] = useState("/operator");
    const [stepUp, setStepUp] = useState(false);

    useEffect(() => {
        const params = new URLSearchParams(window.location.search);
        const requested = params.get("returnTo");
        if (
            requested?.startsWith("/operator") &&
            !requested.startsWith("//") &&
            !requested.includes("\\")
        ) {
            setReturnTo(requested);
        }
        setStepUp(params.get("reason") === "step-up");
    }, []);

    async function submit(event: FormEvent<HTMLFormElement>) {
        event.preventDefault();
        setSubmitting(true);
        setError(null);
        try {
            const response = await fetch("/api/operator/session", {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    Accept: "application/json",
                    "Content-Type": "application/json",
                    [OPERATOR_REQUEST_HEADER]: "1",
                },
                body: JSON.stringify({ username, password }),
            });
            const body = (await response.json().catch(() => null)) as {
                message?: string;
            } | null;
            if (!response.ok) {
                const retryAfter = Number(response.headers.get("retry-after"));
                const retryMessage =
                    response.status === 429 &&
                    Number.isSafeInteger(retryAfter) &&
                    retryAfter > 0
                        ? ` Try again after ${new Date(Date.now() + retryAfter * 1_000).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}.`
                        : "";
                setError(
                    `${body?.message ?? "The username or password was not accepted."}${retryMessage}`,
                );
                return;
            }
            setPassword("");
            router.replace(returnTo);
        } catch {
            setError(
                "The operator service could not be reached. Check node readiness and try again.",
            );
        } finally {
            setSubmitting(false);
        }
    }

    return (
        <main className="grid min-h-screen bg-[#F2EDE3] lg:grid-cols-[minmax(20rem,0.85fr)_minmax(28rem,1.15fr)]">
            <section className="hidden border-r border-[#363028] bg-[#16120D] p-10 text-[#F2EDE3] lg:flex lg:flex-col lg:justify-between">
                <div>
                    <Link
                        href="/"
                        className="inline-flex items-center gap-3 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[#C8501E]"
                    >
                        <span className="flex h-9 w-9 items-center justify-center bg-[#C8501E] font-bold">
                            A
                        </span>
                        <span className="font-bold tracking-[0.14em]">
                            AZOA
                        </span>
                    </Link>
                    <p className="mt-20 max-w-lg text-4xl font-semibold leading-tight">
                        Operate the node.
                        <br />
                        Keep human judgment visible.
                    </p>
                    <p className="mt-6 max-w-md text-sm leading-6 text-[#b7ad9c]">
                        Monitor readiness, configure KYC policy, assign tenant
                        providers, and review only the decisions that require a
                        person.
                    </p>
                </div>
                <p className="font-mono text-[10px] uppercase tracking-[0.17em] text-[#80776a]">
                    Dedicated operator authority · short-lived session
                </p>
            </section>

            <section className="flex items-center justify-center p-5 sm:p-10">
                <div className="w-full max-w-md border border-[#b9ad9d] bg-[#faf7f0] p-5 sm:p-8">
                    <Link
                        href="/login"
                        className="mb-10 inline-flex min-h-11 items-center gap-2 text-sm text-[#655e53] hover:text-[#16120D]"
                    >
                        <ArrowLeft className="h-4 w-4" aria-hidden="true" />
                        Ordinary sign in
                    </Link>
                    <ShieldCheck
                        className="h-9 w-9 text-[#C8501E]"
                        aria-hidden="true"
                    />
                    <p className="mt-5 font-mono text-[10px] uppercase tracking-[0.2em] text-[#C8501E]">
                        Node operator
                    </p>
                    <h1 className="mt-2 text-3xl font-semibold tracking-tight text-[#16120D]">
                        Operator sign in
                    </h1>
                    <p className="mt-3 text-sm leading-6 text-[#655e53]">
                        Use the username and password configured by this
                        node&apos;s deployer. Credentials are never saved in the
                        browser.
                    </p>

                    {stepUp && (
                        <div
                            role="status"
                            className="mt-5 border border-[#C8501E] bg-[#fff1e8] p-3 text-sm leading-6 text-[#5b2b16]"
                        >
                            Please sign in again to confirm the sensitive action
                            you selected. You&apos;ll return to the same
                            operator page.
                        </div>
                    )}

                    <form onSubmit={submit} className="mt-8 space-y-5">
                        {error && (
                            <div
                                role="alert"
                                aria-live="assertive"
                                className="border border-red-700 bg-red-50 p-3 text-sm text-red-800"
                            >
                                {error}
                            </div>
                        )}
                        <div className="space-y-2">
                            <Label htmlFor="operator-username">
                                Operator username
                            </Label>
                            <div className="relative">
                                <UserRound
                                    className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-[#80776a]"
                                    aria-hidden="true"
                                />
                                <Input
                                    id="operator-username"
                                    name="username"
                                    value={username}
                                    onChange={(event) =>
                                        setUsername(event.target.value)
                                    }
                                    autoComplete="username"
                                    className="min-h-11 rounded-none border-[#9c9182] bg-white pl-10"
                                    required
                                    maxLength={128}
                                    disabled={submitting}
                                />
                            </div>
                        </div>
                        <div className="space-y-2">
                            <Label htmlFor="operator-password">Password</Label>
                            <div className="relative">
                                <LockKeyhole
                                    className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-[#80776a]"
                                    aria-hidden="true"
                                />
                                <Input
                                    id="operator-password"
                                    name="password"
                                    type="password"
                                    value={password}
                                    onChange={(event) =>
                                        setPassword(event.target.value)
                                    }
                                    autoComplete="current-password"
                                    className="min-h-11 rounded-none border-[#9c9182] bg-white pl-10"
                                    required
                                    maxLength={512}
                                    disabled={submitting}
                                />
                            </div>
                        </div>
                        <Button
                            type="submit"
                            className="min-h-12 w-full rounded-none text-sm"
                            disabled={submitting}
                        >
                            {submitting && (
                                <Loader2
                                    className="mr-2 h-4 w-4 animate-spin"
                                    aria-hidden="true"
                                />
                            )}
                            {submitting
                                ? "Verifying node authority…"
                                : "Enter operator console"}
                        </Button>
                    </form>
                    <p className="mt-6 text-xs leading-5 text-[#80776a]">
                        Lost credentials are reset through the deployment&apos;s
                        secret store, not in this console.
                    </p>
                </div>
            </section>
        </main>
    );
}
