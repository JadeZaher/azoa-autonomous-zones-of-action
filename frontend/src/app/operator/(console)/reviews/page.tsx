"use client";

import { useCallback, useEffect, useState } from "react";
import {
    RefreshCw,
    ShieldAlert,
    ShieldCheck,
    ShieldQuestion,
    UserCheck,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
} from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import {
    FlatCard,
    OperatorError,
    OperatorLoading,
    OperatorPageHeader,
    StatusPill,
    formatOperatorDate,
} from "@/components/operator/operator-ui";
import { operatorRequest, OperatorRequestError } from "@/lib/operator-client";
import type {
    CursorPage,
    OperatorKycDecisionRequest,
    OperatorKycSubmissionQueueItem,
} from "@/lib/operator-contracts";

export default function OperatorReviewsPage() {
    const [queue, setQueue] = useState<OperatorKycSubmissionQueueItem[]>([]);
    const [nextCursor, setNextCursor] = useState<string | null>(null);
    const [reviewing, setReviewing] =
        useState<OperatorKycSubmissionQueueItem | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);
    const [complianceLoading, setComplianceLoading] = useState(true);
    const [reloadToken, setReloadToken] = useState(0);

    const load = useCallback(
        async ({
            cursor,
            append = false,
        }: { cursor?: string; append?: boolean } = {}) => {
            setLoading(true);
            setError(null);
            try {
                const params = new URLSearchParams({
                    status: "pending",
                    limit: "50",
                });
                if (cursor) params.set("cursor", cursor);
                const page = await operatorRequest<
                    CursorPage<OperatorKycSubmissionQueueItem>
                >(`kyc/submissions?${params.toString()}`);
                setQueue((current) =>
                    append ? [...current, ...page.items] : page.items,
                );
                setNextCursor(page.nextCursor ?? null);
            } catch (reason) {
                setError(
                    reason instanceof Error
                        ? reason.message
                        : "The KYC review queue could not be loaded.",
                );
            } finally {
                setLoading(false);
            }
        },
        [],
    );

    useEffect(() => {
        void load();
    }, [load]);

    return (
        <div className="space-y-6">
            <OperatorPageHeader
                eyebrow="Node operator"
                title="Reviews"
                description="Two queues a human owns: the compliance decisions holding verified people out of value, and the Development-only identity simulations. Documents and sensitive identity evidence are never exposed here."
                onRefresh={() => {
                    setReloadToken((token) => token + 1);
                    void load();
                }}
                refreshing={loading || complianceLoading}
            />

            <ComplianceReviewDesk
                reloadToken={reloadToken}
                onLoadingChange={setComplianceLoading}
            />

            <div className="border-t pt-6">
                <h2 className="text-lg font-semibold">
                    Development identity simulations
                </h2>
                <p className="mt-1 max-w-2xl text-sm leading-6 text-muted-foreground">
                    Act only on Development-authorized simulation rows. External
                    provider outcomes stay provider-managed.
                </p>
            </div>
            <div className="flex flex-wrap gap-2">
                <StatusPill tone={queue.length > 0 ? "attention" : "ready"}>
                    {queue.length} loaded pending
                </StatusPill>
                <StatusPill tone="neutral">
                    Development simulation only
                </StatusPill>
            </div>
            {error && (
                <OperatorError message={error} onRetry={() => void load()} />
            )}
            {loading && queue.length === 0 ? (
                <OperatorLoading label="Loading human review queue" />
            ) : (
                <div className="grid gap-4 lg:grid-cols-2">
                    {queue.map((submission) => (
                        <ReviewCard
                            key={submission.id}
                            submission={submission}
                            onReview={() => setReviewing(submission)}
                        />
                    ))}
                    {!loading && queue.length === 0 && (
                        <div className="border border-dashed border-border p-8 text-center lg:col-span-2">
                            <UserCheck
                                className="mx-auto h-8 w-8 text-emerald-700"
                                aria-hidden="true"
                            />
                            <p className="mt-3 font-medium">
                                No human decisions are waiting
                            </p>
                            <p className="mt-1 text-sm text-muted-foreground">
                                External-provider verification continues through
                                its configured adapter.
                            </p>
                        </div>
                    )}
                </div>
            )}
            {nextCursor && (
                <div className="flex justify-center border-t pt-5">
                    <Button
                        variant="outline"
                        className="min-h-11 rounded-none"
                        disabled={loading}
                        onClick={() =>
                            void load({ cursor: nextCursor, append: true })
                        }
                    >
                        {loading ? "Loading more..." : "Load more submissions"}
                    </Button>
                </div>
            )}
            {reviewing && (
                <DecisionDialog
                    submission={reviewing}
                    onClose={() => setReviewing(null)}
                    onDecided={async () => {
                        setReviewing(null);
                        await load();
                    }}
                />
            )}
        </div>
    );
}

