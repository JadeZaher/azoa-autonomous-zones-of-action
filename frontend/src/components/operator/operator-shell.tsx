"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import {
    Activity,
    Building2,
    ClipboardCheck,
    History,
    LayoutDashboard,
    LogOut,
    RefreshCw,
    ShieldCheck,
} from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { OPERATOR_REQUEST_HEADER } from "@/lib/operator-contracts";

const OPERATOR_LINKS = [
    { href: "/operator", label: "Node", icon: LayoutDashboard },
    { href: "/operator/providers", label: "Providers", icon: ShieldCheck },
    { href: "/operator/tenants", label: "Tenants", icon: Building2 },
    { href: "/operator/reviews", label: "Reviews", icon: ClipboardCheck },
    { href: "/operator/audit", label: "Audit", icon: History },
];

export function OperatorShell({ children }: { children: React.ReactNode }) {
    const pathname = usePathname();
    const router = useRouter();
    const [checking, setChecking] = useState(true);
    const [authorized, setAuthorized] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const checkAccess = useCallback(async () => {
        setChecking(true);
        setAuthorized(false);
        setError(null);
        try {
            const response = await fetch("/api/operator/session", {
                cache: "no-store",
                credentials: "same-origin",
                headers: { Accept: "application/json" },
            });
            if (response.status === 401 || response.status === 403) {
                router.replace("/operator/login");
                return;
            }
            if (!response.ok) {
                setError(
                    response.status === 429
                        ? "The node is receiving too many operator requests. Wait a moment and retry."
                        : "The node operator service is not ready. Check the deployment and API health.",
                );
                return;
            }
            setAuthorized(true);
        } catch {
            setError("The node operator service could not be reached.");
        } finally {
            setChecking(false);
        }
    }, [router]);

    useEffect(() => {
        void checkAccess();
    }, [checkAccess]);

    useEffect(() => {
        const returnTo = encodeURIComponent(pathname);
        const expire = () =>
            router.replace(
                `/operator/login?reason=expired&returnTo=${returnTo}`,
            );
        const stepUp = () =>
            router.replace(
                `/operator/login?reason=step-up&returnTo=${returnTo}`,
            );
        window.addEventListener("azoa:operator-session-expired", expire);
        window.addEventListener("azoa:operator-step-up-required", stepUp);
        return () => {
            window.removeEventListener("azoa:operator-session-expired", expire);
            window.removeEventListener(
                "azoa:operator-step-up-required",
                stepUp,
            );
        };
    }, [pathname, router]);

    async function signOut() {
        await fetch("/api/operator/session", {
            method: "DELETE",
            credentials: "same-origin",
            headers: { [OPERATOR_REQUEST_HEADER]: "1" },
        }).catch(() => undefined);
        router.replace("/operator/login");
    }

    if (checking || (!authorized && !error)) {
        return (
            <main className="flex min-h-screen items-center justify-center bg-[#16120D] text-[#F2EDE3]">
                <div className="flex items-center gap-3 font-mono text-xs uppercase tracking-[0.16em]">
                    <RefreshCw
                        className="h-4 w-4 animate-spin"
                        aria-hidden="true"
                    />
                    Verifying operator session
                </div>
            </main>
        );
    }

    if (error) {
        return (
            <main className="flex min-h-screen items-center justify-center bg-[#16120D] p-5 text-[#F2EDE3]">
                <section
                    className="w-full max-w-lg border border-[#655f55] bg-[#201b15] p-6"
                    aria-labelledby="operator-unavailable"
                >
                    <Activity
                        className="mb-5 h-8 w-8 text-[#C8501E]"
                        aria-hidden="true"
                    />
                    <h1
                        id="operator-unavailable"
                        className="text-2xl font-semibold"
                    >
                        Operator console unavailable
                    </h1>
                    <p
                        className="mt-3 text-sm leading-6 text-[#c8c0b1]"
                        role="alert"
                    >
                        {error}
                    </p>
                    <div className="mt-6 flex flex-col gap-3 sm:flex-row">
                        <Button
                            onClick={() => void checkAccess()}
                            className="min-h-11"
                        >
                            Retry readiness check
                        </Button>
                        <Link
                            href="/login"
                            className={cn(
                                buttonVariants({ variant: "outline" }),
                                "min-h-11 border-[#655f55] bg-transparent px-4 text-[#F2EDE3]",
                            )}
                        >
                            Ordinary sign in
                        </Link>
                    </div>
                </section>
            </main>
        );
    }

    return (
        <div className="min-h-screen bg-background">
            <header className="sticky top-0 z-40 border-b border-[#504a41] bg-[#16120D] text-[#F2EDE3]">
                <div className="flex min-h-16 items-center gap-3 px-4 sm:px-6">
                    <Link
                        href="/operator"
                        className="flex min-h-11 items-center gap-3 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[#C8501E]"
                    >
                        <span className="flex h-8 w-8 items-center justify-center bg-[#C8501E] font-bold">
                            A
                        </span>
                        <span>
                            <span className="block text-sm font-bold tracking-[0.12em]">
                                AZOA
                            </span>
                            <span className="block font-mono text-[9px] uppercase tracking-[0.16em] text-[#b7ad9c]">
                                Node operator
                            </span>
                        </span>
                    </Link>
                    <div className="flex-1" />
                    <Button
                        variant="ghost"
                        onClick={() => void signOut()}
                        className="min-h-11 text-[#F2EDE3] hover:bg-[#302820] hover:text-white"
                    >
                        <LogOut className="mr-2 h-4 w-4" aria-hidden="true" />
                        <span className="hidden sm:inline">
                            End operator session
                        </span>
                        <span className="sm:hidden">Sign out</span>
                    </Button>
                </div>
                <nav
                    aria-label="Operator console"
                    className="overflow-x-auto border-t border-[#302b25] px-2 sm:px-4"
                >
                    <div className="flex min-w-max">
                        {OPERATOR_LINKS.map(({ href, label, icon: Icon }) => {
                            const active = pathname === href;
                            return (
                                <Link
                                    key={href}
                                    href={href}
                                    aria-current={active ? "page" : undefined}
                                    className={cn(
                                        "flex min-h-12 items-center gap-2 border-b-2 px-4 text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-[#C8501E]",
                                        active
                                            ? "border-[#C8501E] text-white"
                                            : "border-transparent text-[#b7ad9c] hover:text-white",
                                    )}
                                >
                                    <Icon
                                        className="h-4 w-4"
                                        aria-hidden="true"
                                    />
                                    {label}
                                </Link>
                            );
                        })}
                    </div>
                </nav>
            </header>
            <main className="mx-auto w-full max-w-7xl px-4 py-6 sm:px-6 sm:py-8">
                {children}
            </main>
        </div>
    );
}
