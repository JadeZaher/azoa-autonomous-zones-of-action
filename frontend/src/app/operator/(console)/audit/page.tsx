"use client";

import { FormEvent, useCallback, useEffect, useState } from "react";
import { ArrowRight, ChevronDown, Filter, History, RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
    FlatCard,
    OperatorError,
    OperatorLoading,
    OperatorPageHeader,
    StatusPill,
    formatOperatorDate,
} from "@/components/operator/operator-ui";
import { operatorRequest } from "@/lib/operator-client";
import type {
    CursorPage,
    KycControlAuditAction,
    KycControlAuditResponse,
} from "@/lib/operator-contracts";

type AuditFilters = {
    tenantId: string;
    providerKey: string;
    action: KycControlAuditAction | "";
};

type AuditValue = string | number | boolean | null | undefined;

type AuditChange = {
    label: string;
    previous: AuditValue;
    current: AuditValue;
};

const EMPTY_FILTERS: AuditFilters = {
    tenantId: "",
    providerKey: "",
    action: "",
};

export default function OperatorAuditPage() {
    const [entries, setEntries] = useState<KycControlAuditResponse[]>([]);
    const [draftFilters, setDraftFilters] = useState<AuditFilters>(EMPTY_FILTERS);
    const [appliedFilters, setAppliedFilters] =
        useState<AuditFilters>(EMPTY_FILTERS);
    const [nextCursor, setNextCursor] = useState<string | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    const load = useCallback(
        async (
            filters: AuditFilters,
            {
                cursor,
                append = false,
                preserveExisting = false,
            }: {
                cursor?: string;
                append?: boolean;
                preserveExisting?: boolean;
            } = {},
        ) => {
            setLoading(true);
            setError(null);
            if (!append && !preserveExisting) {
                setEntries([]);
                setNextCursor(null);
            }
            try {
                const params = new URLSearchParams({ limit: "25" });
                if (cursor) params.set("cursor", cursor);
                if (filters.tenantId) params.set("tenantId", filters.tenantId);
                if (filters.providerKey) {
                    params.set("providerKey", filters.providerKey);
                }
                if (filters.action) params.set("action", filters.action);

                const page = await operatorRequest<
                    CursorPage<KycControlAuditResponse>
                >(`kyc/audit?${params.toString()}`);
                setEntries((current) =>
                    append ? [...current, ...page.items] : page.items,
                );
                setNextCursor(page.nextCursor ?? null);
            } catch (reason) {
                setError(
                    reason instanceof Error
                        ? reason.message
                        : "The KYC control history could not be loaded.",
                );
            } finally {
                setLoading(false);
            }
        },
        [],
    );

    useEffect(() => {
        void load(EMPTY_FILTERS);
    }, [load]);

    function applyFilters(event: FormEvent<HTMLFormElement>) {
        event.preventDefault();
        const filters: AuditFilters = {
            tenantId: draftFilters.tenantId.trim(),
            providerKey: draftFilters.providerKey.trim(),
            action: draftFilters.action,
        };
        setDraftFilters(filters);
        setAppliedFilters(filters);
        void load(filters);
    }

    function clearFilters() {
        const filters = { ...EMPTY_FILTERS };
        setDraftFilters(filters);
        setAppliedFilters(filters);
        void load(filters);
    }

    const filtered = hasActiveFilters(appliedFilters);
    const suppressEmptyState = Boolean(error && entries.length === 0);

    return (
        <div className="space-y-6">
            <OperatorPageHeader
                eyebrow="Control history"
                title="KYC audit trail"
                description="Review who changed tenant assignments and provider policy. This view contains control metadata only; credentials, provider payloads, and identity evidence never appear here."
                onRefresh={() =>
                    void load(appliedFilters, { preserveExisting: true })
                }
                refreshing={loading}
                refreshLabel="Refresh history"
            />

            <form
                onSubmit={applyFilters}
                className="border border-border bg-card p-4 sm:p-5"
                aria-label="Filter KYC audit history"
            >
                <div className="flex items-center gap-2">
                    <Filter className="h-4 w-4 text-primary" aria-hidden="true" />
                    <h2 className="font-mono text-xs uppercase tracking-[0.14em]">
                        Filter this record
                    </h2>
                </div>
                <div className="mt-4 grid gap-4 md:grid-cols-3">
                    <div className="space-y-2">
                        <Label htmlFor="audit-tenant">Tenant id</Label>
                        <Input
                            id="audit-tenant"
                            value={draftFilters.tenantId}
                            onChange={(event) =>
                                setDraftFilters({
                                    ...draftFilters,
                                    tenantId: event.target.value,
                                })
                            }
                            placeholder="00000000-0000-0000-0000-000000000000"
                            pattern="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
                            maxLength={36}
                            autoCapitalize="none"
                            autoComplete="off"
                            className="min-h-11 rounded-none font-mono text-xs"
                        />
                    </div>
                    <div className="space-y-2">
                        <Label htmlFor="audit-provider">Provider key</Label>
                        <Input
                            id="audit-provider"
                            value={draftFilters.providerKey}
                            onChange={(event) =>
                                setDraftFilters({
                                    ...draftFilters,
                                    providerKey: event.target.value,
                                })
                            }
                            placeholder="veriff"
                            pattern="[a-z0-9][a-z0-9_-]{0,63}"
                            maxLength={64}
                            autoCapitalize="none"
                            autoComplete="off"
                            className="min-h-11 rounded-none font-mono text-xs"
                        />
                    </div>
                    <div className="space-y-2">
                        <Label htmlFor="audit-action">Action</Label>
                        <select
                            id="audit-action"
                            value={draftFilters.action}
                            onChange={(event) =>
                                setDraftFilters({
                                    ...draftFilters,
                                    action: event.target.value as AuditFilters["action"],
                                })
                            }
                            className="min-h-11 w-full rounded-none border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                        >
                            <option value="">All control changes</option>
                            <option value="profile.trust-change">
                                Provider trust change
                            </option>
                            <option value="profile.metadata-change">
                                Provider metadata change
                            </option>
                            <option value="tenant.provider-selection">
                                Tenant provider selection
                            </option>
                        </select>
                    </div>
                </div>
                <div className="mt-4 flex flex-col gap-2 border-t border-border pt-4 sm:flex-row sm:justify-end">
                    <Button
                        type="button"
                        variant="ghost"
                        className="min-h-11 rounded-none"
                        onClick={clearFilters}
                        disabled={loading || (!filtered && !hasActiveFilters(draftFilters))}
                    >
                        <RotateCcw className="mr-2 h-4 w-4" aria-hidden="true" />
                        Clear filters
                    </Button>
                    <Button
                        type="submit"
                        className="min-h-11 rounded-none"
                        disabled={loading}
                    >
                        <Filter className="mr-2 h-4 w-4" aria-hidden="true" />
                        Apply filters
                    </Button>
                </div>
            </form>

            <div className="flex flex-wrap items-center gap-2" aria-live="polite">
                <StatusPill tone="neutral">
                    {entries.length} {entries.length === 1 ? "event" : "events"} loaded
                </StatusPill>
                {filtered && <StatusPill tone="attention">Filtered view</StatusPill>}
                <p className="text-xs text-muted-foreground">
                    Server-issued pagination tokens stay opaque. Refresh to
                    request current history.
                </p>
            </div>

            {error && (
                <OperatorError
                    message={error}
                    onRetry={() =>
                        void load(appliedFilters, {
                            preserveExisting: entries.length > 0,
                        })
                    }
                />
            )}

            {loading && entries.length === 0 ? (
                <OperatorLoading label="Loading KYC control history" />
            ) : suppressEmptyState ? null : entries.length > 0 ? (
                <ol className="grid gap-4 lg:grid-cols-2" aria-label="KYC control events">
                    {entries.map((entry) => (
                        <li key={entry.id}>
                            <AuditEntry entry={entry} />
                        </li>
                    ))}
                </ol>
            ) : (
                <div className="border border-dashed border-border p-8 text-center">
                    <History
                        className="mx-auto h-8 w-8 text-muted-foreground"
                        aria-hidden="true"
                    />
                    <p className="mt-3 font-medium">
                        {filtered
                            ? "No control changes match these filters"
                            : "No KYC control changes have been recorded"}
                    </p>
                    <p className="mt-1 text-sm text-muted-foreground">
                        {filtered
                            ? "Clear a filter or check the exact tenant and provider identifiers."
                            : "Provider policy and tenant assignment changes will appear here."}
                    </p>
                </div>
            )}

            {nextCursor && (
                <div className="flex justify-center border-t border-border pt-5">
                    <Button
                        variant="outline"
                        className="min-h-11 rounded-none"
                        disabled={loading}
                        onClick={() =>
                            void load(appliedFilters, {
                                cursor: nextCursor,
                                append: true,
                            })
                        }
                    >
                        {loading ? "Loading older activity..." : "Load older activity"}
                    </Button>
                </div>
            )}
        </div>
    );
}