function ReviewCard({
    submission,
    onReview,
}: {
    submission: OperatorKycSubmissionQueueItem;
    onReview: () => void;
}) {
    const [observedAt] = useState(Date.now);
    const expired = new Date(submission.expiresAt).getTime() <= observedAt;
    const canReview =
        submission.humanReviewAllowed &&
        submission.reviewMode === "development_simulation";
    return (
        <FlatCard>
            <CardHeader>
                <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                        <p className="font-mono text-[10px] uppercase tracking-[0.14em] text-muted-foreground">
                            Submission {submission.id.slice(0, 8)}…
                        </p>
                        <CardTitle className="mt-1 text-base">
                            {submission.providerKey}
                        </CardTitle>
                    </div>
                    <StatusPill
                        tone={
                            expired
                                ? "attention"
                                : canReview
                                  ? "attention"
                                  : "neutral"
                        }
                    >
                        {expired
                            ? "Expired"
                            : canReview
                              ? "Dev simulation"
                              : "Provider managed"}
                    </StatusPill>
                </div>
            </CardHeader>
            <CardContent className="space-y-4 text-sm">
                <dl className="grid gap-3 sm:grid-cols-2">
                    <QueueDatum
                        label="User"
                        value={`${submission.userId.slice(0, 8)}…`}
                        title={submission.userId}
                    />
                    <QueueDatum
                        label="Tenant"
                        value={
                            submission.tenantId
                                ? `${submission.tenantId.slice(0, 8)}…`
                                : "Direct"
                        }
                        title={submission.tenantId ?? undefined}
                    />
                    <QueueDatum
                        label="Waiting"
                        value={ageLabel(submission.submittedAt, observedAt)}
                    />
                    <QueueDatum
                        label="Expires"
                        value={formatOperatorDate(submission.expiresAt)}
                    />
                </dl>
                {!canReview && !expired && (
                    <p className="flex gap-2 border bg-muted/40 p-3 text-xs leading-5 text-muted-foreground">
                        The external provider owns this result. The console will
                        not manually override it.
                    </p>
                )}
                {canReview && !expired && (
                    <p className="border border-amber-700 bg-amber-50 p-3 text-xs leading-5 text-amber-950 dark:bg-amber-950/30 dark:text-amber-100">
                        Development simulation only. No real identity evidence
                        is displayed or evaluated in this flow.
                    </p>
                )}
                <div className="flex justify-end border-t pt-3">
                    <Button
                        className="min-h-11 rounded-none"
                        onClick={onReview}
                        disabled={!canReview || expired}
                    >
                        <ShieldQuestion
                            className="mr-2 h-4 w-4"
                            aria-hidden="true"
                        />
                        {expired
                            ? "Expired attempt"
                            : canReview
                              ? "Simulate decision"
                              : "Awaiting provider"}
                    </Button>
                </div>
            </CardContent>
        </FlatCard>
    );
}

function QueueDatum({
    label,
    value,
    title,
}: {
    label: string;
    value: string;
    title?: string;
}) {
    return (
        <div className="border-t pt-2">
            <dt className="text-xs text-muted-foreground">{label}</dt>
            <dd className="mt-1 font-mono text-xs" title={title}>
                {value}
            </dd>
        </div>
    );
}

function ageLabel(value: string, observedAt: number): string {
    const timestamp = new Date(value).getTime();
    if (!Number.isFinite(timestamp)) return "Unknown";
    const minutes = Math.max(0, Math.floor((observedAt - timestamp) / 60_000));
    if (minutes < 60) return `${minutes} min`;
    const hours = Math.floor(minutes / 60);
    if (hours < 48) return `${hours} hr`;
    return `${Math.floor(hours / 24)} days`;
}

