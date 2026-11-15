'use client'

import Link from 'next/link'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { CheckCircle2, ExternalLink, ShieldAlert } from 'lucide-react'
import {
  createHttpCommercialTransport,
  isAccessLinkExpired,
  parseSignedRideAlongParams,
} from 'azoa-sdk/money'
import type {
  AccessLinkScope,
  AccessLinkVerificationView,
  AccountDocumentReportView,
  CommercialInvoiceView,
  IdentomatSessionStatusView,
  OwnershipReceiptView,
  PaymentRequestView,
  ProviderDeferralView,
} from 'azoa-sdk/money'
import {
  AccountDocumentsAndReportsPanel,
  IdentomatVerificationRide,
  InvoiceAndPaymentRequestPanel,
  OwnershipReceiptsPanel,
} from 'azoa-sdk/react'
import { azoa } from '@/lib/azoa'
import { readInitialApiUrl } from '@/lib/runtime-config'
import { Button, buttonVariants } from '@/components/ui/button'
import { ErrorBanner } from '@/components/shared/error-banner'
import { safeReturnUrl } from '@/components/auth/member-handoff'
import { commercialSlots } from '@/components/money/commercial-slots'

const SCOPE_HEADINGS: Record<AccessLinkScope, { title: string; summary: string }> = {
  kyc: {
    title: 'Complete identity verification',
    summary: 'Finish your Identomat identity check to unlock funding, withdrawals, and payouts.',
  },
  consent: {
    title: 'Review account access consent',
    summary: 'Confirm permission for the connected platform to coordinate your account actions.',
  },
  add_funds: {
    title: 'Add funds to your account',
    summary: 'Deposit funds by card and track settlement directly inside your account.',
  },
  cash_out: {
    title: 'Withdraw available balance',
    summary: 'Transfer cleared funds to your verified payout destination.',
  },
  invoice_pay: {
    title: 'Review and settle invoice',
    summary: 'Inspect itemized charges and complete your payment securely.',
  },
  receipt_view: {
    title: 'Inspect ownership receipt',
    summary: 'Verify settlement details, unit quantity, SHA-256 fingerprint, and export records.',
  },
  statement_view: {
    title: 'Account statements & reports',
    summary: 'Review generated statements, export JSON archives, and check any provider notices.',
  },
}

function readErrorMessage(err: unknown, fallback: string): string {
  if (err instanceof Error && err.message.trim().length > 0) return err.message
  return fallback
}

