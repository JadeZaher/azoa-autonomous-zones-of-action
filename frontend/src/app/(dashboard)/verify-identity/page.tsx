'use client'

// First-party identity verification: begin/resume the hosted session, return, refresh the provider result.
// See `src/app/(dashboard)/AGENTS.md` §first-party-money.
import { useCallback, useEffect, useState } from 'react'
import type { KycSessionStart, KycSubmission } from 'azoa-sdk'
import { ExternalLink, RefreshCw, ShieldCheck } from 'lucide-react'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorBanner } from '@/components/shared/error-banner'
import { refusalMessage } from '@/lib/refusal-messages'

const STATUS_COPY: Record<KycSubmission['status'], string> = {
  PENDING: 'Started — finish the steps with the verification provider, then come back and refresh.',
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
    try {
      const started = await azoa.api.startKycSession()
      if (isOk(started)) {
        setSession(started.value)
        const current = await azoa.api.getKycStatus()
        if (isOk(current)) setStatus(current.value)
      } else setError(refusalMessage(started.error, 'Verification could not start.'))
    } finally { setBusy(false) }
  }

  async function refresh() {
    setBusy(true)
    setError(null)
    try {
      const refreshed = await azoa.api.refreshKyc()
      if (isOk(refreshed)) {
        setStatus(refreshed.value)
        if (refreshed.value.status !== 'PENDING') setSession(null)
      } else {
        setError(refusalMessage(refreshed.error, 'The provider result could not be refreshed.'))
      }
    } finally { setBusy(false) }
  }

  const verificationUrl = session?.verificationUrl ?? (status?.status === 'PENDING' ? status.verificationUrl : null)
  const terminal = status?.status === 'APPROVED'

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold">Verify your identity</h1>
        <p className="text-muted-foreground">Adding funds and cashing out need a verified identity. Documents go to the verification provider, never to this page.</p>
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
          {status?.rejectionReason && status.status === 'REJECTED' && <p className="text-sm">Reason given: {status.rejectionReason}</p>}
          {status?.expiresAt && status.status === 'APPROVED' && <p className="text-sm">Valid until {new Date(status.expiresAt).toLocaleDateString()}.</p>}
          {session?.developmentSimulation && (
            <p className="text-sm text-amber-600" role="note">This is a development simulation; no real verification happens.</p>
          )}
          {session?.instructions && <p className="text-sm">{session.instructions}</p>}
          <div className="flex flex-wrap gap-3">
            {!terminal && (
              <Button onClick={() => void begin()} disabled={busy || !loaded}>
                {status?.status === 'PENDING' ? 'Resume verification' : 'Start verification'}
              </Button>
            )}
            <Button variant="outline" onClick={() => void refresh()} disabled={busy || (!status && !session)}>
              <RefreshCw className={`mr-1.5 h-3.5 w-3.5 ${busy ? 'animate-spin' : ''}`} />
              Refresh result
            </Button>
            {verificationUrl && (
              <a className="inline-flex items-center gap-1 text-sm underline underline-offset-4" href={verificationUrl} target="_blank" rel="noopener noreferrer">
                Continue with the verification provider <ExternalLink className="h-3.5 w-3.5" />
              </a>
            )}
          </div>
        </CardContent>
      </Card>
    </div>
  )
}