function DecisionDialog({
    submission,
    onClose,
    onDecided,
}: {
    submission: OperatorKycSubmissionQueueItem;
    onClose: () => void;
    onDecided: () => Promise<void>;
}) {
    const [decision, setDecision] = useState<"approve" | "reject" | "">("");
    const [notes, setNotes] = useState("");
    const [reason, setReason] = useState("");
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState<string | null>(null);

    async function decide() {
        if (!decision || (decision === "reject" && !reason.trim())) return;
        setSaving(true);
        setError(null);
        const request: OperatorKycDecisionRequest = {
            decision,
            notes: notes.trim() || undefined,
            reason: decision === "reject" ? reason.trim() : undefined,
        };
        try {
            await operatorRequest<unknown>(
                `kyc/submissions/${submission.id}/decision`,
                { method: "POST", body: request },
            );
            await onDecided();
        } catch (cause) {
            setError(
                cause instanceof OperatorRequestError && cause.status === 409
                    ? "This submission changed while it was open. Close and refresh the queue to see its current status."
                    : cause instanceof Error
                      ? cause.message
                      : "The decision could not be recorded.",
            );
        } finally {
            setSaving(false);
        }
    }

    return (
        <Dialog
            open
            onOpenChange={(open) => {
                if (!open && !saving) onClose();
            }}
        >
            <DialogContent className="max-h-[calc(100vh-2rem)] overflow-y-auto rounded-none sm:max-w-lg">
                <DialogHeader>
                    <DialogTitle>
                        Development verification simulation
                    </DialogTitle>
                    <DialogDescription>
                        This action exercises the Development-only manual path.
                        It does not claim that real identity evidence was
                        inspected. Your operator identity is recorded
                        automatically.
                    </DialogDescription>
                </DialogHeader>

                <fieldset className="space-y-3">
                    <legend className="text-sm font-medium">Decision</legend>
                    <DecisionChoice
                        value="approve"
                        selected={decision}
                        onSelect={(value) => {
                            setDecision(value);
                            if (value === "approve") setReason("");
                        }}
                        title="Approve simulated attempt"
                        description="Mark this Development-only test attempt approved."
                    />
                    <DecisionChoice
                        value="reject"
                        selected={decision}
                        onSelect={(value) => {
                            setDecision(value);
                            if (value === "approve") setReason("");
                        }}
                        title="Reject simulated attempt"
                        description="Reject the test attempt and record a reason for the development workflow."
                    />
                </fieldset>

                {decision === "reject" && (
                    <div className="space-y-2">
                        <Label htmlFor="rejection-reason">
                            Rejection reason
                        </Label>
                        <Textarea
                            id="rejection-reason"
                            value={reason}
                            onChange={(event) => setReason(event.target.value)}
                            maxLength={1_000}
                            required
                            className="min-h-24 rounded-none"
                            placeholder="State what must be corrected without exposing sensitive evidence."
                        />
                    </div>
                )}
                {decision && (
                    <div className="space-y-2">
                        <Label htmlFor="review-notes">
                            Internal review note{" "}
                            <span className="font-normal text-muted-foreground">
                                (optional)
                            </span>
                        </Label>
                        <Textarea
                            id="review-notes"
                            value={notes}
                            onChange={(event) => setNotes(event.target.value)}
                            maxLength={2_000}
                            className="min-h-20 rounded-none"
                            placeholder="Record process context, not document contents."
                        />
                    </div>
                )}
                {error && <OperatorError message={error} />}
                <DialogFooter className="rounded-none">
                    <Button
                        variant="outline"
                        className="min-h-11 rounded-none"
                        onClick={onClose}
                        disabled={saving}
                    >
                        Cancel
                    </Button>
                    <Button
                        variant={
                            decision === "reject" ? "destructive" : "default"
                        }
                        className="min-h-11 rounded-none"
                        onClick={() => void decide()}
                        disabled={
                            saving ||
                            !decision ||
                            (decision === "reject" && !reason.trim())
                        }
                    >
                        {saving
                            ? "Recording decision…"
                            : decision === "reject"
                              ? "Confirm rejection"
                              : decision === "approve"
                                ? "Confirm approval"
                                : "Choose a decision"}
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}

function DecisionChoice({
    value,
    selected,
    onSelect,
    title,
    description,
}: {
    value: "approve" | "reject";
    selected: string;
    onSelect: (value: "approve" | "reject") => void;
    title: string;
    description: string;
}) {
    return (
        <label className="flex min-h-16 cursor-pointer items-start gap-3 border p-3 has-[:checked]:border-primary has-[:checked]:bg-primary/5">
            <input
                type="radio"
                name="decision"
                value={value}
                checked={selected === value}
                onChange={() => onSelect(value)}
                className="mt-1 h-5 w-5 accent-primary"
            />
            <span>
                <span className="block font-medium">{title}</span>
                <span className="mt-1 block text-xs leading-5 text-muted-foreground">
                    {description}
                </span>
            </span>
        </label>
    );
}

/**
 * Node-operator surface: the sanctions screenings a human still owes a decision.
 *
 * A person here passed identity verification and is held only by the AML answer.
 * Until an operator records a decision they read as under review to their tenant
 * and every value action stays closed, so an untouched queue is a person stranded,
 * not a queue at rest.
 */
interface AmlManualReviewItem {
    userId: string;
    tenantId?: string | null;
    /** Null when nothing has been screened; there is then nothing to supersede. */
    screeningId?: string | null;
    /** Azoa's own verdict, never the provider's raw listing payload. */
    decision: "NOT_SCREENED" | "MANUAL_REVIEW" | "HIT" | "NO_HIT";
    topScore: number;
    hitCount: number;
    topics: string[];
    resultsTruncated: boolean;
    screenedAt?: string | null;
    reviewDueAt?: string | null;
    overdue: boolean;
    policyVersion: string;
    /** The node enforces the same rule on the write; this only mirrors it. */
    decisionAllowed: boolean;
    reviewContact?: string | null;
    /** The exact wording the subject is shown, so this console cannot drift from it. */
    subjectNotice: string;
}

interface AmlManualReviewDecisionRequest {
    decision: "clear" | "deny";
    tenantId?: string | null;
    expectedScreeningId: string;
    reason: string;
}

const AML_REVIEW_PATH = "kyc/aml-reviews";

/** Milliseconds until the operator's own deadline; null when no clock is running. */
function millisecondsUntilDue(value: string | null | undefined) {
    if (!value) return null;
    const due = new Date(value).getTime();
    return Number.isNaN(due) ? null : due - Date.now();
}

/**
 * Nearest deadline first, so the person closest to being stranded is at the top.
 * A row with no deadline has never been screened at all, which is a worse state
 * than a late review, so it sorts to the top rather than out of sight at the end.
 */
function byUrgency(left: AmlManualReviewItem, right: AmlManualReviewItem) {
    const a = millisecondsUntilDue(left.reviewDueAt);
    const b = millisecondsUntilDue(right.reviewDueAt);
    if (a === null && b === null) return 0;
    if (a === null) return -1;
    if (b === null) return 1;
    return a - b;
}

function reviewKey(item: AmlManualReviewItem) {
    return `${item.userId}:${item.tenantId ?? "node"}`;
}

function ComplianceReviewDesk({
    reloadToken,
    onLoadingChange,
}: {
    reloadToken: number;
    onLoadingChange: (loading: boolean) => void;
}) {
    const [queue, setQueue] = useState<AmlManualReviewItem[]>([]);
    const [nextCursor, setNextCursor] = useState<string | null>(null);
    const [deciding, setDeciding] = useState<AmlManualReviewItem | null>(null);
    const [loading, setLoading] = useState(true);
    const [notWired, setNotWired] = useState(false);
    const [forbidden, setForbidden] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const load = useCallback(
        async ({
            cursor,
            append = false,
        }: { cursor?: string; append?: boolean } = {}) => {
            setLoading(true);
            onLoadingChange(true);
            setError(null);
            setNotWired(false);
            setForbidden(false);
            try {
                const params = new URLSearchParams({ limit: "50" });
                if (cursor) params.set("cursor", cursor);
                const page = await operatorRequest<
                    CursorPage<AmlManualReviewItem>
                >(`${AML_REVIEW_PATH}?${params.toString()}`);
                setQueue((current) =>
                    [...(append ? current : []), ...page.items].sort(byUrgency),
                );
                setNextCursor(page.nextCursor ?? null);
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
                        : "The compliance review queue could not be loaded.",
                );
            } finally {
                setLoading(false);
                onLoadingChange(false);
            }
        },
        [onLoadingChange],
    );

    useEffect(() => {
        void load();
    }, [load, reloadToken]);

    const overdueCount = queue.filter((item) => item.overdue).length;
    const neverScreenedCount = queue.filter(
        (item) => item.decision === "NOT_SCREENED",
    ).length;
    // A node with no contact configured tells people "support will confirm the
    // outcome" and names nobody. That is the failure this desk exists to prevent.
    const missingContact = queue.some((item) => !item.reviewContact);

    return (
        <section aria-labelledby="compliance-review-desk" className="space-y-4">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
                <div>
                    <h2
                        id="compliance-review-desk"
                        className="text-lg font-semibold"
                    >
                        Compliance review queue
                    </h2>
                    <p className="mt-1 max-w-2xl text-sm leading-6 text-muted-foreground">
                        People whose identity verified but whose sanctions
                        screening needs a human decision. A name collision is the
                        common case; leaving it undecided is what strands a
                        legitimate person.
                    </p>
                </div>
                <Button
                    variant="outline"
                    className="min-h-11 shrink-0 self-start rounded-none"
                    onClick={() => void load()}
                    disabled={loading}
                >
                    <RefreshCw
                        className={`mr-2 h-4 w-4 ${loading ? "animate-spin" : ""}`}
                        aria-hidden="true"
                    />
                    Refresh queue
                </Button>
            </div>

            {!notWired && !forbidden && (
                <div className="flex flex-wrap gap-2">
                    <StatusPill tone={queue.length > 0 ? "attention" : "ready"}>
                        {queue.length} awaiting a decision
                    </StatusPill>
                    {overdueCount > 0 && (
                        <StatusPill tone="attention">
                            {overdueCount} past the deadline
                        </StatusPill>
                    )}
                    {neverScreenedCount > 0 && (
                        <StatusPill tone="attention">
                            {neverScreenedCount} never screened
                        </StatusPill>
                    )}
                </div>
            )}

            {error && <OperatorError message={error} onRetry={() => void load()} />}

            {notWired && (
                <div className="border border-amber-700 bg-amber-50 p-4 text-sm leading-6 text-amber-950 dark:bg-amber-950/30 dark:text-amber-100">
                    <p className="font-medium">
                        This console cannot reach the AML review route.
                    </p>
                    <p className="mt-1">
                        Flagged people still project as{" "}
                        <strong>under review</strong> and stay closed to value, so
                        nothing here fails open — but nobody can clear them until
                        the path resolves end to end.
                    </p>
                    <p className="mt-2 font-mono text-xs">
                        GET api/operator/{AML_REVIEW_PATH} · POST
                        api/operator/{AML_REVIEW_PATH}/{"{userId}"}/decision
                    </p>
                    <p className="mt-2">
                        The node mounts both on{" "}
                        <code>Controllers/AmlReviewController.cs</code>. A 404 with
                        the node running therefore points at the operator BFF
                        allow-list in{" "}
                        <code>app/api/operator/[...path]/route.ts</code>, which
                        refuses any path it does not name.
                    </p>
                </div>
            )}

            {forbidden && (
                <div className="border border-dashed p-5 text-sm leading-6 text-muted-foreground">
                    A compliance decision needs a current node-operator session.
                    Sign in again to see this queue.
                </div>
            )}

            {missingContact && !notWired && !forbidden && (
                <div className="border border-amber-700 bg-amber-50 p-4 text-sm leading-6 text-amber-950 dark:bg-amber-950/30 dark:text-amber-100">
                    <p className="font-medium">
                        No review contact is configured on this node.
                    </p>
                    <p className="mt-1">
                        People held here are told support will confirm the outcome,
                        and no one is named. Set{" "}
                        <code>Kyc:ManualReviewContact</code> so a person caught by a
                        sanctions-name collision has somebody to ask.
                    </p>
                </div>
            )}

            {loading && queue.length === 0 && !notWired && !forbidden && (
                <OperatorLoading label="Loading the compliance review queue" />
            )}

            {!notWired && !forbidden && (
                <div className="grid gap-4 lg:grid-cols-2">
                    {queue.map((item) => (
                        <ComplianceReviewCard
                            key={reviewKey(item)}
                            item={item}
                            onDecide={() => setDeciding(item)}
                        />
                    ))}
                    {!loading && queue.length === 0 && (
                        <div className="border border-dashed border-border p-8 text-center lg:col-span-2">
                            <UserCheck
                                className="mx-auto h-8 w-8 text-emerald-700"
                                aria-hidden="true"
                            />
                            <p className="mt-3 font-medium">
                                No one is waiting on a compliance decision
                            </p>
                            <p className="mt-1 text-sm text-muted-foreground">
                                Every verified person on this node has a clear
                                screening verdict.
                            </p>
                        </div>
                    )}
                </div>
            )}

            {nextCursor && !notWired && !forbidden && (
                <div className="flex justify-center border-t pt-5">
                    <Button
                        variant="outline"
                        className="min-h-11 rounded-none"
                        disabled={loading}
                        onClick={() =>
                            void load({ cursor: nextCursor, append: true })
                        }
                    >
                        {loading ? "Loading more..." : "Load more people"}
                    </Button>
                </div>
            )}

            {deciding && (
                <ComplianceDecisionDialog
                    item={deciding}
                    onClose={() => setDeciding(null)}
                    onDecided={async () => {
                        setDeciding(null);
                        await load();
                    }}
                />
            )}
        </section>
    );
}

