'use client'

// Tenant payout float (D-8): cash-outs priced by this tenant are paid from it, never from the node.
// See `src/app/(dashboard)/AGENTS.md` §tenant-payout-float.
import { useCallback, useEffect, useState } from 'react'
import type { TenantPayoutFloatView, TenantPayoutTopUp } from 'azoa-sdk'
import { ExternalLink, RefreshCw } from 'lucide-react'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorBanner } from '@/components/shared/error-banner'
import { refusalMessage } from '@/lib/refusal-messages'
import { PendingRequestNotice } from '@/components/shared/pending-request-notice'
import {
  type SavedRequest, claimRequestKey, clearSavedRequest, formatMinorUnits, isDefiniteRefusal, isFinishedCheckout,
  readSavedRequest, toMinorUnits,
} from '@/lib/persisted-request-key'

const SCOPE = 'tenant-payout-top-up'

export default function PayoutBalancePage() {
  const { userId } = useAzoaAuth()
  const [floats, setFloats] = useState<TenantPayoutFloatView[] | null>(null)
  const [topUp, setTopUp] = useState<TenantPayoutTopUp | null>(null)
  const [amount, setAmount] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [pending, setPending] = useState<SavedRequest | null>(null)

  const load = useCallback(async () => {
    setBusy(true)
    setError(null)
    try {
      const result = await azoa.api.getTenantPayoutBalance()
      if (isOk(result)) setFloats(result.value ?? [])
      else if (result.error.status === 403 && !result.error.serverCode?.includes('KYB')) setError('The payout balance belongs to an organization account. Sign in as the organization to see it.')
      else setError(refusalMessage(result.error, 'Your payout balance could not be read.'))
    } finally { setBusy(false) }
  }, [])

  useEffect(() => { void load() }, [load])
  useEffect(() => { if (userId) setPending(readSavedRequest(SCOPE, userId)) }, [userId])

  async function startTopUp() {
    if (!userId) return
    const minorUnits = toMinorUnits(amount)
    if (minorUnits === null || minorUnits <= 0) {
      setError('Enter an amount like 500 or 500.00.')
      return
    }
    const claim = claimRequestKey(SCOPE, userId, minorUnits)
    if (claim.kind === 'pending') {
      setError(null)
      setPending(claim.pending)
      return
    }
    await send(claim.request)
  }

  async function send(request: SavedRequest) {
    if (!userId) return
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const result = await azoa.api.startTenantPayoutTopUp({ requestKey: request.requestKey, amountMinorUnits: request.amountMinorUnits })
      if (isOk(result)) {
        setPending(null)
        setTopUp(result.value)
        if (isFinishedCheckout(result.value)) {
          clearSavedRequest(SCOPE, userId)
          if (result.value.resumed) setNotice('This matched your earlier top-up, which has already finished. Nothing new was charged. Press "Continue to payment" again to start a new top-up.')
        }
      } else if (isDefiniteRefusal(result.error.status)) {
        clearSavedRequest(SCOPE, userId)
        setPending(null)
        setError(refusalMessage(result.error, 'The top-up was refused. Nothing was charged.'))
      } else {
        setPending(request)
        setError(refusalMessage(result.error, 'The top-up could not start. Resuming the saved request reopens the same payment.'))
      }
    } finally { setBusy(false) }
  }

  function discardPending() {
    if (!userId) return
    clearSavedRequest(SCOPE, userId)
    setPending(null)
    setNotice('The unconfirmed top-up was discarded. Enter an amount to start a new one.')
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold">Payout balance</h1>
        <p className="text-muted-foreground">
          When your price schedule sets what a person receives on cash-out, the money comes from this balance — never from the platform.
          A cash-out it cannot cover is refused before anything is sent.
        </p>
      </div>
      {error && <ErrorBanner message={error} onRetry={() => void load()} />}
      {notice && <p role="status" className="text-sm">{notice}</p>}
      {pending && (
        <PendingRequestNotice
          summary={`A top-up of ${formatMinorUnits(pending.amountMinorUnits, floats?.[0]?.currencyCode ?? '')}`}
          detail="Resuming reopens that same payment instead of starting a second one. Discard it only if you want a separate top-up; a payment page you already completed still counts."
          resumeLabel="Resume that top-up"
          busy={busy}
          onResume={() => void send(pending)}
          onDiscard={discardPending}
        />
      )}
      <Card>
        <CardHeader className="flex flex-row items-start justify-between">
          <div>
            <CardTitle>Balance</CardTitle>
            <CardDescription>Top-ups count, net of fees, once the payment can no longer be charged back.</CardDescription>
          </div>
          <Button variant="outline" size="sm" onClick={() => void load()} disabled={busy}><RefreshCw className="mr-1.5 h-3.5 w-3.5" />Refresh</Button>
        </CardHeader>
        <CardContent className="space-y-4">
          {floats === null ? <p className="text-sm text-muted-foreground">Reading…</p>
            : floats.length === 0 ? <p className="text-sm text-muted-foreground">No balance yet. Top up below before publishing a price schedule that pays people out.</p>
            : floats.map(float => (
              <div key={float.floatId} className="space-y-2 text-sm">
                {floats.length > 1 && <p className="text-xs text-muted-foreground">{float.currencyCode.toUpperCase()} · {float.environment}</p>}
                <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
                  <div><div className="text-xs text-muted-foreground">Available</div><div className="font-medium">{formatMinorUnits(float.availableMinorUnits, float.currencyCode)}</div></div>
                  <div><div className="text-xs text-muted-foreground">Reserved for cash-outs in flight</div><div className="font-medium">{formatMinorUnits(float.reservedMinorUnits, float.currencyCode)}</div></div>
                  <div><div className="text-xs text-muted-foreground">Top-ups still maturing</div><div className="font-medium">{formatMinorUnits(float.pendingCreditMinorUnits, float.currencyCode)}</div></div>
                </div>
                {float.deficitMinorUnits > 0 && (
                  <p className="text-destructive" role="alert">
                    A top-up was charged back: {formatMinorUnits(float.deficitMinorUnits, float.currencyCode)} is uncovered, and every cash-out priced by you is refused until it is topped up.
                  </p>
                )}
              </div>
            ))}
        </CardContent>
      </Card>
          <Card>
            <CardHeader><CardTitle>Top up</CardTitle></CardHeader>
            <CardContent className="space-y-4 text-sm">
              <div className="flex flex-wrap items-end gap-3">
                <div className="space-y-1.5">
                  <Label htmlFor="top-up-amount">Amount</Label>
                  <Input id="top-up-amount" className="w-40" inputMode="decimal" value={amount} onChange={event => setAmount(event.target.value)} placeholder="500.00" />
                </div>
                <Button onClick={() => void startTopUp()} disabled={busy || !userId}>Continue to payment</Button>
              </div>
              {topUp?.paymentUrl && topUp.settlementState === 'not_started' && (
                <a className="inline-flex items-center gap-1 underline underline-offset-4" href={topUp.paymentUrl}>
                  Open the payment page <ExternalLink className="h-3.5 w-3.5" />
                </a>
              )}
            </CardContent>
          </Card>
    </div>
  )
}
