'use client'

import Link from 'next/link'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { Check, Copy, RefreshCw, Share2 } from 'lucide-react'
import {
  buildSignedRideAlongUrl,
  createHttpCommercialTransport,
  formatInvoiceTotal,
  isAccessLinkExpired,
} from 'azoa-sdk/money'
import type {
  AccessLinkScope,
  AccountDocumentReportView,
  CommercialInvoiceView,
  IdentomatSessionStatusView,
  OwnershipReceiptView,
  PaymentRequestView,
  ProviderDeferralView,
  SignedAccessLinkView,
} from 'azoa-sdk/money'
import {
  AccessLinkRideAlongCard,
  AccountDocumentsAndReportsPanel,
  IdentomatVerificationRide,
  InvoiceAndPaymentRequestPanel,
  OwnershipReceiptsPanel,
} from 'azoa-sdk/react'
import type {
  AccessLinkMintInput,
  CreateInvoiceDraft,
  CreatePaymentRequestDraft,
  GenerateAccountDocumentDraft,
  IssueReceiptDraft,
} from 'azoa-sdk/react'
import { azoa } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { readInitialApiUrl } from '@/lib/runtime-config'
import { Button, buttonVariants } from '@/components/ui/button'
import { ErrorBanner } from '@/components/shared/error-banner'
import { commercialSlots } from '@/components/money/commercial-slots'

function readErrorMessage(err: unknown, fallback: string): string {
  if (err instanceof Error && err.message.trim().length > 0) return err.message
  return fallback
}

function isIgnorableEmptyStatus(err: unknown): boolean {
  if (typeof err !== 'object' || err === null) return false
  const status = (err as { status?: unknown }).status
  return status === 404
}