function ComplianceReviewCard({
    item,
    onDecide,
}: {
    item: AmlManualReviewItem;
    onDecide: () => void;
}) {
    const neverScreened = item.decision === "NOT_SCREENED";
    return (
        <FlatCard>
            <CardHeader>
                <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                        <p className="font-mono text-[10px] uppercase tracking-[0.14em] text-muted-foreground">
                            Person {item.userId.slice(0, 8)}…
                        </p>
                        <CardTitle className="mt-1 flex items-center gap-2 text-base">
                            <ShieldAlert
                                className="h-4 w-4 text-amber-700"
                                aria-hidden="true"
                            />
                            Awaiting a decision
                        </CardTitle>
                    </div>
                    <StatusPill tone={item.overdue ? "attention" : "neutral"}>
                        {item.overdue
                            ? "Past the deadline"
                            : neverScreened
                              ? "Never screened"
                              : item.decision}
                    </StatusPill>
                </div>
            </CardHeader>
            <CardContent className="space-y-4 text-sm">
                <dl className="grid gap-3 sm:grid-cols-2">
                    <QueueDatum
                        label="Person"
                        value={`${item.userId.slice(0, 8)}…`}
                        title={item.userId}
                    />
                    <QueueDatum
                        label="Tenant"
                        value={
                            item.tenantId
                                ? `${item.tenantId.slice(0, 8)}…`
                                : "Direct"
                        }
                        title={item.tenantId ?? undefined}
                    />
                    <QueueDatum
                        label="Screened"
                        value={
                            neverScreened
                                ? "Never"
                                : formatOperatorDate(item.screenedAt)
                        }
                    />
                    <QueueDatum
                        label="Decision due"
                        value={
                            item.reviewDueAt
                                ? formatOperatorDate(item.reviewDueAt)
                                : "No clock running"
                        }
                    />
                </dl>

                {neverScreened ? (
                    <p className="border border-amber-700 bg-amber-50 p-3 text-xs leading-5 text-amber-950 dark:bg-amber-950/30 dark:text-amber-100">
                        This person verified their identity and was never screened,
                        so there is no verdict to supersede and no deadline running.
                        Nothing can be decided here: the screening service has to run
                        for them first.
                    </p>
                ) : (
                    <p className="text-xs leading-5 text-muted-foreground">
                        {item.hitCount} candidate
                        {item.hitCount === 1 ? "" : "s"}, top score {item.topScore}
                        {item.resultsTruncated
                            ? " — the provider's list was incomplete, so it can never read clear on its own"
                            : ""}
                        .
                    </p>
                )}

                {item.topics.length > 0 && (
                    <p className="flex flex-wrap gap-2 text-xs text-muted-foreground">
                        {item.topics.map((topic) => (
                            <span key={topic} className="border px-2 py-1">
                                {topic}
                            </span>
                        ))}
                    </p>
                )}

                <p className="border bg-muted/40 p-3 text-xs leading-5 text-muted-foreground">
                    <span className="block font-medium text-foreground">
                        What this person is being told
                    </span>
                    <span className="mt-1 block">{item.subjectNotice}</span>
                </p>

                <div className="flex justify-end border-t pt-3">
                    <Button
                        className="min-h-11 rounded-none"
                        onClick={onDecide}
                        disabled={!item.decisionAllowed}
                    >
                        <ShieldQuestion
                            className="mr-2 h-4 w-4"
                            aria-hidden="true"
                        />
                        {item.decisionAllowed
                            ? "Record a decision"
                            : "Nothing to decide"}
                    </Button>
                </div>
            </CardContent>
        </FlatCard>
    );
}