function AuditEntry({ entry }: { entry: KycControlAuditResponse }) {
    const changes = auditChanges(entry);
    const knownAction = isKnownAuditAction(entry.action);
    const provider = entry.providerKey ?? entry.previousProviderKey ?? "Not recorded";

    return (
        <FlatCard className="h-full">
            <CardHeader>
                <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                    <div className="min-w-0">
                        <p className="font-mono text-[10px] uppercase tracking-[0.14em] text-muted-foreground">
                            Revision {entry.version}
                        </p>
                        <CardTitle className="mt-1 text-base">
                            {actionLabel(entry.action)}
                        </CardTitle>
                    </div>
                    <StatusPill
                        tone={
                            entry.action === "profile.trust-change" || !knownAction
                                ? "attention"
                                : "neutral"
                        }
                    >
                        {!knownAction
                            ? "Unknown type"
                            : entry.action === "tenant.provider-selection"
                              ? "Tenant"
                              : "Provider"}
                    </StatusPill>
                </div>
            </CardHeader>
            <CardContent className="space-y-4 text-sm">
                <dl className="grid gap-3 border-y border-border py-3 sm:grid-cols-2">
                    <AuditDatum label="Provider" value={provider} />
                    <ReferenceDatum
                        label="Tenant"
                        value={entry.tenantId}
                        emptyLabel="Node-wide policy"
                    />
                    <ReferenceDatum
                        label="Changed by"
                        value={entry.actorAvatarId}
                        emptyLabel="Actor not recorded"
                    />
                    <AuditDatum
                        label="Recorded"
                        value={formatOperatorDate(entry.occurredAt)}
                    />
                </dl>

                {!knownAction && (
                    <p
                        role="status"
                        className="border border-amber-700 bg-amber-50 p-3 text-xs leading-5 text-amber-950 dark:bg-amber-950/30 dark:text-amber-100"
                    >
                        This node returned an unsupported audit action,{" "}
                        <code className="break-all">
                            {entry.action || "blank"}
                        </code>. Review API compatibility before interpreting its
                        fields.
                    </p>
                )}

                <div>
                    <h3 className="font-mono text-[10px] uppercase tracking-[0.14em] text-muted-foreground">
                        Recorded delta
                    </h3>
                    {changes.length > 0 ? (
                        <ul className="mt-3 space-y-2">
                            {changes.map((change) => (
                                <li
                                    key={change.label}
                                    className="grid gap-1 border-l-2 border-primary/60 bg-muted/40 p-3 sm:grid-cols-[8rem_1fr] sm:items-center"
                                >
                                    <span className="text-xs font-medium">
                                        {change.label}
                                    </span>
                                    <span className="flex min-w-0 items-center gap-2 font-mono text-xs">
                                        <span className="min-w-0 break-all text-muted-foreground line-through">
                                            {formatAuditValue(change.previous)}
                                        </span>
                                        <ArrowRight
                                            className="h-3.5 w-3.5 shrink-0 text-primary"
                                            aria-hidden="true"
                                        />
                                        <span className="sr-only">changed to</span>
                                        <span className="min-w-0 break-all">
                                            {formatAuditValue(change.current)}
                                        </span>
                                    </span>
                                </li>
                            ))}
                        </ul>
                    ) : (
                        <p className="mt-2 text-xs leading-5 text-muted-foreground">
                            This event records a control-plane action without an
                            additional displayable field delta.
                        </p>
                    )}
                </div>
            </CardContent>
        </FlatCard>
    );
}

