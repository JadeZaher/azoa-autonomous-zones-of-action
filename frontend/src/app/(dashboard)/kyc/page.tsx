"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { ExternalLink, FileCheck2, RefreshCw, ShieldCheck } from "lucide-react";
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
import type {
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
