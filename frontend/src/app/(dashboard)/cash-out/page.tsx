'use client'

// First-party cash-out: payout-account setup (hosted, first-party only), availability, a keyed request,
// and history. See `src/app/(dashboard)/AGENTS.md` §first-party-money.
import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import type { CashOutQuote, CashOutView, PayoutAccountStatus } from 'azoa-sdk'
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

const SCOPE = 'cash-out'

export default function CashOutPage() {
  const { userId } = useAzoaAuth()
  const [quote, setQuote] = useState<CashOutQuote | null>(null)
  const [account, setAccount] = useState<PayoutAccountStatus | null>(null)
  const [history, setHistory] = useState<CashOutView[]>([])
  const [amount, setAmount] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    if (!userId) return
    setBusy(true)
    setError(null)
    try {
      const [available, payoutAccount, rows] = await Promise.all([
        azoa.api.getCashOutAvailability(userId), azoa.api.getPayoutAccountStatus(userId), azoa.api.listCashOutHistory(userId, 20),
      ])
      if (isOk(available)) setQuote(available.value)
      else setError(refusalMessage(available.error, 'What you can cash out could not be read.'))
      if (isOk(payoutAccount)) setAccount(payoutAccount.value)
      if (isOk(rows)) setHistory(rows.value ?? [])
    } finally { setBusy(false) }
  }, [userId])

  useEffect(() => { void load() }, [load])

  async function setUpAccount() {
    if (!userId) return
    setBusy(true)
    setError(null)
    try {
      const result = await azoa.api.beginPayoutAccountSetup(userId)
      if (isOk(result)) setAccount(result.value)
      else setError(refusalMessage(result.error, 'Payout-account setup could not start.'))
    } finally { setBusy(false) }
  }

  async function cashOut() {
    if (!userId || !quote) return
    const minorUnits = toMinorUnits(amount)
    if (minorUnits === null || minorUnits < quote.minimumAmountMinorUnits || minorUnits > Math.min(quote.maximumAmountMinorUnits, quote.cashableMinorUnits)) {
      setError(`Enter an amount between ${formatMinorUnits(quote.minimumAmountMinorUnits, quote.currencyCode)} and ${formatMinorUnits(Math.min(quote.maximumAmountMinorUnits, quote.cashableMinorUnits), quote.currencyCode)}.`)
      return
    }
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const saved = requestKeyFor(SCOPE, userId, minorUnits)
      const result = await azoa.api.startCashOut(userId, { requestKey: saved.requestKey, amountMinorUnits: minorUnits })
      if (isOk(result)) {
        clearSavedRequest(SCOPE, userId)
        setNotice(result.value.statusMessage)
        setAmount('')
        await load()
      } else {
        setError(refusalMessage(result.error, 'The cash-out was not confirmed. Trying again resumes the same request and cannot pay twice.'))
      }
    } finally { setBusy(false) }
  }

  const ready = quote?.available && account?.readyToReceive

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold">Cash out</h1>
        <p className="text-muted-foreground">Turn your balance into money in your own bank account. Needs a verified identity and a payout account.</p>
      </div>
      {error && <ErrorBanner message={error} onRetry={() => void load()} />}
      {notice && <p role="status" className="text-sm">{notice}</p>}

      <Card>
        <CardHeader>
          <CardTitle>Payout account</CardTitle>
          <CardDescription>{account?.statusMessage ?? 'Reading your payout account…'}</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-wrap items-center gap-3 text-sm">
          {account && !account.readyToReceive && (
            <Button onClick={() => void setUpAccount()} disabled={busy || !account.available}>
              {account.accountOpened ? 'Continue setup' : 'Set up payout account'}
            </Button>
          )}
          {account?.setupUrl && (
            <a className="inline-flex items-center gap-1 underline underline-offset-4" href={account.setupUrl}>
              Open the setup page <ExternalLink className="h-3.5 w-3.5" />
            </a>
          )}
          {account && account.outstandingRequirementCount > 0 && <span>{account.outstandingRequirementCount} item(s) still needed by the provider.</span>}
          <Link className="underline underline-offset-4" href="/verify-identity">Identity verification</Link>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Available</CardTitle>
          <CardDescription>{quote?.statusMessage ?? 'Reading what you can cash out…'}</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4 text-sm">
          {quote && (
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
              <div><div className="text-xs text-muted-foreground">Cashable now</div><div className="font-medium">{formatMinorUnits(quote.cashableMinorUnits, quote.currencyCode)}</div></div>
              <div><div className="text-xs text-muted-foreground">Still on hold</div><div className="font-medium">{formatMinorUnits(quote.onHoldMinorUnits, quote.currencyCode)}</div></div>
              {quote.availableFrom && <div><div className="text-xs text-muted-foreground">Held part frees up</div><div className="font-medium">{new Date(quote.availableFrom).toLocaleDateString()}</div></div>}
            </div>
          )}
          <div className="flex flex-wrap items-end gap-3">
            <div className="space-y-1.5">
              <Label htmlFor="cash-out-amount">Amount</Label>
              <Input id="cash-out-amount" className="w-40" inputMode="decimal" value={amount} onChange={event => setAmount(event.target.value)} placeholder="50.00" />
            </div>
            <Button onClick={() => void cashOut()} disabled={busy || !ready}>Cash out</Button>
            <Button variant="outline" onClick={() => void load()} disabled={busy}><RefreshCw className="mr-1.5 h-3.5 w-3.5" />Refresh</Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader><CardTitle>History</CardTitle></CardHeader>
        <CardContent className="text-sm">
          {history.length === 0 ? <p className="text-muted-foreground">No cash-outs yet.</p> : (
            <ul className="divide-y">
              {history.map(row => (
                <li key={row.id} className="flex flex-wrap justify-between gap-2 py-2">
                  <span>{formatMinorUnits(row.amountMinorUnits, row.currencyCode)}</span>
                  <span className="text-muted-foreground">{row.statusMessage}</span>
                  <span className="text-xs text-muted-foreground">{new Date(row.requestedAt).toLocaleString()}</span>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
