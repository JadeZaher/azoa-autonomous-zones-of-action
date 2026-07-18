"use client";

import { useCallback, useEffect, useState } from "react";
import { ShieldQuestion, UserCheck } from "lucide-react";
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
                eyebrow="Development verification"
                title="Manual simulation queue"
                description="Act only on Development-authorized simulation rows. External provider outcomes remain provider-managed; payloads, documents, and sensitive identity data are never exposed here."
                onRefresh={() => void load()}
                refreshing={loading}
            />
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
                        label="Avatar"
                        value={`${submission.avatarId.slice(0, 8)}…`}
                        title={submission.avatarId}
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
