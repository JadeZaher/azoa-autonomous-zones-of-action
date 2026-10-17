'use client'

// First-party "add funds": a Checkout payment for the caller's own account; the balance moves only on
// the provider-confirmed webhook. See `src/app/(dashboard)/AGENTS.md` §first-party-money.
import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import type { FiatCheckoutIntent, HostedSessionCapabilities } from 'azoa-sdk'
import { ExternalLink, RefreshCw } from 'lucide-react'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorBanner } from '@/components/shared/error-banner'
import { refusalMessage } from '@/lib/refusal-messages'
import { clearSavedRequest, formatMinorUnits, requestKeyFor, toMinorUnits } from '@/lib/persisted-request-key'

const SCOPE = 'funds'
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

const SETTLEMENT_COPY: Record<FiatCheckoutIntent['settlementState'], string> = {
  not_started: 'Waiting for your payment.',
  payment_received: 'Your payment cleared. The funds are being added to your account.',
  funds_applied: 'The funds are in your account.',
  payment_failed: 'The payment did not go through. Nothing was added.',
}

export default function FundsPage() {
  const { userId } = useAzoaAuth()
  const [capabilities, setCapabilities] = useState<HostedSessionCapabilities | null>(null)
  const [amount, setAmount] = useState('')
  const [intent, setIntent] = useState<FiatCheckoutIntent | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    void azoa.api.getFiatCheckoutCapabilities().then(result => {
      if (isOk(result)) setCapabilities(result.value)
      else setError(refusalMessage(result.error, 'Payment availability could not be read.'))
    })
  }, [])

  const readIntent = useCallback(async (intentId: string) => {
    if (!userId || !UUID.test(intentId)) return
    setBusy(true)
    try {
      const result = await azoa.api.getFiatCheckoutIntent(userId, intentId)
      if (isOk(result)) {
        setIntent(result.value)
        if (result.value.settlementState === 'funds_applied' || result.value.settlementState === 'payment_failed'
          || result.value.status === 'expired') clearSavedRequest(SCOPE, userId)
      } else {
        setError(refusalMessage(result.error, 'That payment could not be read.'))
      }
    } finally { setBusy(false) }
  }, [userId])

  useEffect(() => {
    const returnedIntent = new URLSearchParams(window.location.search).get('intent')
    if (returnedIntent) void readIntent(returnedIntent)
  }, [readIntent])

  async function pay() {
    if (!userId) return
    const minorUnits = toMinorUnits(amount)
    if (minorUnits === null || minorUnits <= 0) {
      setError('Enter an amount like 25 or 25.50.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      const saved = requestKeyFor(SCOPE, userId, minorUnits)
      const result = await azoa.api.startFiatCheckout(userId, { requestKey: saved.requestKey, amountMinorUnits: minorUnits, purpose: 'funds-top-up' })
      if (isOk(result)) setIntent(result.value)
      else setError(refusalMessage(result.error, 'The payment could not start. Your saved request will resume it — try again.'))
    } finally { setBusy(false) }
  }

  const unavailable = capabilities !== null && !capabilities.available

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold">Add funds</h1>
        <p className="text-muted-foreground">Pay by card. Your balance changes only after the payment provider confirms the money arrived.</p>
      </div>
      {error && <ErrorBanner message={error} />}
      {unavailable && (
        <Card><CardContent className="pt-6 text-sm">
          Payments are not available right now{capabilities?.unavailableReason ? `: ${capabilities.unavailableReason}` : '.'}
        </CardContent></Card>
      )}
      <Card>
        <CardHeader>
          <CardTitle>New payment</CardTitle>
          <CardDescription>
            Needs a verified identity — <Link className="underline underline-offset-4" href="/verify-identity">verify it here</Link> if you have not.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="max-w-xs space-y-1.5">
            <Label htmlFor="funds-amount">Amount</Label>
            <Input id="funds-amount" inputMode="decimal" value={amount} onChange={event => setAmount(event.target.value)} placeholder="25.00" />
          </div>
          <Button onClick={() => void pay()} disabled={busy || unavailable || !userId}>Continue to payment</Button>
        </CardContent>
      </Card>
      {intent && (
        <Card>
          <CardHeader>
            <CardTitle>Payment {intent.resumed ? '(resumed)' : ''}</CardTitle>
            <CardDescription role="status">{SETTLEMENT_COPY[intent.settlementState]}</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            {intent.amountPaidMinorUnits !== null && intent.currency && <p>Paid: {formatMinorUnits(intent.amountPaidMinorUnits, intent.currency)}</p>}
            {intent.expiresAt && intent.settlementState === 'not_started' && <p>The payment page works until {new Date(intent.expiresAt).toLocaleString()}.</p>}
            <div className="flex flex-wrap gap-3">
              {intent.paymentUrl && intent.settlementState === 'not_started' && (
                <a className="inline-flex items-center gap-1 underline underline-offset-4" href={intent.paymentUrl}>
                  Open the payment page <ExternalLink className="h-3.5 w-3.5" />
                </a>
              )}
              <Button variant="outline" size="sm" disabled={busy} onClick={() => void readIntent(intent.intentId)}>
                <RefreshCw className="mr-1.5 h-3.5 w-3.5" /> Check payment
              </Button>
            </div>
          </CardContent>
        </Card>
      )}
    </div>
  )
}
