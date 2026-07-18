'use client'

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { Activity, ArrowRight, Check, Clock3, ExternalLink, ServerCog, ShieldAlert, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  FlatCard,
  MetricCard,
  OperatorError,
  OperatorLoading,
  OperatorPageHeader,
  StatusPill,
  formatOperatorDate,
} from '@/components/operator/operator-ui'
import { operatorRequest, OperatorRequestError } from '@/lib/operator-client'
import { OPERATOR_REQUEST_HEADER } from '@/lib/operator-contracts'
import type { NodeOperatorOverviewResponse } from '@/lib/operator-contracts'

export default function OperatorOverviewPage() {
  const [overview, setOverview] = useState<NodeOperatorOverviewResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [confirmRevokeAll, setConfirmRevokeAll] = useState(false)
  const [revokingAll, setRevokingAll] = useState(false)
  const [revokeError, setRevokeError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setOverview(await operatorRequest<NodeOperatorOverviewResponse>('overview'))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'The node snapshot could not be loaded.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void load() }, [load])

  async function revokeAllSessions() {
    setRevokingAll(true)
    setRevokeError(null)
    try {
      await operatorRequest<unknown>('session/revoke', { method: 'POST', body: {} })
      await fetch('/api/operator/session', {
        method: 'DELETE',
        credentials: 'same-origin',
        headers: { [OPERATOR_REQUEST_HEADER]: '1' },
      })
      window.location.assign('/operator/login')
    } catch (reason) {
      setRevokeError(reason instanceof OperatorRequestError
        ? reason.message
        : 'All operator sessions could not be revoked. No success is assumed.')
    } finally {
      setRevokingAll(false)
    }
  }

  return (
    <div className="space-y-6">
      <OperatorPageHeader
        eyebrow="Control plane"
        title="Node overview"
        description="A point-in-time view of persistence, operator authority, KYC capacity, and decisions waiting for a person."
        onRefresh={() => void load()}
        refreshing={loading}
      />
      {error && <OperatorError message={error} onRetry={() => void load()} />}
      {loading && !overview ? <OperatorLoading label="Loading node snapshot" /> : overview && (
        <>
          <section aria-labelledby="node-runtime" className="grid gap-4 lg:grid-cols-[1.15fr_0.85fr]">
            <FlatCard className="bg-[#16120D] text-[#F2EDE3]">
              <CardHeader>
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div>
                    <p className="font-mono text-[10px] uppercase tracking-[0.18em] text-[#C8501E]">Runtime</p>
                    <CardTitle id="node-runtime" className="mt-2 text-xl">{overview.node.environment} node</CardTitle>
                  </div>
                  <StatusPill tone={overview.node.persistenceReady ? 'ready' : 'attention'}>
                    {overview.node.persistenceReady ? 'Persistence ready' : 'Persistence blocked'}
                  </StatusPill>
                </div>
              </CardHeader>
              <CardContent className="grid gap-4 text-sm sm:grid-cols-2">
                <div className="border-t border-[#4b443b] pt-3">
                  <p className="text-[#9e9587]">Service version</p>
                  <p className="mt-1 break-all font-mono text-xs">{overview.node.serviceVersion}</p>
                </div>
                <div className="border-t border-[#4b443b] pt-3">
                  <p className="text-[#9e9587]">Snapshot generated</p>
                  <p className="mt-1 text-xs">{formatOperatorDate(overview.node.generatedAt)}</p>
                </div>
              </CardContent>
            </FlatCard>

            <FlatCard>
              <CardHeader>
                <p className="font-mono text-[10px] uppercase tracking-[0.18em] text-primary">Current authority</p>
                <CardTitle className="mt-1 text-lg">{overview.operator.username}</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3 text-sm">
                <div className="flex justify-between gap-4 border-t pt-3">
                  <span className="text-muted-foreground">Credential revision</span>
                  <span className="font-mono">{overview.operator.credentialRevision}</span>
                </div>
                <div className="flex justify-between gap-4 border-t pt-3">
                  <span className="text-muted-foreground">Activated</span>
                  <span className="text-right text-xs">{formatOperatorDate(overview.operator.activatedAt)}</span>
                </div>
                <div className="flex justify-between gap-4 border-t pt-3">
                  <span className="text-muted-foreground">Credentials updated</span>
                  <span className="text-right text-xs">{formatOperatorDate(overview.operator.credentialUpdatedAt)}</span>
                </div>
              </CardContent>
            </FlatCard>
          </section>

          <section aria-labelledby="kyc-capacity">
            <div className="mb-3 flex items-center justify-between gap-3">
              <h2 id="kyc-capacity" className="text-lg font-semibold">KYC capacity</h2>
              <Link href="/operator/providers" className="inline-flex min-h-11 items-center text-sm font-medium text-primary underline underline-offset-4">
                Configure providers <ArrowRight className="ml-1 h-4 w-4" />
              </Link>
            </div>
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
              <MetricCard label="Profiles" value={overview.kyc.profileCount} detail="Known provider profiles" />
              <MetricCard label="Enabled" value={overview.kyc.enabledProfileCount} detail="Allowed by node policy" />
              <MetricCard label="Ready" value={overview.kyc.readyProfileCount} detail="Configured and available" />
              <MetricCard label="Tenants" value={overview.kyc.configuredTenantCount} detail="With a provider selected" />
              <MetricCard label="Pending queue" value={overview.kyc.pendingSubmissionCount} detail="Provider and simulation states" />
            </div>
          </section>

          <section className="grid gap-4 lg:grid-cols-2">
            <FlatCard>
              <CardHeader>
                <CardTitle className="flex items-center gap-2 text-base"><ServerCog className="h-4 w-4" /> Deployment checklist</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3 text-sm">
                <ChecklistItem ready={overview.node.persistenceReady} label="Durable persistence is ready" />
                <ChecklistItem ready={overview.kyc.readyProfileCount > 0} label="At least one KYC profile is ready" />
                <ChecklistItem ready={overview.kyc.configuredTenantCount > 0} label="At least one tenant selected a provider" />
                <p className="border-t pt-3 text-xs leading-5 text-muted-foreground">
                  Provider API keys, webhook secrets, and operator credentials stay in Railway or your host secret store. This dashboard reports whether they are configured; it never reads them back.
                </p>
              </CardContent>
            </FlatCard>
            <FlatCard>
              <CardHeader>
                <CardTitle className="flex items-center gap-2 text-base"><Activity className="h-4 w-4" /> Monitoring boundary</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3 text-sm">
                <p className="leading-6 text-muted-foreground">
                  This console exposes a bounded node snapshot and minimized KYC queue. Raw logs, upstream provider payloads, documents, and stack traces are intentionally not shown here.
                </p>
                <div className="flex items-center gap-2 border-t pt-3 text-xs text-muted-foreground">
                  <Clock3 className="h-4 w-4" aria-hidden="true" />
                  Last snapshot: {formatOperatorDate(overview.node.generatedAt)}
                </div>
                <a href="https://docs.railway.com/guides/logs" target="_blank" rel="noreferrer" className="inline-flex min-h-11 items-center text-sm font-medium text-primary underline underline-offset-4">
                  Open host logging guidance <ExternalLink className="ml-1 h-4 w-4" aria-hidden="true" />
                </a>
              </CardContent>
            </FlatCard>
          </section>

          <section aria-labelledby="operator-session-control">
            <FlatCard className="border-red-300 bg-red-50/40">
              <CardHeader>
                <CardTitle id="operator-session-control" className="flex items-center gap-2 text-base text-red-950">
                  <ShieldAlert className="h-4 w-4" aria-hidden="true" /> Session security
                </CardTitle>
              </CardHeader>
              <CardContent className="space-y-4 text-sm">
                <p className="max-w-3xl leading-6 text-red-950/80">
                  The header action ends only this browser session. Use global revocation after credential exposure, an operator handoff, or an incident. It invalidates every current operator session on every device.
                </p>
                {revokeError && <p role="alert" className="border border-red-300 bg-white p-3 text-red-900">{revokeError}</p>}
                {!confirmRevokeAll ? (
                  <Button variant="outline" className="min-h-11 border-red-700 text-red-800 hover:bg-red-100" onClick={() => setConfirmRevokeAll(true)}>
                    Revoke all operator sessions
                  </Button>
                ) : (
                  <div className="border border-red-300 bg-white p-4">
                    <p className="font-medium text-red-950">Confirm global revocation</p>
                    <p className="mt-1 leading-5 text-red-900/80">Every signed-in operator, including this browser, will need the current credentials again.</p>
                    <div className="mt-4 flex flex-col gap-2 sm:flex-row">
                      <Button variant="destructive" className="min-h-11" disabled={revokingAll} onClick={() => void revokeAllSessions()}>
                        {revokingAll ? 'Revoking every session…' : 'Yes, revoke every session'}
                      </Button>
                      <Button variant="outline" className="min-h-11" disabled={revokingAll} onClick={() => setConfirmRevokeAll(false)}>
                        Cancel
                      </Button>
                    </div>
                  </div>
                )}
              </CardContent>
            </FlatCard>
          </section>
        </>
      )}
    </div>
  )
}

function ChecklistItem({ ready, label }: { ready: boolean; label: string }) {
  const Icon = ready ? Check : X
  return (
    <div className="flex items-center gap-3 border-t pt-3 first:border-0 first:pt-0">
      <span className={ready ? 'text-emerald-700' : 'text-amber-700'}><Icon className="h-4 w-4" aria-hidden="true" /></span>
      <span>{label}</span>
      <span className="ml-auto"><StatusPill tone={ready ? 'ready' : 'attention'}>{ready ? 'Ready' : 'Action needed'}</StatusPill></span>
    </div>
  )
}