export default function DocumentsPage() {
  const { userId } = useAzoaAuth()
  const [origin, setOrigin] = useState('')
  const [loading, setLoading] = useState(true)
  const [mutating, setMutating] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [accessLinks, setAccessLinks] = useState<SignedAccessLinkView[]>([])
  const [latestLink, setLatestLink] = useState<SignedAccessLinkView | null>(null)
  const [copiedKey, setCopiedKey] = useState<string | null>(null)
  const [identomat, setIdentomat] = useState<IdentomatSessionStatusView | null>(null)
  const [invoices, setInvoices] = useState<CommercialInvoiceView[]>([])
  const [paymentRequests, setPaymentRequests] = useState<PaymentRequestView[]>([])
  const [receipts, setReceipts] = useState<OwnershipReceiptView[]>([])
  const [documents, setDocuments] = useState<AccountDocumentReportView[]>([])
  const [deferrals, setDeferrals] = useState<ProviderDeferralView[]>([])

  useEffect(() => {
    setOrigin(window.location.origin)
  }, [])

  const transport = useMemo(
    () =>
      createHttpCommercialTransport({
        baseUrl: readInitialApiUrl(),
        getToken: () => azoa.session.token,
      }),
    [userId],
  )

  const loadAll = useCallback(async () => {
    if (!userId) {
      setLoading(false)
      return
    }
    setLoading(true)
    setError(null)
    const results = await Promise.allSettled([
      transport.listAccessLinks(),
      transport.getIdentomatStatus(),
      transport.listInvoices(),
      transport.listPaymentRequests(),
      transport.listOwnershipReceipts(),
      transport.listAccountDocuments(),
      transport.listProviderDeferrals(),
    ])

    const errors: string[] = []
    const [linksRes, identomatRes, invoicesRes, reqsRes, receiptsRes, docsRes, deferralsRes] = results

    if (linksRes.status === 'fulfilled') {
      setAccessLinks(linksRes.value)
      setLatestLink(prev => prev ?? linksRes.value[0] ?? null)
    } else if (!isIgnorableEmptyStatus(linksRes.reason)) {
      errors.push(readErrorMessage(linksRes.reason, 'Signed access links could not be read.'))
    }

    if (identomatRes.status === 'fulfilled') {
      setIdentomat(identomatRes.value)
    } else if (!isIgnorableEmptyStatus(identomatRes.reason)) {
      errors.push(readErrorMessage(identomatRes.reason, 'Identomat status could not be read.'))
    }

    if (invoicesRes.status === 'fulfilled') {
      setInvoices(invoicesRes.value)
    } else if (!isIgnorableEmptyStatus(invoicesRes.reason)) {
      errors.push(readErrorMessage(invoicesRes.reason, 'Invoices could not be read.'))
    }

    if (reqsRes.status === 'fulfilled') {
      setPaymentRequests(reqsRes.value)
    } else if (!isIgnorableEmptyStatus(reqsRes.reason)) {
      errors.push(readErrorMessage(reqsRes.reason, 'Payment requests could not be read.'))
    }

    if (receiptsRes.status === 'fulfilled') {
      setReceipts(receiptsRes.value)
    } else if (!isIgnorableEmptyStatus(receiptsRes.reason)) {
      errors.push(readErrorMessage(receiptsRes.reason, 'Ownership receipts could not be read.'))
    }

    if (docsRes.status === 'fulfilled') {
      setDocuments(docsRes.value)
    } else if (!isIgnorableEmptyStatus(docsRes.reason)) {
      errors.push(readErrorMessage(docsRes.reason, 'Account documents could not be read.'))
    }

    if (deferralsRes.status === 'fulfilled') {
      setDeferrals(deferralsRes.value)
    } else if (!isIgnorableEmptyStatus(deferralsRes.reason)) {
      errors.push(readErrorMessage(deferralsRes.reason, 'Provider notices could not be read.'))
    }

    if (errors.length > 0) setError(errors[0] ?? 'Commercial workspace failed to load.')
    setLoading(false)
  }, [transport, userId])

  useEffect(() => {
    void loadAll()
  }, [loadAll])

  async function runAction<T>(fn: () => Promise<T>, onSuccess: (value: T) => void, fallbackError: string) {
    setMutating(true)
    setError(null)
    try {
      const value = await fn()
      onSuccess(value)
    } catch (err) {
      setError(readErrorMessage(err, fallbackError))
    } finally {
      setMutating(false)
    }
  }

  const resolveShareUrl = (entry: SignedAccessLinkView): string => {
    const built = buildSignedRideAlongUrl(origin || '', entry)
    if (origin && built.startsWith('/')) return `${origin}${built}`
    return built
  }

  const copyShareUrl = async (key: string, url: string) => {
    if (typeof navigator === 'undefined' || !navigator.clipboard) return
    await navigator.clipboard.writeText(url)
    setCopiedKey(key)
    setTimeout(() => setCopiedKey(curr => (curr === key ? null : curr)), 1800)
  }

  const handleMintAccessLink = async (input: AccessLinkMintInput) => {
    await runAction(
      () =>
        transport.mintAccessLink({
          scope: input.scope,
          externalSubject: userId ?? undefined,
          targetResourceId: input.targetResourceId,
          returnUrl: input.returnUrl,
        }),
      issued => {
        const enriched = issued.signedUrl
          ? issued
          : { ...issued, signedUrl: buildSignedRideAlongUrl(origin || '', issued) }
        setLatestLink(enriched)
        setAccessLinks(prev => [enriched, ...prev.filter(item => item.linkId !== enriched.linkId)])
      },
      'Access link could not be issued.',
    )
  }

  const handleQuickMintAndCopy = async (key: string, scope: AccessLinkScope, targetResourceId?: string) => {
    await runAction(
      () =>
        transport.mintAccessLink({
          scope,
          externalSubject: userId ?? undefined,
          targetResourceId,
          returnUrl: origin ? `${origin}/documents` : '/documents',
        }),
      issued => {
        const enriched = issued.signedUrl
          ? issued
          : { ...issued, signedUrl: buildSignedRideAlongUrl(origin || '', issued) }
        setLatestLink(enriched)
        setAccessLinks(prev => [enriched, ...prev.filter(item => item.linkId !== enriched.linkId)])
        void copyShareUrl(key, resolveShareUrl(enriched))
      },
      'Shareable ride-along link could not be issued.',
    )
  }

  const handleStartIdentomat = async () => {
    await runAction(
      () =>
        transport.startIdentomatSession({
          externalSubject: userId ?? undefined,
          returnUrl: origin ? `${origin}/documents` : '/documents',
          flowKind: 'kyc',
        }),
      next => setIdentomat(next),
      'Identomat verification could not be started.',
    )
  }

  const handleRefreshIdentomat = async () => {
    await runAction(
      () => transport.getIdentomatStatus(),
      next => setIdentomat(next),
      'Identomat status could not be refreshed.',
    )
  }

  const handleCreateInvoice = async (draft: CreateInvoiceDraft) => {
    const amountMinorUnits = draft.quantity * draft.unitPriceMinorUnits
    await runAction(
      () =>
        transport.createInvoice({
          title: draft.title,
          payerSubject: draft.payerSubject,
          currencyCode: draft.currencyCode,
          feeMinorUnits: draft.feeMinorUnits ?? 0,
          lineItems: [
            {
              description: draft.description,
              quantity: draft.quantity,
              unitPriceMinorUnits: draft.unitPriceMinorUnits,
              amountMinorUnits,
            },
          ],
        }),
      created => setInvoices(prev => [created, ...prev]),
      'Invoice could not be created.',
    )
  }

  const handleCreatePaymentRequest = async (draft: CreatePaymentRequestDraft) => {
    await runAction(
      () =>
        transport.createPaymentRequest({
          title: draft.title,
          payerSubject: draft.payerSubject,
          currencyCode: draft.currencyCode,
          amountMinorUnits: draft.amountMinorUnits,
          memo: draft.memo,
        }),
      created => setPaymentRequests(prev => [created, ...prev]),
      'Payment request could not be created.',
    )
  }

  const handlePayInvoice = async (invoiceId: string) => {
    await runAction(
      () => transport.payInvoice(invoiceId),
      updated => setInvoices(prev => prev.map(item => (item.id === updated.id ? updated : item))),
      'Invoice payment could not be completed.',
    )
  }

  const handleVoidInvoice = async (invoiceId: string) => {
    await runAction(
      () => transport.voidInvoice(invoiceId),
      updated => setInvoices(prev => prev.map(item => (item.id === updated.id ? updated : item))),
      'Invoice could not be voided.',
    )
  }

  const handlePayPaymentRequest = async (requestId: string) => {
    await runAction(
      () => transport.payPaymentRequest(requestId),
      updated => setPaymentRequests(prev => prev.map(item => (item.id === updated.id ? updated : item))),
      'Payment request could not be settled.',
    )
  }

  const handleIssueReceipt = async (draft: IssueReceiptDraft) => {
    await runAction(
      () =>
        transport.issueOwnershipReceipt({
          allocationOrSaleId: draft.allocationOrSaleId,
          holderSubject: draft.holderSubject,
          holderClass: draft.holderClass,
          assetSymbol: draft.assetSymbol,
          unitsFormatted: draft.unitsFormatted,
          fiatMinorUnits: draft.fiatMinorUnits,
          currencyCode: draft.currencyCode,
        }),
      created => setReceipts(prev => [created, ...prev]),
      'Ownership receipt could not be issued.',
    )
  }

  const handleGenerateDocument = async (draft: GenerateAccountDocumentDraft) => {
    await runAction(
      () =>
        transport.generateAccountDocument({
          documentType: draft.documentType,
          title: draft.title,
        }),
      created => setDocuments(prev => [created, ...prev]),
      'Account document could not be generated.',
    )
  }

  const openRideAlong = (url: string) => {
    if (typeof window === 'undefined') return
    if (url.startsWith('/')) {
      window.location.assign(url)
      return
    }
    try {
      const parsed = new URL(url)
      if (parsed.origin === window.location.origin) {
        window.location.assign(parsed.pathname + parsed.search)
        return
      }
      window.open(parsed.href, '_blank', 'noopener,noreferrer')
    } catch {
      // Safe fallback handled by SDK component.
    }
  }

  if (!userId) {
    return (
      <main className="mx-auto max-w-4xl space-y-4 p-6">
        <h1 className="text-2xl font-semibold tracking-tight">Documents, receipts &amp; invoices</h1>
        <p className="text-sm text-muted-foreground">Sign in to manage invoices, payment requests, ownership receipts, and access links.</p>
        <Link className="inline-block text-sm underline underline-offset-4" href="/login">
          Sign in
        </Link>
      </main>
    )
  }

  const totalOpenInvoiceMinor = invoices
    .filter(item => item.status === 'open' || item.status === 'draft')
    .reduce((sum, item) => sum + item.totalMinorUnits, 0)

  return (
    <main className="mx-auto max-w-5xl space-y-6 p-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="space-y-1">
          <h1 className="text-2xl font-semibold tracking-tight">Documents, receipts &amp; invoices</h1>
          <p className="text-sm text-muted-foreground">
            Issue ride-along access links, manage commercial invoices and payment requests, and export verified ownership statements.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="outline" size="sm" onClick={() => void loadAll()} disabled={loading || mutating}>
            <RefreshCw className={`mr-1.5 h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
            Refresh
          </Button>
          <Link href="/funds" className={buttonVariants({ variant: 'outline', size: 'sm' })}>
            Add funds
          </Link>
        </div>
      </div>

      {error && <ErrorBanner message={error} onRetry={() => void loadAll()} />}

      <div className="grid gap-3 sm:grid-cols-4">
        <div className="rounded-lg border bg-card p-4">
          <p className="text-xs text-muted-foreground">Open invoices total</p>
          <p className="mt-1 text-lg font-semibold">{formatInvoiceTotal(totalOpenInvoiceMinor, 'USD')}</p>
        </div>
        <div className="rounded-lg border bg-card p-4">
          <p className="text-xs text-muted-foreground">Payment requests</p>
          <p className="mt-1 text-lg font-semibold">{paymentRequests.length}</p>
        </div>
        <div className="rounded-lg border bg-card p-4">
          <p className="text-xs text-muted-foreground">Ownership receipts</p>
          <p className="mt-1 text-lg font-semibold">{receipts.length}</p>
        </div>
        <div className="rounded-lg border bg-card p-4">
          <p className="text-xs text-muted-foreground">Generated reports</p>
          <p className="mt-1 text-lg font-semibold">{documents.length}</p>
        </div>
      </div>

      <section aria-label="Quick-share ride-along links" className="space-y-3 rounded-lg border bg-card p-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div>
            <h2 className="flex items-center gap-1.5 text-sm font-semibold">
              <Share2 className="h-4 w-4 text-primary" aria-hidden="true" />
              Quick-share ride-along links
            </h2>
            <p className="text-xs text-muted-foreground">
              Issue a signed link for a counterparty to verify identity or settle an invoice/request without manual setup.
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Button
              type="button"
              size="sm"
              disabled={mutating}
              onClick={() => void handleQuickMintAndCopy('quick-kyc', 'kyc')}
            >
              {copiedKey === 'quick-kyc' ? 'Copied KYC Link' : 'Issue & Copy KYC Link'}
            </Button>
            {invoices.length > 0 && (
              <Button
                type="button"
                size="sm"
                variant="outline"
                disabled={mutating}
                onClick={() => void handleQuickMintAndCopy('quick-invoice', 'invoice_pay', invoices[0]!.id)}
              >
                {copiedKey === 'quick-invoice' ? 'Copied Invoice Link' : 'Issue & Copy Latest Invoice Link'}
              </Button>
            )}
            {paymentRequests.length > 0 && (
              <Button
                type="button"
                size="sm"
                variant="outline"
                disabled={mutating}
                onClick={() => void handleQuickMintAndCopy('quick-request', 'invoice_pay', paymentRequests[0]!.id)}
              >
                {copiedKey === 'quick-request' ? 'Copied Request Link' : 'Issue & Copy Latest Request Link'}
              </Button>
            )}
          </div>
        </div>
        {latestLink && (
          <div className="flex flex-wrap items-center justify-between gap-2 rounded border bg-muted/30 px-3 py-2 text-xs">
            <span className="truncate font-mono">{resolveShareUrl(latestLink)}</span>
            <Button
              type="button"
              size="sm"
              variant="secondary"
              className="h-7 px-2 text-xs"
              onClick={() => void copyShareUrl('latest', resolveShareUrl(latestLink))}
            >
              {copiedKey === 'latest'
                ? <Check className="mr-1 h-3.5 w-3.5 text-primary" aria-hidden="true" />
                : <Copy className="mr-1 h-3.5 w-3.5" aria-hidden="true" />}
              {copiedKey === 'latest' ? 'Copied' : 'Copy shareable link'}
            </Button>
          </div>
        )}
      </section>

      <div className="grid gap-6 lg:grid-cols-2">
        <div className="space-y-4">
          <AccessLinkRideAlongCard
            link={latestLink}
            onMint={handleMintAccessLink}
            onOpenRideAlong={openRideAlong}
            mintButtonLabel="Issue Access Link"
            slots={commercialSlots}
            busy={mutating}
          />
          {accessLinks.length > 0 && (
            <section className="space-y-2 rounded-lg border bg-card p-4" aria-label="Issued access links">
              <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Recent Access Links</h3>
              <ul className="space-y-2 text-xs">
                {accessLinks.slice(0, 5).map(entry => {
                  const expired = isAccessLinkExpired(entry.expiresAt)
                  const rideHref = buildSignedRideAlongUrl(origin || '', entry)
                  const shareUrl = resolveShareUrl(entry)
                  return (
                    <li key={entry.linkId} className="flex flex-wrap items-center justify-between gap-2 rounded border px-3 py-2">
                      <div>
                        <span className="font-medium">{entry.scope}</span>
                        <span className="ml-2 text-muted-foreground">{expired ? 'Expired' : 'Active'}</span>
                      </div>
                      <div className="flex items-center gap-2">
                        <Button
                          type="button"
                          size="sm"
                          variant="ghost"
                          className="h-7 px-2 text-xs"
                          onClick={() => void copyShareUrl(entry.linkId, shareUrl)}
                        >
                          {copiedKey === entry.linkId ? 'Copied' : 'Copy link'}
                        </Button>
                        <Link className="underline underline-offset-4" href={rideHref.startsWith('http') ? `/ride${new URL(rideHref).search}` : rideHref}>
                          Open ride page
                        </Link>
                      </div>
                    </li>
                  )
                })}
              </ul>
            </section>
          )}
        </div>

        <IdentomatVerificationRide
          session={identomat}
          onStartVerification={handleStartIdentomat}
          onRefresh={handleRefreshIdentomat}
          slots={commercialSlots}
          busy={mutating}
        />
      </div>

      <InvoiceAndPaymentRequestPanel
        invoices={invoices}
        paymentRequests={paymentRequests}
        onCreateInvoice={handleCreateInvoice}
        onCreatePaymentRequest={handleCreatePaymentRequest}
        onPayInvoice={handlePayInvoice}
        onVoidInvoice={handleVoidInvoice}
        onPayPaymentRequest={handlePayPaymentRequest}
        slots={commercialSlots}
        busy={mutating}
      />

      <OwnershipReceiptsPanel
        receipts={receipts}
        onIssueReceipt={handleIssueReceipt}
        slots={commercialSlots}
        busy={mutating}
      />

      <AccountDocumentsAndReportsPanel
        documents={documents}
        deferrals={deferrals}
        onGenerateDocument={handleGenerateDocument}
        slots={commercialSlots}
        busy={mutating}
      />
    </main>
  )
}
