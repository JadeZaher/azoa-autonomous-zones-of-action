"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import {
    ExternalLink,
    FileCheck2,
    RefreshCw,
    ShieldAlert,
    ShieldCheck,
} from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
} from "@/components/ui/card";
import { ErrorBanner } from "@/components/shared/error-banner";
import { humanizeReadiness } from "@/lib/kyc-provider-state";
import {
    getTenantKycProviders,
    getTenantKycSelection,
    setTenantKycSelection,
    TenantKycRequestError,
} from "@/lib/tenant-kyc-client";
import { operatorRequest, OperatorRequestError } from "@/lib/operator-client";
import type {
    CursorPage,
    TenantKycProviderChoiceResponse,
    TenantKycSelectionResponse,
} from "@/lib/operator-contracts";

export default function TenantKycPage() {
    const [choices, setChoices] = useState<TenantKycProviderChoiceResponse[]>(
        [],
    );
    const [selection, setSelection] =
        useState<TenantKycSelectionResponse | null>(null);
    const [selectedKey, setSelectedKey] = useState("");
    const [confirmed, setConfirmed] = useState(false);
    const [loading, setLoading] = useState(true);
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [reauthRequired, setReauthRequired] = useState(false);
    const [success, setSuccess] = useState<string | null>(null);

    const load = useCallback(async () => {
        setLoading(true);
        setError(null);
        setReauthRequired(false);
        try {
            const [providerChoices, current] = await Promise.all([
                getTenantKycProviders(),
                getTenantKycSelection(),
            ]);
            setChoices(providerChoices);
            setSelection(current);
            setSelectedKey(current.providerKey ?? "");
            setConfirmed(false);
        } catch (reason) {
            setError(
                reason instanceof Error
                    ? reason.message
                    : "KYC provider settings could not be loaded.",
            );
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => {
        void load();
    }, [load]);

    const changed = Boolean(
        selection &&
        selectedKey &&
        selectedKey !== (selection.providerKey ?? ""),
    );
    const isProviderChange = Boolean(selection?.providerKey && changed);

    async function save() {
        if (
            !selection ||
            !selectedKey ||
            !changed ||
            (isProviderChange && !confirmed)
        )
            return;
        setSaving(true);
        setError(null);
        setReauthRequired(false);
        setSuccess(null);
        try {
            const updated = await setTenantKycSelection(
                selectedKey,
                selection.selectionVersion,
            );
            setSelection(updated);
            setSelectedKey(updated.providerKey ?? "");
            setConfirmed(false);
            setSuccess(
                `KYC provider changed to ${updated.providerDisplayName ?? updated.providerKey}.`,
            );
        } catch (reason) {
            const message =
                reason instanceof Error
                    ? reason.message
                    : "The KYC provider could not be saved.";
            if (reason instanceof TenantKycRequestError && reason.conflict) {
                await load();
                setError(message);
            } else {
                setReauthRequired(
                    reason instanceof TenantKycRequestError &&
                        reason.reauthenticate,
                );
                setError(message);
            }
        } finally {
            setSaving(false);
        }
    }

    return (
        <div className="mx-auto max-w-4xl space-y-6">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
                <div>
                    <p className="font-mono text-[10px] uppercase tracking-[0.18em] text-primary">
                        Tenant identity policy
                    </p>
                    <h1 className="mt-2 text-2xl font-semibold tracking-tight">
                        KYC provider
                    </h1>
                    <p className="mt-2 max-w-2xl text-sm leading-6 text-muted-foreground">
                        Choose how people in your tenant verify identity. Only
                        providers enabled and ready on this Azoa node appear
                        here.
                    </p>
                </div>
                <Button
                    variant="outline"
                    className="min-h-11 rounded-none self-start"
                    onClick={() => void load()}
                    disabled={loading}
                >
                    <RefreshCw
                        className={`mr-2 h-4 w-4 ${loading ? "animate-spin" : ""}`}
                        aria-hidden="true"
                    />{" "}
                    Refresh
                </Button>
            </div>

            {error && (
                <ErrorBanner message={error} onRetry={() => void load()} />
            )}
            {reauthRequired && (
                <div className="flex flex-col gap-3 border border-primary bg-primary/5 p-4 text-sm sm:flex-row sm:items-center sm:justify-between">
                    <p>
                        Fresh authentication protects provider changes from an
                        unattended session.
                    </p>
                    <Link
                        href="/login?reason=step-up&returnTo=%2Fkyc"
                        className={buttonVariants({
                            className: "min-h-11 shrink-0 rounded-none",
                        })}
                    >
                        Sign in again
                    </Link>
                </div>
            )}
            {success && (
                <div
                    role="status"
                    aria-live="polite"
                    className="border border-emerald-700 bg-emerald-50 p-3 text-sm text-emerald-900"
                >
                    {success}
                </div>
            )}

            {selection && (
                <Card className="rounded-none border ring-0">
                    <CardHeader>
                        <CardTitle className="text-base">
                            Current tenant policy
                        </CardTitle>
                        <CardDescription>
                            Selection revision {selection.selectionVersion}
                        </CardDescription>
                    </CardHeader>
                    <CardContent className="grid gap-4 text-sm sm:grid-cols-2">
                        <div className="border-t pt-3">
                            <p className="text-xs text-muted-foreground">
                                Provider
                            </p>
                            <p className="mt-1 font-medium">
                                {selection.providerDisplayName ??
                                    selection.providerKey ??
                                    "Not selected"}
                            </p>
                        </div>
                        <div className="border-t pt-3">
                            <p className="text-xs text-muted-foreground">
                                Readiness
                            </p>
                            <p className="mt-1 font-medium">
                                {humanizeReadiness(selection.readinessCode)}
                            </p>
                        </div>
                    </CardContent>
                </Card>
            )}

            <section aria-labelledby="provider-choices">
                <h2 id="provider-choices" className="text-lg font-semibold">
                    Available verification paths
                </h2>
                <p className="mt-1 text-sm text-muted-foreground">
                    A provider may redirect to its hosted flow, accept secure
                    document references, or support both.
                </p>
                {loading && choices.length === 0 ? (
                    <div
                        role="status"
                        className="mt-4 flex min-h-40 items-center justify-center border border-dashed text-sm text-muted-foreground"
                    >
                        <RefreshCw className="mr-2 h-4 w-4 animate-spin" />{" "}
                        Loading ready providers
                    </div>
                ) : (
                    <div className="mt-4 grid gap-3 sm:grid-cols-2">
                        {choices.map((provider) => (
                            <label
                                key={provider.providerKey}
                                className="flex min-h-36 cursor-pointer flex-col border bg-card p-4 has-[:checked]:border-primary has-[:checked]:bg-primary/5"
                            >
                                <span className="flex items-start gap-3">
                                    <input
                                        type="radio"
                                        name="kyc-provider"
                                        value={provider.providerKey}
                                        checked={
                                            selectedKey === provider.providerKey
                                        }
                                        onChange={() => {
                                            setSelectedKey(
                                                provider.providerKey,
                                            );
                                            setConfirmed(false);
                                            setSuccess(null);
                                        }}
                                        className="mt-1 h-5 w-5 shrink-0 accent-primary"
                                    />
                                    <span>
                                        <span className="block font-medium">
                                            {provider.displayName}
                                        </span>
                                        <span className="mt-1 block font-mono text-[10px] text-muted-foreground">
                                            {provider.providerKey}
                                        </span>
                                        <span className="mt-2 block text-xs text-muted-foreground">
                                            Assurance: {provider.assuranceLevel}
                                        </span>
                                    </span>
                                </span>
                                <span className="mt-4 flex flex-wrap gap-2 text-xs text-muted-foreground">
                                    {provider.hostedVerification && (
                                        <span className="inline-flex items-center gap-1 border px-2 py-1">
                                            <ExternalLink className="h-3.5 w-3.5" />{" "}
                                            Hosted verification
                                        </span>
                                    )}
                                    {provider.acceptsDocumentReferences && (
                                        <span className="inline-flex items-center gap-1 border px-2 py-1">
                                            <FileCheck2 className="h-3.5 w-3.5" />{" "}
                                            Secure document references
                                        </span>
                                    )}
                                </span>
                            </label>
                        ))}
                        {!loading && choices.length === 0 && (
                            <div className="border border-dashed p-5 text-sm leading-6 text-muted-foreground sm:col-span-2">
                                No provider is ready for tenant selection. Ask
                                the node operator to enable and configure a KYC
                                adapter.
                            </div>
                        )}
                    </div>
                )}
            </section>

            {isProviderChange && (
                <label className="flex items-start gap-3 border border-amber-700 bg-amber-50 p-4 text-sm leading-6 text-amber-950 dark:bg-amber-950/30 dark:text-amber-100">
                    <input
                        type="checkbox"
                        checked={confirmed}
                        onChange={(event) => setConfirmed(event.target.checked)}
                        className="mt-1 h-5 w-5 shrink-0 accent-primary"
                    />
                    <span>
                        I understand this change makes active attempts and prior
                        approvals stale. Affected people must verify again
                        before KYC-gated value actions can continue.
                    </span>
                </label>
            )}

            <AmlManualReviewQueue />

            <div className="flex justify-end border-t pt-5">
                <Button
                    className="min-h-11 rounded-none"
                    onClick={() => void save()}
                    disabled={
                        saving || !changed || (isProviderChange && !confirmed)
                    }
                >
                    <ShieldCheck className="mr-2 h-4 w-4" aria-hidden="true" />
                    {saving
                        ? "Saving tenant policy…"
                        : isProviderChange
                          ? "Confirm provider change"
                          : "Save provider"}
                </Button>
            </div>
        </div>
    );
}

/** Node-operator surface: the sanctions screenings a human still owes a decision. */
interface AmlManualReviewItem {
    userId: string;
    tenantId?: string | null;
    /** AZOA's own verdict; never the provider's raw listing payload. */
    decision: "MANUAL_REVIEW" | "HIT";
    topScore: number;
    hitCount: number;
    topics: string[];
    resultsTruncated: boolean;
    screenedAt: string;
    reviewDueAt?: string | null;
    policyVersion: string;
}

interface AmlManualReviewDecisionRequest {
    decision: "approve" | "deny";
    notes: string;
}

const AML_REVIEW_PATH = "kyc/aml-reviews";

function formatWhen(value: string | null | undefined) {
    if (!value) return "—";
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

/** Milliseconds until the operator's own decision deadline; null when there is none. */
function millisecondsUntilDue(value: string | null | undefined) {
    if (!value) return null;
    const due = new Date(value).getTime();
    return Number.isNaN(due) ? null : due - Date.now();
}

function isOverdue(item: AmlManualReviewItem) {
    const remaining = millisecondsUntilDue(item.reviewDueAt);
    return remaining !== null && remaining < 0;
}

/**
 * Nearest deadline first, so the person closest to being stranded is at the top.
 * A row with no deadline has never been screened at all — a worse state than a
 * late review, so it sorts to the top rather than out of sight at the bottom.
 */
function byUrgency(left: AmlManualReviewItem, right: AmlManualReviewItem) {
    const a = millisecondsUntilDue(left.reviewDueAt);
    const b = millisecondsUntilDue(right.reviewDueAt);
    if (a === null && b === null) return 0;
    if (a === null) return -1;
    if (b === null) return 1;
    return a - b;
}

/**
 * Lists the AML manual-review queue and records an operator decision.
 *
 * The node endpoint this calls is NOT deployed yet (see the not-wired banner
 * below and W1.5's cross-boundary handoff); when it answers 404 or 501 this
 * panel says so in as many words rather than rendering an empty queue, because
 * "no one is waiting" and "nobody is listening" are opposite facts.
 */
function AmlManualReviewQueue() {
    const [queue, setQueue] = useState<AmlManualReviewItem[]>([]);
    const [loading, setLoading] = useState(true);
    const [notWired, setNotWired] = useState(false);
    const [forbidden, setForbidden] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [deciding, setDeciding] = useState<string | null>(null);
    const [notes, setNotes] = useState<Record<string, string>>({});

    const load = useCallback(async () => {
        setLoading(true);
        setError(null);
        setNotWired(false);
        setForbidden(false);
        try {
            const page = await operatorRequest<CursorPage<AmlManualReviewItem>>(
                `${AML_REVIEW_PATH}?limit=50`,
            );
            setQueue([...page.items].sort(byUrgency));
        } catch (reason) {
            if (reason instanceof OperatorRequestError) {
                if (reason.status === 404 || reason.status === 501) {
                    setNotWired(true);
                    setQueue([]);
                    return;
                }
                if (reason.status === 401 || reason.status === 403) {
                    setForbidden(true);
                    setQueue([]);
                    return;
                }
            }
            setError(
                reason instanceof Error
                    ? reason.message
                    : "The AML review queue could not be loaded.",
            );
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => {
        void load();
    }, [load]);

    async function decide(item: AmlManualReviewItem, decision: "approve" | "deny") {
        const note = (notes[item.userId] ?? "").trim();
        if (!note) {
            setError(
                "Record why this subject is being cleared or denied before deciding.",
            );
            return;
        }
        setDeciding(item.userId);
        setError(null);
        try {
            const body: AmlManualReviewDecisionRequest = {
                decision,
                notes: note,
            };
            await operatorRequest<AmlManualReviewItem>(
                `${AML_REVIEW_PATH}/${encodeURIComponent(item.userId)}/decision`,
                { method: "POST", body },
            );
            await load();
        } catch (reason) {
            setError(
                reason instanceof Error
                    ? reason.message
                    : "The review decision could not be recorded.",
            );
        } finally {
            setDeciding(null);
        }
    }

    return (
        <section aria-labelledby="aml-review-queue" className="space-y-3">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
                <div>
                    <h2 id="aml-review-queue" className="text-lg font-semibold">
                        Compliance review queue
                    </h2>
                    <p className="mt-1 max-w-2xl text-sm leading-6 text-muted-foreground">
                        People whose identity verified but whose sanctions
                        screening needs a human decision. Until one is recorded
                        they read as <strong>Under review</strong> to their
                        tenant, and every value action stays closed. A name
                        collision is the common case; leaving it undecided is
                        what strands a legitimate person.
                    </p>
                </div>
                <Button
                    variant="outline"
                    className="min-h-11 self-start rounded-none"
                    onClick={() => void load()}
                    disabled={loading}
                >
                    <RefreshCw
                        className={`mr-2 h-4 w-4 ${loading ? "animate-spin" : ""}`}
                        aria-hidden="true"
                    />{" "}
                    Refresh queue
                </Button>
            </div>

            {error && <ErrorBanner message={error} onRetry={() => void load()} />}

            {notWired && (
                <div className="border border-amber-700 bg-amber-50 p-4 text-sm leading-6 text-amber-950 dark:bg-amber-950/30 dark:text-amber-100">
                    <p className="font-medium">
                        This node exposes no AML review endpoint yet.
                    </p>
                    <p className="mt-1">
                        The queue and the approve/deny decision are recorded
                        against the screening ledger, which is node-operator
                        data. Flagged people still project as{" "}
                        <strong>Under review</strong> and stay closed to value —
                        nothing here fails open — but no operator can clear them
                        until the route ships.
                    </p>
                    <p className="mt-2 font-mono text-xs">
                        GET api/operator/{AML_REVIEW_PATH} · POST
                        api/operator/{AML_REVIEW_PATH}/{"{userId}"}/decision
                    </p>
                    <p className="mt-2">
                        Both legs are missing: the node routes on{" "}
                        <code>NodeOperatorController</code>, and the operator BFF
                        allow-list in{" "}
                        <code>app/api/operator/[...path]/route.ts</code>, which
                        refuses any path it does not name.
                    </p>
                </div>
            )}

            {forbidden && (
                <div className="border border-dashed p-5 text-sm leading-6 text-muted-foreground">
                    Compliance review is a node-operator action. Sign in to the
                    operator console to see this queue.
                </div>
            )}

            {!notWired && !forbidden && (
                <div className="grid gap-3">
                    {queue.map((item) => (
                        <Card
                            key={item.userId}
                            className="rounded-none border ring-0"
                        >
                            <CardHeader>
                                <CardTitle className="flex items-center gap-2 text-base">
                                    <ShieldAlert
                                        className="h-4 w-4 text-amber-700"
                                        aria-hidden="true"
                                    />
                                    Awaiting a decision
                                    {isOverdue(item) && (
                                        <span className="border border-red-700 px-2 py-0.5 text-[10px] font-medium uppercase tracking-wide text-red-800 dark:text-red-300">
                                            Past the deadline
                                        </span>
                                    )}
                                </CardTitle>
                                <CardDescription>
                                    Screened {formatWhen(item.screenedAt)} ·
                                    decision due {formatWhen(item.reviewDueAt)}
                                    {item.reviewDueAt
                                        ? ""
                                        : " (never screened — no deadline is running)"}
                                </CardDescription>
                            </CardHeader>
                            <CardContent className="space-y-4 text-sm">
                                <div className="grid gap-3 sm:grid-cols-2">
                                    <div className="border-t pt-3">
                                        <p className="text-xs text-muted-foreground">
                                            Person
                                        </p>
                                        <p className="mt-1 font-mono text-xs">
                                            {item.userId}
                                        </p>
                                    </div>
                                    <div className="border-t pt-3">
                                        <p className="text-xs text-muted-foreground">
                                            Match strength
                                        </p>
                                        <p className="mt-1 font-medium">
                                            {item.hitCount} candidate
                                            {item.hitCount === 1 ? "" : "s"},
                                            top score {item.topScore}
                                            {item.resultsTruncated
                                                ? " (list incomplete)"
                                                : ""}
                                        </p>
                                    </div>
                                </div>
                                {item.topics.length > 0 && (
                                    <p className="flex flex-wrap gap-2 text-xs text-muted-foreground">
                                        {item.topics.map((topic) => (
                                            <span
                                                key={topic}
                                                className="border px-2 py-1"
                                            >
                                                {topic}
                                            </span>
                                        ))}
                                    </p>
                                )}
                                <label className="block">
                                    <span className="text-xs text-muted-foreground">
                                        Why (recorded with the decision)
                                    </span>
                                    <textarea
                                        className="mt-1 min-h-20 w-full border bg-background p-2 text-sm"
                                        value={notes[item.userId] ?? ""}
                                        onChange={(event) =>
                                            setNotes((current) => ({
                                                ...current,
                                                [item.userId]:
                                                    event.target.value,
                                            }))
                                        }
                                    />
                                </label>
                                <div className="flex flex-wrap justify-end gap-2 border-t pt-4">
                                    <Button
                                        variant="outline"
                                        className="min-h-11 rounded-none"
                                        disabled={deciding === item.userId}
                                        onClick={() =>
                                            void decide(item, "deny")
                                        }
                                    >
                                        Keep closed
                                    </Button>
                                    <Button
                                        className="min-h-11 rounded-none"
                                        disabled={deciding === item.userId}
                                        onClick={() =>
                                            void decide(item, "approve")
                                        }
                                    >
                                        <ShieldCheck
                                            className="mr-2 h-4 w-4"
                                            aria-hidden="true"
                                        />
                                        {deciding === item.userId
                                            ? "Recording…"
                                            : "Clear this person"}
                                    </Button>
                                </div>
                            </CardContent>
                        </Card>
                    ))}
                    {!loading && queue.length === 0 && (
                        <div className="border border-dashed p-5 text-sm leading-6 text-muted-foreground">
                            No one is waiting on a compliance decision.
                        </div>
                    )}
                    {loading && queue.length === 0 && (
                        <div
                            role="status"
                            className="flex min-h-24 items-center justify-center border border-dashed text-sm text-muted-foreground"
                        >
                            <RefreshCw className="mr-2 h-4 w-4 animate-spin" />{" "}
                            Loading the review queue
                        </div>
                    )}
                </div>
            )}
        </section>
    );
}