function AuditDatum({
    label,
    value,
}: {
    label: string;
    value: string;
}) {
    return (
        <div>
            <dt className="text-xs text-muted-foreground">{label}</dt>
            <dd className="mt-1 break-all font-mono text-xs">
                {value}
            </dd>
        </div>
    );
}

function ReferenceDatum({
    label,
    value,
    emptyLabel,
}: {
    label: string;
    value?: string | null;
    emptyLabel: string;
}) {
    if (!value) return <AuditDatum label={label} value={emptyLabel} />;

    return (
        <div>
            <dt className="text-xs text-muted-foreground">{label}</dt>
            <dd className="mt-1">
                <details className="group border border-border bg-muted/30">
                    <summary className="flex min-h-11 cursor-pointer list-none items-center justify-between gap-2 px-3 font-mono text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
                        <span>{shortReference(value)}</span>
                        <span className="flex items-center gap-1 text-primary">
                            Full ID
                            <ChevronDown
                                className="h-3.5 w-3.5 transition-transform group-open:rotate-180"
                                aria-hidden="true"
                            />
                        </span>
                    </summary>
                    <code className="block break-all border-t border-border px-3 py-2 text-xs">
                        {value}
                    </code>
                </details>
            </dd>
        </div>
    );
}

function auditChanges(entry: KycControlAuditResponse): AuditChange[] {
    if (!isKnownAuditAction(entry.action)) return [];

    const changes: AuditChange[] = entry.action === "tenant.provider-selection"
        ? [
              {
                  label: "Provider",
                  previous: entry.previousProviderKey,
                  current: entry.providerKey,
              },
          ]
        : [
              {
                  label: "Display name",
                  previous: entry.previousDisplayName,
                  current: entry.displayName,
              },
              {
                  label: "Adapter",
                  previous: entry.previousAdapterKey,
                  current: entry.adapterKey,
              },
              {
                  label: "Enabled",
                  previous: entry.previousEnabled,
                  current: entry.enabled,
              },
              {
                  label: "Policy version",
                  previous: entry.previousPolicyVersion,
                  current: entry.policyVersion,
              },
              {
                  label: "Assurance",
                  previous: entry.previousAssuranceLevel,
                  current: entry.assuranceLevel,
              },
              {
                  label: "Trust revision",
                  previous: entry.previousTrustRevision,
                  current: entry.trustRevision,
              },
          ];

    return changes.filter(
        ({ previous, current }) => (previous ?? null) !== (current ?? null),
    );
}

function actionLabel(action: string): string {
    if (action === "profile.trust-change") return "Provider trust changed";
    if (action === "profile.metadata-change") return "Provider details changed";
    if (action === "tenant.provider-selection") return "Tenant provider changed";
    return "Unknown control event";
}

function isKnownAuditAction(action: string): action is KycControlAuditAction {
    return (
        action === "profile.trust-change" ||
        action === "profile.metadata-change" ||
        action === "tenant.provider-selection"
    );
}

function formatAuditValue(value: AuditValue): string {
    if (value === null || value === undefined || value === "") return "Not set";
    if (typeof value === "boolean") return value ? "Enabled" : "Disabled";
    return String(value);
}

function shortReference(value: string): string {
    return value.length > 12 ? `${value.slice(0, 8)}...` : value;
}

function hasActiveFilters(filters: AuditFilters): boolean {
    return Boolean(filters.tenantId || filters.providerKey || filters.action);
}