function ComplianceDecisionDialog({
    item,
    onClose,
    onDecided,
}: {
    item: AmlManualReviewItem;
    onClose: () => void;
    onDecided: () => Promise<void>;
}) {
    const [decision, setDecision] = useState<"clear" | "deny" | "">("");
    const [reason, setReason] = useState("");
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState<string | null>(null);

    async function decide() {
        if (!decision || !reason.trim() || !item.screeningId) return;
        setSaving(true);
        setError(null);
        const request: AmlManualReviewDecisionRequest = {
            decision,
            tenantId: item.tenantId ?? undefined,
            expectedScreeningId: item.screeningId,
            reason: reason.trim(),
        };
        try {
            await operatorRequest<AmlManualReviewItem>(
                `${AML_REVIEW_PATH}/${encodeURIComponent(item.userId)}/decision`,
                { method: "POST", body: request },
            );
            await onDecided();
        } catch (cause) {
            setError(
                cause instanceof OperatorRequestError && cause.status === 409
                    ? "A newer screening arrived while this was open. Close and refresh the queue, then read the current verdict before deciding."
                    : cause instanceof Error
                      ? cause.message
                      : "The decision could not be recorded.",
            );
        } finally {
            setSaving(false);
        }
    }

    return (
        <Dialog
            open
            onOpenChange={(open) => {
                if (!open && !saving) onClose();
            }}
        >
            <DialogContent className="max-h-[calc(100vh-2rem)] overflow-y-auto rounded-none sm:max-w-lg">
                <DialogHeader>
                    <DialogTitle>Compliance decision</DialogTitle>
                    <DialogDescription>
                        This supersedes the screening verdict you just read with a
                        new one. Clearing re-opens every value action for this
                        person. Your operator identity is recorded automatically.
                    </DialogDescription>
                </DialogHeader>

                <fieldset className="space-y-3">
                    <legend className="text-sm font-medium">Decision</legend>
                    <ComplianceDecisionChoice
                        value="clear"
                        selected={decision}
                        onSelect={setDecision}
                        title="Clear this person"
                        description="The match is not them, or the finding does not bar them. Value actions re-open."
                    />
                    <ComplianceDecisionChoice
                        value="deny"
                        selected={decision}
                        onSelect={setDecision}
                        title="Keep closed"
                        description="The finding stands. Value actions stay closed and the verdict is recorded as adverse."
                    />
                </fieldset>

                <div className="space-y-2">
                    <Label htmlFor="compliance-reason">
                        Why (recorded with the decision)
                    </Label>
                    <Textarea
                        id="compliance-reason"
                        value={reason}
                        onChange={(event) => setReason(event.target.value)}
                        maxLength={2_000}
                        required
                        className="min-h-24 rounded-none"
                        placeholder="State what you checked and what it showed. Do not paste listing contents."
                    />
                </div>

                {error && <OperatorError message={error} />}
                <DialogFooter className="rounded-none">
                    <Button
                        variant="outline"
                        className="min-h-11 rounded-none"
                        onClick={onClose}
                        disabled={saving}
                    >
                        Cancel
                    </Button>
                    <Button
                        variant={decision === "deny" ? "destructive" : "default"}
                        className="min-h-11 rounded-none"
                        onClick={() => void decide()}
                        disabled={saving || !decision || !reason.trim()}
                    >
                        {decision === "clear" && (
                            <ShieldCheck
                                className="mr-2 h-4 w-4"
                                aria-hidden="true"
                            />
                        )}
                        {saving
                            ? "Recording decision…"
                            : decision === "deny"
                              ? "Confirm and keep closed"
                              : decision === "clear"
                                ? "Confirm and clear"
                                : "Choose a decision"}
                    </Button>
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}

function ComplianceDecisionChoice({
    value,
    selected,
    onSelect,
    title,
    description,
}: {
    value: "clear" | "deny";
    selected: string;
    onSelect: (value: "clear" | "deny") => void;
    title: string;
    description: string;
}) {
    return (
        <label className="flex min-h-16 cursor-pointer items-start gap-3 border p-3 has-[:checked]:border-primary has-[:checked]:bg-primary/5">
            <input
                type="radio"
                name="compliance-decision"
                value={value}
                checked={selected === value}
                onChange={() => onSelect(value)}
                className="mt-1 h-5 w-5 accent-primary"
            />
            <span>
                <span className="block font-medium">{title}</span>
                <span className="mt-1 block text-xs leading-5 text-muted-foreground">
                    {description}
                </span>
            </span>
        </label>
    );
}
