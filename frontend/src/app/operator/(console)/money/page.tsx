"use client";

import { useCallback, useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { AlertCircle, Inbox, Wallet } from "lucide-react";
import { CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
    FlatCard,
    MetricCard,
    OperatorError,
    OperatorLoading,
    OperatorPageHeader,
    StatusPill,
    formatOperatorDate,
} from "@/components/operator/operator-ui";
import { operatorRequest } from "@/lib/operator-client";
import type {
    OperatorFiatEvent,
    OperatorOnRampLot,
} from "@/lib/operator-contracts";

const BOARD_LIMIT = 50;

function formatMoney(minorUnits: number, currencyCode: string): string {
    const amount = (minorUnits / 100).toLocaleString(undefined, {
        minimumFractionDigits: 2,
        maximumFractionDigits: 2,
    });
    return `${amount} ${currencyCode || ""}`.trim();
}

export default function OperatorMoneyPage() {
    const [lots, setLots] = useState<OperatorOnRampLot[]>([]);
    const [events, setEvents] = useState<OperatorFiatEvent[]>([]);
    const [loading, setLoading] = useState(true);
    const [refreshing, setRefreshing] = useState(false);
    const [reason, setReason] = useState("");
    const [refundReference, setRefundReference] = useState("");
    const [acting, setActing] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const load = useCallback(async () => {
        setError(null);
        try {
            const [lotRows, eventRows] = await Promise.all([
                operatorRequest<OperatorOnRampLot[]>(
                    `on-ramp-lots?limit=${BOARD_LIMIT}`,
                ),
                operatorRequest<OperatorFiatEvent[]>(
                    `fiat-events?limit=${BOARD_LIMIT}`,
                ),
            ]);
            setLots(lotRows ?? []);
            setEvents(eventRows ?? []);
        } catch (cause) {
            setError(
                cause instanceof Error
                    ? cause.message
                    : "The money boards could not be read.",
            );
        } finally {
            setLoading(false);
            setRefreshing(false);
        }
    }, []);

    useEffect(() => {
        void load();
    }, [load]);

    async function recover(path: string, body: unknown) {
        setActing(true);
        setError(null);
        try {
            await operatorRequest(path, { method: "POST", body });
            await load();
        } catch (cause) {
            setError(cause instanceof Error ? cause.message : "Recovery could not complete.");
        } finally { setActing(false); }
    }

    const outOfPocket = lots.filter((lot) => lot.chargedAndUncredited).length;
    const needAPerson = lots.filter((lot) => lot.needsAPersonToMoveIt).length;
    const abandoned = events.filter(
        (event) => event.providerWasToldToStopRetrying,
    ).length;

    return (
        <div className="flex flex-col gap-8">
            <OperatorPageHeader
                eyebrow="Money"
                title="Payments nothing finished"
                description="Every row here is money this node confirmed as taken. A lot that is neither credited nor given back is somebody who paid and holds nothing for it, and unfinished deliveries are retried automatically. Operator recovery preserves the original issuance and refund keys."
                onRefresh={() => {
                    setRefreshing(true);
                    void load();
                }}
                refreshing={refreshing}
            />

            {error && <OperatorError message={error} onRetry={() => void load()} />}

            {loading ? (
                <OperatorLoading label="Reading the money boards" />
            ) : (
                <>
                    <div className="grid gap-4 sm:grid-cols-3">
                        <MetricCard
                            label="Out of pocket"
                            value={outOfPocket}
                            detail="Paid, and holding nothing for it"
                        />
                        <MetricCard
                            label="Waiting on a person"
                            value={needAPerson}
                            detail="Parked where the node will never move them"
                        />
                        <MetricCard
                            label="Deliveries abandoned"
                            value={abandoned}
                            detail="The provider was told to stop retrying"
                        />
                    </div>

                    <FlatCard>
                        <CardHeader>
                            <CardTitle className="flex items-center gap-2 text-base">
                                <Wallet className="h-4 w-4" aria-hidden="true" />
                                Funding that never became funds
                            </CardTitle>
                        </CardHeader>
                        <CardContent className="p-0">
                            {lots.length === 0 ? (
                                <p className="px-6 pb-6 text-sm text-muted-foreground">
                                    Nobody is owed anything on this node right now.
                                </p>
                            ) : (
                                <div className="overflow-x-auto">
                                    <table className="w-full text-left text-sm">
                                        <thead className="border-y border-border text-xs uppercase tracking-wide text-muted-foreground">
                                            <tr>
                                                <th className="px-6 py-3 font-medium">Payment</th>
                                                <th className="px-6 py-3 font-medium">Money taken</th>
                                                <th className="px-6 py-3 font-medium">Status</th>
                                                <th className="px-6 py-3 font-medium">Why</th>
                                                <th className="px-6 py-3 font-medium">Taken</th>
                                                <th className="px-6 py-3 font-medium">Recovery</th>
                                            </tr>
                                        </thead>
                                        <tbody>
                                            {lots.map((lot) => (
                                                <tr key={lot.id} className="border-b border-border/60">
                                                    <td className="px-6 py-3 font-mono text-xs">{lot.intentId}</td>
                                                    <td className="px-6 py-3 tabular-nums">
                                                        {formatMoney(lot.fiatMinorUnits, lot.currencyCode)}
                                                    </td>
                                                    <td className="px-6 py-3">
                                                        <StatusPill
                                                            tone={lot.chargedAndUncredited ? "attention" : "neutral"}
                                                        >
                                                            {lot.status}
                                                        </StatusPill>
                                                    </td>
                                                    <td className="px-6 py-3 text-muted-foreground">
                                                        {lot.statusReason ?? "—"}
                                                    </td>
                                                    <td className="px-6 py-3 text-muted-foreground">
                                                        {formatOperatorDate(lot.createdAt)}
                                                    </td>
                                                    <td className="px-6 py-3 space-x-2">
                                                        <Button size="sm" disabled={acting || !reason.trim() || lot.status === "RefundPending"} onClick={() => void recover(`on-ramp-lots/intents/${lot.intentId}/resume`, { reason })}>Resume issuance</Button>
                                                        <Button size="sm" variant="outline" disabled={acting || !reason.trim()} onClick={() => void recover(`on-ramp-lots/intents/${lot.intentId}/refund`, { reason, ...(refundReference.trim() ? { providerRefundId: refundReference.trim() } : {}) })}>Refund payment</Button>
                                                        {['failed', 'canceled'].includes(lot.providerRefundStatus ?? '') && <Button size="sm" variant="outline" disabled={acting || !reason.trim()} onClick={() => void recover(`on-ramp-lots/intents/${lot.intentId}/refund`, { reason, retryFailed: true, expectedRefundAttempt: lot.refundAttempt })}>Retry failed refund</Button>}
                                                    </td>
                                                </tr>
                                            ))}
                                        </tbody>
                                    </table>
                                </div>
                            )}
                        </CardContent>
                    </FlatCard>

                    <FlatCard>
                        <CardHeader>
                            <CardTitle className="flex items-center gap-2 text-base">
                                <Inbox className="h-4 w-4" aria-hidden="true" />
                                Payment notifications nothing finished
                            </CardTitle>
                        </CardHeader>
                        <CardContent className="p-0">
                            {events.length === 0 ? (
                                <p className="px-6 pb-6 text-sm text-muted-foreground">
                                    Every delivery this node received was finished.
                                </p>
                            ) : (
                                <div className="overflow-x-auto">
                                    <table className="w-full text-left text-sm">
                                        <thead className="border-y border-border text-xs uppercase tracking-wide text-muted-foreground">
                                            <tr>
                                                <th className="px-6 py-3 font-medium">Delivery</th>
                                                <th className="px-6 py-3 font-medium">Kind</th>
                                                <th className="px-6 py-3 font-medium">Status</th>
                                                <th className="px-6 py-3 font-medium">Attempts</th>
                                                <th className="px-6 py-3 font-medium">Recovery</th>
                                                <th className="px-6 py-3 font-medium">Last error</th>
                                            </tr>
                                        </thead>
                                        <tbody>
                                            {events.map((event) => (
                                                <tr key={event.id} className="border-b border-border/60">
                                                    <td className="px-6 py-3 font-mono text-xs">
                                                        {event.providerEventId}
                                                    </td>
                                                    <td className="px-6 py-3">{event.eventType}</td>
                                                    <td className="px-6 py-3">
                                                        <StatusPill
                                                            tone={
                                                                event.providerWasToldToStopRetrying
                                                                    ? "attention"
                                                                    : "neutral"
                                                            }
                                                        >
                                                            {event.status}
                                                        </StatusPill>
                                                    </td>
                                                    <td className="px-6 py-3 tabular-nums">{event.attemptCount}</td>
                                                    <td className="px-6 py-3"><Button size="sm" disabled={acting} onClick={() => void recover(`fiat-events/${event.id}/replay`, {})}>Replay delivery</Button></td>
                                                    <td className="px-6 py-3 text-muted-foreground">
                                                        {event.lastError ?? "—"}
                                                    </td>
                                                </tr>
                                            ))}
                                        </tbody>
                                    </table>
                                </div>
                            )}
                        </CardContent>
                    </FlatCard>

                    <p className="flex items-start gap-2 text-xs leading-5 text-muted-foreground">
                        <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                        These boards are read-only. Crediting or refunding a parked payment moves
                        real money and is done deliberately, not from a list.
                    </p>
                </>
            )}
        </div>
    );
}
