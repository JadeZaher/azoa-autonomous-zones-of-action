'use client'

// First-party identity verification: begin/resume the hosted session, return, refresh the provider result.
// See `src/app/(dashboard)/AGENTS.md` §first-party-money.
import { useCallback, useEffect, useState } from 'react'
import type { KycSessionStart, KycSubmission } from 'azoa-sdk'
import { Check, Copy, ExternalLink, RefreshCw, ShieldCheck } from 'lucide-react'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorBanner } from '@/components/shared/error-banner'
import { refusalMessage } from '@/lib/refusal-messages'

const STATUS_COPY: Record<KycSubmission['status'], string> = {
  PENDING: 'Started — finish the steps with the verification provider, then come back and refresh.',
  PENDING_VENDOR_CREDENTIALS: 'Queued — Identomat credentials are finishing activation. Refresh shortly or return later; nothing you submitted is lost.',
  IN_REVIEW: 'The provider is reviewing your documents. Refresh in a few minutes.',
  APPROVED: 'Verified. Money features that need identity verification are open to you.',
  REJECTED: 'The provider could not verify you. You can start a new verification.',
  EXPIRED: 'Your verification expired. Start a new one to keep using money features.',
}

export default function VerifyIdentityPage() {
  const [status, setStatus] = useState<KycSubmission | null>(null)
  const [session, setSession] = useState<KycSessionStart | null>(null)
  const [busy, setBusy] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [copiedUrl, setCopiedUrl] = useState(false)

  const load = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const current = await azoa.api.getKycStatus()
      if (isOk(current)) setStatus(current.value)
      else if (current.error.status === 404) setStatus(null)
      else setError(refusalMessage(current.error, 'Your verification status could not be read.'))
    } finally {
      setBusy(false)
      setLoaded(true)
    }
  }, [])

  useEffect(() => { void load() }, [load])

  async function begin() {
    setBusy(true)
    setError(null)
    setCopiedUrl(false)
    try {
      const started = await azoa.api.startKycSession()
      if (isOk(started)) {
        const current = await azoa.api.getKycStatus()
        setSession(started.value)
        if (isOk(current)) {
          setStatus(current.value)
        } else if (current.error.status !== 404) {
          setError(refusalMessage(current.error, 'Your verification status could not be refreshed after starting.'))
        }
      } else {
        setError(refusalMessage(started.error, 'Verification could not start.'))
      }
    } finally { setBusy(false) }
  }

  async function refresh() {
    setBusy(true)
    setError(null)
    try {
      const refreshed = await azoa.api.refreshKyc()
      if (isOk(refreshed)) {
        setStatus(refreshed.value)
        if (refreshed.value.status !== 'PENDING' && refreshed.value.status !== 'PENDING_VENDOR_CREDENTIALS') {
          setSession(null)
        }
      } else {
        setError(refusalMessage(refreshed.error, 'The provider result could not be refreshed.'))
      }
    } finally { setBusy(false) }
  }

  const verificationUrl = session?.verificationUrl
    ?? (status?.status === 'PENDING' || status?.status === 'PENDING_VENDOR_CREDENTIALS' ? status.verificationUrl : null)
  const terminal = status?.status === 'APPROVED'
  const currentStatus = status?.status
  const canRefresh = Boolean(status || session)

  const timelineSteps = [
    {
      label: 'Session created',
      detail: status || session ? 'Recorded for your account' : 'Start to create a hosted session',
      done: Boolean(status || session),
    },
    {
      label: 'Identomat check',
      detail: currentStatus === 'PENDING_VENDOR_CREDENTIALS'
        ? 'PendingVendorCredentials: waiting for vendor credentials to activate'
        : currentStatus === 'IN_REVIEW' || currentStatus === 'APPROVED'
          ? 'Identity check submitted'
          : verificationUrl
            ? 'Hosted check link ready'
            : 'Follow the hosted link when issued',
      done: currentStatus === 'IN_REVIEW' || currentStatus === 'APPROVED',
    },
    {
      label: 'Decision',
      detail: currentStatus === 'APPROVED'
        ? 'Approved'
        : currentStatus === 'REJECTED'
          ? 'Not approved — start a new check'
          : currentStatus === 'IN_REVIEW'
            ? 'Under compliance review'
            : 'Pending completion',
      done: currentStatus === 'APPROVED',
    },
  ]

  async function copyLink() {
    if (!verificationUrl || typeof navigator === 'undefined' || !navigator.clipboard?.writeText) return
    try {
      await navigator.clipboard.writeText(verificationUrl)
      setCopiedUrl(true)
    } catch {
      setCopiedUrl(false)
    }
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold">Verify your identity</h1>
        <p className="text-muted-foreground">Complete identity verification directly with our hosted partner so you can fund, withdraw, and receive payouts.</p>
      </div>
      {error && <ErrorBanner message={error} onRetry={() => void load()} />}
      <Card>
        <CardHeader>
          <div className="flex items-center gap-2">
            <ShieldCheck className={terminal ? 'h-5 w-5 text-primary' : 'h-5 w-5 text-muted-foreground'} />
            <CardTitle>{terminal ? 'Identity verified' : 'Identity verification'}</CardTitle>
          </div>
          <CardDescription>
            {!loaded ? 'Reading your verification…' : status ? STATUS_COPY[status.status] : 'You have not started a verification yet.'}
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {currentStatus === 'PENDING_VENDOR_CREDENTIALS' && (
            <div className="rounded-md border border-amber-500/40 bg-amber-500/10 p-3 text-sm" role="status">
              <p className="font-medium">Identomat activation queued (PendingVendorCredentials)</p>
              <p className="mt-1 text-xs text-muted-foreground">
                Your verification intent is saved. Once Identomat credentials become active on this deployment, click Resume verification or Refresh result without re-entering earlier steps.
              </p>
            </div>
          )}

          <ol className="grid gap-2 sm:grid-cols-3" aria-label="Verification timeline">
            {timelineSteps.map(step => (
              <li key={step.label} className="rounded-md border p-3 text-xs">
                <div className="flex items-center gap-1.5 font-medium">
                  <Check className={`h-3.5 w-3.5 ${step.done ? 'text-primary' : 'text-muted-foreground/40'}`} />
                  <span>{step.label}</span>
                </div>
                <p className="mt-1 text-muted-foreground">{step.detail}</p>
              </li>
            ))}
          </ol>

          {status?.rejectionReason && status.status === 'REJECTED' && <p className="text-sm">Reason given: {status.rejectionReason}</p>}
          {status?.expiresAt && status.status === 'APPROVED' && <p className="text-sm">Valid until {new Date(status.expiresAt).toLocaleDateString()}.</p>}
          {session?.developmentSimulation && (
            <p className="text-sm text-amber-600" role="note">This is a development simulation; no real verification happens.</p>
          )}
          {session?.instructions && <p className="text-sm">{session.instructions}</p>}
          <div className="flex flex-wrap items-center gap-3">
            {!terminal && (
              <Button onClick={() => void begin()} disabled={busy || !loaded}>
                {status?.status === 'PENDING' || status?.status === 'PENDING_VENDOR_CREDENTIALS' ? 'Resume verification' : 'Start verification'}
              </Button>
            )}
            <Button variant="outline" onClick={() => void refresh()} disabled={busy || !canRefresh}>
              <RefreshCw className={`mr-1.5 h-3.5 w-3.5 ${busy ? 'animate-spin' : ''}`} />
              Refresh result
            </Button>
            {verificationUrl && (
              <>
                <a className="inline-flex items-center gap-1 text-sm underline underline-offset-4" href={verificationUrl} target="_blank" rel="noopener noreferrer">
                  Continue with the verification provider <ExternalLink className="h-3.5 w-3.5" />
                </a>
                <Button type="button" variant="outline" size="sm" onClick={() => void copyLink()} disabled={busy}>
                  <Copy className="mr-1.5 h-3.5 w-3.5" />
                  {copiedUrl ? 'Link copied' : 'Copy link'}
                </Button>
              </>
            )}
          </div>
        </CardContent>
      </Card>
    </div>
  )
}
