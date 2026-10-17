'use client'

import { useCallback, useEffect, useState } from 'react'
import type { BusinessVerificationCapabilities, BusinessVerificationStatus } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorBanner } from '@/components/shared/error-banner'

export default function BusinessVerificationPage() {
  const [capabilities, setCapabilities] = useState<BusinessVerificationCapabilities | null>(null)
  const [status, setStatus] = useState<BusinessVerificationStatus | null>(null)
  const [redirect, setRedirect] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const [available, current] = await Promise.all([
        azoa.api.getBusinessVerificationCapabilities(), azoa.api.getBusinessVerification(),
      ])
      if (!isOk(available) || !isOk(current)) {
        setError('Business verification could not be loaded. Sign in as an active tenant administrator and try again.')
        return
      }
      setCapabilities(available.value)
      setStatus(current.value)
      if (current.value?.status !== 'PENDING') setRedirect(null)
    } finally { setBusy(false) }
  }, [])

  useEffect(() => { void load() }, [load])

  async function begin() {
    setBusy(true)
    setError(null)
    try {
      const result = await azoa.api.beginBusinessVerification(crypto.randomUUID())
      if (!isOk(result)) {
        setError('Business verification could not start. Refresh the status and try again.')
        return
      }
      setStatus(result.value.verification)
      setRedirect(result.value.verificationUrl)
    } finally { setBusy(false) }
  }

  async function refresh() {
    setBusy(true)
    setError(null)
    try {
      const result = await azoa.api.refreshBusinessVerification()
      if (isOk(result)) {
        setStatus(result.value)
        if (result.value?.status !== 'PENDING') setRedirect(null)
      } else setError('Business verification could not be refreshed. Please try again.')
    } finally { setBusy(false) }
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold">Business verification</h1>
        <p className="text-muted-foreground">Verify your organization and its beneficial owners with Identomat.</p>
      </div>
      {error && <ErrorBanner message={error} />}
      <Card>
        <CardHeader>
          <CardTitle>{status?.verified ? 'Business verified' : 'Organization review'}</CardTitle>
          <CardDescription>Company documents and personal information are collected in Identomat’s secure verification flow.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <p role="status">{status ? `Status: ${status.status.toLowerCase().replaceAll('_', ' ')}` : 'No verification started.'}</p>
          {status?.reviewedAt && <p>Reviewed {new Date(status.reviewedAt).toLocaleDateString()}. Linked participants: {status.beneficiaryCount}.</p>}
          {status && <p>Valid until {new Date(status.expiresAt).toLocaleDateString()}.</p>}
          {capabilities && !capabilities.available && <p>Business verification is currently unavailable. Contact the platform operator to enable Identomat business verification.</p>}
          {status?.status === 'NEEDS_OPERATOR' && <p>The verification request needs reconciliation. Contact the platform operator before starting another review.</p>}
          <div className="flex flex-wrap gap-3">
            <Button onClick={() => void begin()} disabled={busy || !capabilities?.available || status?.status === 'NEEDS_OPERATOR'}>
              {status?.status === 'PENDING' ? 'Resume verification' : status ? 'Start a new review' : 'Start verification'}
            </Button>
            <Button variant="outline" onClick={() => void refresh()} disabled={busy}>Refresh status</Button>
            {redirect && <a className="underline underline-offset-4" href={redirect} target="_blank" rel="noopener noreferrer">Continue with Identomat</a>}
          </div>
          <p className="text-sm text-muted-foreground">A business approval does not replace an individual’s identity verification or screening requirements.</p>
        </CardContent>
      </Card>
    </div>
  )
}