export function RideAlongView({ linkIdFromPath }: { linkIdFromPath?: string }) {
  const [phase, setPhase] = useState<'verifying' | 'valid' | 'invalid'>('verifying')
  const [verification, setVerification] = useState<AccessLinkVerificationView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [mutating, setMutating] = useState(false)
  const [consentConfirmed, setConsentConfirmed] = useState(false)

  const [identomat, setIdentomat] = useState<IdentomatSessionStatusView | null>(null)
  const [invoices, setInvoices] = useState<CommercialInvoiceView[]>([])
  const [paymentRequests, setPaymentRequests] = useState<PaymentRequestView[]>([])
  const [receipts, setReceipts] = useState<OwnershipReceiptView[]>([])
  const [documents, setDocuments] = useState<AccountDocumentReportView[]>([])
  const [deferrals, setDeferrals] = useState<ProviderDeferralView[]>([])

  const transport = useMemo(
    () =>
      createHttpCommercialTransport({
        baseUrl: readInitialApiUrl(),
        getToken: () => azoa.session.token,
      }),
    [],
  )

  const verifyAndLoad = useCallback(async () => {
    if (typeof window === 'undefined') return
    setPhase('verifying')
    setError(null)
    const parsed = parseSignedRideAlongParams(window.location.search)
    const linkId = parsed.linkId ?? linkIdFromPath ?? null

    if (!linkId || !parsed.sig) {
      setPhase('invalid')
      setError('This ride-along access link is missing its signature or link identifier.')
      return
    }

    if (parsed.exp && isAccessLinkExpired(parsed.exp)) {
      setPhase('invalid')
      setError('This ride-along access link has expired. Request a new link from your platform.')
      return
    }

    try {
      const checked = await transport.verifyAccessLink(linkId, {
        sig: parsed.sig,
        exp: parsed.exp,
        scope: parsed.scope,
        sub: parsed.sub,
      })
      const confirmedLink = checked.link ?? null
      const expired = checked.expired === true || (confirmedLink ? isAccessLinkExpired(confirmedLink.expiresAt) : false)
      if (!checked.valid || expired) {
        setVerification(checked)
        setPhase('invalid')
        setError(checked.reason ?? (expired ? 'This ride-along access link has expired.' : 'This access link signature could not be verified.'))
        return
      }

      setVerification(checked)
      setPhase('valid')

      const activeScope = (confirmedLink?.scope ?? parsed.scope ?? 'kyc') as AccessLinkScope
      if (activeScope === 'kyc') {
        const res = await Promise.allSettled([transport.getIdentomatStatus(), transport.listProviderDeferrals()])
        if (res[0].status === 'fulfilled') setIdentomat(res[0].value)
        if (res[1].status === 'fulfilled') setDeferrals(res[1].value)
      } else if (activeScope === 'invoice_pay') {
        const res = await Promise.allSettled([transport.listInvoices(), transport.listPaymentRequests()])
        if (res[0].status === 'fulfilled') {
          const targetId = confirmedLink?.targetResourceId
          setInvoices(targetId ? res[0].value.filter(i => i.id === targetId || !targetId) : res[0].value)
        }
        if (res[1].status === 'fulfilled') {
          setPaymentRequests(res[1].value)
        }
      } else if (activeScope === 'receipt_view') {
        const res = await Promise.allSettled([transport.listOwnershipReceipts()])
        if (res[0].status === 'fulfilled') {
          const targetId = confirmedLink?.targetResourceId
          const filtered = targetId ? res[0].value.filter(r => r.receiptId === targetId) : res[0].value
          setReceipts(filtered.length > 0 ? filtered : res[0].value)
        }
      } else if (activeScope === 'statement_view') {
        const res = await Promise.allSettled([transport.listAccountDocuments(), transport.listProviderDeferrals()])
        if (res[0].status === 'fulfilled') setDocuments(res[0].value)
        if (res[1].status === 'fulfilled') setDeferrals(res[1].value)
      }
    } catch (err) {
      setPhase('invalid')
      setError(readErrorMessage(err, 'The access link could not be verified right now.'))
    }
  }, [linkIdFromPath, transport])

  useEffect(() => {
    void verifyAndLoad()
  }, [verifyAndLoad])

  const verifiedLink = verification?.link ?? null
  const scope = (verifiedLink?.scope ?? (typeof window !== 'undefined' ? parseSignedRideAlongParams(window.location.search).scope : null) ?? 'kyc') as AccessLinkScope
  const copy = SCOPE_HEADINGS[scope] ?? SCOPE_HEADINGS.kyc
  const returnUrl = verifiedLink?.returnUrl && safeReturnUrl(verifiedLink.returnUrl) ? verifiedLink.returnUrl : null

  const runAction = async <T,>(fn: () => Promise<T>, onSuccess: (val: T) => void, fallback: string) => {
    setMutating(true)
    setError(null)
    try {
      const value = await fn()
      onSuccess(value)
    } catch (err) {
      setError(readErrorMessage(err, fallback))
    } finally {
      setMutating(false)
    }
  }

  return (
    <main className="mx-auto max-w-3xl space-y-6 p-6">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b pb-4">
        <div>
          <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">AZOA Guided Session</p>
          <h1 className="text-2xl font-semibold tracking-tight">{copy.title}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{copy.summary}</p>
        </div>
        {returnUrl && (
          <a href={returnUrl} className={buttonVariants({ variant: 'outline', size: 'sm' })}>
            Return to platform <ExternalLink className="ml-1.5 h-3.5 w-3.5" />
          </a>
        )}
      </div>

      {phase === 'verifying' && (
        <div role="status" className="rounded-lg border bg-card p-5 text-sm text-muted-foreground">
          Verifying access link signature and expiry…
        </div>
      )}

      {phase === 'invalid' && (
        <section className="space-y-4 rounded-lg border border-destructive/40 bg-destructive/5 p-5" role="alert">
          <div className="flex items-start gap-3">
            <ShieldAlert className="mt-0.5 h-5 w-5 text-destructive" />
            <div className="space-y-1">
              <h2 className="text-base font-semibold">Access link unavailable</h2>
              <p className="text-sm text-muted-foreground">{error ?? 'This link is invalid or expired.'}</p>
            </div>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" size="sm" onClick={() => void verifyAndLoad()}>
              Try again
            </Button>
            <Link href="/documents" className={buttonVariants({ variant: 'ghost', size: 'sm' })}>
              Open Documents hub
            </Link>
          </div>
        </section>
      )}

      {phase === 'valid' && (
        <div className="space-y-6">
          {error && <ErrorBanner message={error} onRetry={() => void verifyAndLoad()} />}

          {verifiedLink && (
            <div className="flex flex-wrap items-center gap-3 rounded-lg border bg-muted/30 px-4 py-3 text-xs text-muted-foreground">
              <span className="inline-flex items-center gap-1 font-medium text-foreground">
                <CheckCircle2 className="h-3.5 w-3.5 text-emerald-500" /> Verified scope: {verifiedLink.scope}
              </span>
              <span>Subject: {verifiedLink.externalSubject}</span>
              <span>Expires: {new Date(verifiedLink.expiresAt).toLocaleString()}</span>
            </div>
          )}

          {scope === 'kyc' && (
            <div className="space-y-4">
              <IdentomatVerificationRide
                session={identomat}
                onStartVerification={() =>
                  runAction(
                    () =>
                      transport.startIdentomatSession({
                        externalSubject: verifiedLink?.externalSubject,
                        returnUrl: returnUrl ?? (typeof window !== 'undefined' ? window.location.href : undefined),
                        flowKind: 'kyc',
                      }),
                    next => setIdentomat(next),
                    'Identomat check could not be started.',
                  )
                }
                onRefresh={() =>
                  runAction(
                    () => transport.getIdentomatStatus(),
                    next => setIdentomat(next),
                    'Verification status could not be refreshed.',
                  )
                }
                slots={commercialSlots}
                busy={mutating}
              />
              {deferrals.length > 0 && (
                <AccountDocumentsAndReportsPanel
                  documents={[]}
                  deferrals={deferrals}
                  slots={commercialSlots}
                />
              )}
              <div className="text-xs text-muted-foreground">
                Need full account verification history?{' '}
                <Link className="underline underline-offset-4" href="/verify-identity">
                  Open Identity Verification page
                </Link>
              </div>
            </div>
          )}

          {scope === 'consent' && (
            <section className="space-y-4 rounded-lg border bg-card p-5">
              <h2 className="text-base font-semibold">Authorize Platform Permissions</h2>
              <p className="text-sm text-muted-foreground">
                Grant or manage scoped authorization for {verifiedLink?.externalSubject ?? 'your account'}.
              </p>
              {consentConfirmed ? (
                <div role="status" className="rounded-md border border-emerald-500/40 bg-emerald-500/10 p-3 text-sm">
                  Consent step acknowledged. Review active grants any time under Settings.
                </div>
              ) : (
                <div className="flex flex-wrap gap-3">
                  <Button onClick={() => setConsentConfirmed(true)}>Confirm consent review</Button>
                  <Link href="/settings" className={buttonVariants({ variant: 'outline' })}>
                    Manage active consents
                  </Link>
                </div>
              )}
            </section>
          )}

          {scope === 'add_funds' && (
            <section className="space-y-4 rounded-lg border bg-card p-5">
              <h2 className="text-base font-semibold">Continue to Add Funds</h2>
              <p className="text-sm text-muted-foreground">
                Proceed to your account funding form to deposit by card and track payment settlement.
              </p>
              <Link href="/funds" className={buttonVariants()}>
                Open Add Funds
              </Link>
            </section>
          )}

          {scope === 'cash_out' && (
            <section className="space-y-4 rounded-lg border bg-card p-5">
              <h2 className="text-base font-semibold">Continue to Cash Out</h2>
              <p className="text-sm text-muted-foreground">
                Withdraw settled balance or verify your payout destination directly in your account.
              </p>
              <div className="flex flex-wrap gap-3">
                <Link href="/cash-out" className={buttonVariants()}>
                  Open Cash Out
                </Link>
                <Link href="/payout-balance" className={buttonVariants({ variant: 'outline' })}>
                  Check Payout Destination
                </Link>
              </div>
            </section>
          )}

          {scope === 'invoice_pay' && (
            <InvoiceAndPaymentRequestPanel
              invoices={invoices}
              paymentRequests={paymentRequests}
              onPayInvoice={invoiceId =>
                runAction(
                  () => transport.payInvoice(invoiceId),
                  updated => setInvoices(prev => prev.map(item => (item.id === updated.id ? updated : item))),
                  'Invoice payment could not be settled.',
                )
              }
              onPayPaymentRequest={requestId =>
                runAction(
                  () => transport.payPaymentRequest(requestId),
                  updated => setPaymentRequests(prev => prev.map(item => (item.id === updated.id ? updated : item))),
                  'Payment request could not be settled.',
                )
              }
              slots={commercialSlots}
              busy={mutating}
            />
          )}

          {scope === 'receipt_view' && (
            <OwnershipReceiptsPanel receipts={receipts} slots={commercialSlots} busy={mutating} />
          )}

          {scope === 'statement_view' && (
            <AccountDocumentsAndReportsPanel
              documents={documents}
              deferrals={deferrals}
              onGenerateDocument={draft =>
                runAction(
                  () => transport.generateAccountDocument(draft),
                  created => setDocuments(prev => [created, ...prev]),
                  'Statement could not be generated.',
                )
              }
              slots={commercialSlots}
              busy={mutating}
            />
          )}
        </div>
      )}
    </main>
  )
}
