"use client";

import { useEffect, useState, FormEvent } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { useAzoaAuth } from "@/lib/azoa-auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Mail, Lock, ArrowRight, Loader2 } from "lucide-react";

export default function LoginPage() {
    const { login } = useAzoaAuth();
    const router = useRouter();
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [error, setError] = useState<string | null>(null);
    const [submitting, setSubmitting] = useState(false);
    const [returnTo, setReturnTo] = useState("/overview");
    const [stepUp, setStepUp] = useState(false);

    useEffect(() => {
        const params = new URLSearchParams(window.location.search);
        const requested = params.get("returnTo");
        if (
            requested?.startsWith("/") &&
            !requested.startsWith("//") &&
            !requested.includes("\\")
        ) {
            setReturnTo(requested);
        }
        setStepUp(params.get("reason") === "step-up");
    }, []);

    async function handleSubmit(e: FormEvent) {
        e.preventDefault();
        setError(null);
        setSubmitting(true);
        const result = await login(email, password);
        if (result.success) {
            router.replace(returnTo);
        } else {
            setError(result.error ?? "Invalid email or password");
        }
        setSubmitting(false);
    }

    return (
        <div className="space-y-6">
            {/* Header */}
            <div className="space-y-2 text-center lg:text-left">
                <p className="font-mono text-[11px] uppercase tracking-[0.2em] text-primary">
                    Sign in
                </p>
                <h1 className="text-2xl font-bold tracking-tight">
                    Welcome back
                </h1>
                <p className="text-sm text-muted-foreground">
                    Sign in to your AZOA account to continue
                </p>
            </div>

            <form onSubmit={handleSubmit} className="space-y-5">
                {stepUp && (
                    <div
                        role="status"
                        className="border border-primary/40 bg-primary/5 px-4 py-3 text-sm leading-6"
                    >
                        Sign in again to confirm the tenant setting you
                        selected. You&apos;ll return to that page afterward.
                    </div>
                )}
                {error && (
                    <div className="flex items-start gap-3 rounded-lg border border-destructive/30 bg-destructive/5 px-4 py-3 text-sm text-destructive">
                        <span className="mt-0.5 shrink-0 select-none text-base">
                            !
                        </span>
                        <p>{error}</p>
                    </div>
                )}

                <div className="space-y-4">
                    <div className="space-y-2">
                        <Label htmlFor="email">Email</Label>
                        <div className="relative">
                            <Mail className="pointer-events-none absolute left-3.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/50" />
                            <Input
                                id="email"
                                type="email"
                                placeholder="you@example.com"
                                className="h-11 pl-10"
                                value={email}
                                onChange={(e) => setEmail(e.target.value)}
                                required
                                autoComplete="email"
                            />
                        </div>
                    </div>

                    <div className="space-y-2">
                        <Label htmlFor="password">Password</Label>
                        <div className="relative">
                            <Lock className="pointer-events-none absolute left-3.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground/50" />
                            <Input
                                id="password"
                                type="password"
                                placeholder="Enter your password"
                                className="h-11 pl-10"
                                value={password}
                                onChange={(e) => setPassword(e.target.value)}
                                required
                                autoComplete="current-password"
                            />
                        </div>
                    </div>
                </div>

                <Button
                    type="submit"
                    className="h-11 w-full text-sm font-semibold"
                    disabled={submitting}
                >
                    {submitting ? (
                        <span className="flex items-center gap-2">
                            <Loader2 className="h-4 w-4 animate-spin" />
                            Signing in…
                        </span>
                    ) : (
                        <span className="flex items-center gap-2">
                            Sign in
                            <ArrowRight className="h-4 w-4" />
                        </span>
                    )}
                </Button>
            </form>

            <p className="border-y border-border py-3 text-center text-xs text-muted-foreground">
                This node currently supports password sign-in only. Account
                recovery and federated sign-in are not configured.
            </p>

            <p className="text-center text-sm text-muted-foreground">
                Don&apos;t have an account?{" "}
                <Link
                    href="/register"
                    className="font-medium text-primary underline-offset-4 hover:underline"
                >
                    Create one
                </Link>
            </p>

            <p className="text-center text-xs text-muted-foreground">
                Running this node?{" "}
                <Link
                    href="/operator/login"
                    className="font-medium text-foreground underline underline-offset-4"
                >
                    Open operator sign in
                </Link>
            </p>
        </div>
    );
}
