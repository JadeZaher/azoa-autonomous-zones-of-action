'use client'

import { useEffect, useMemo, useState } from 'react'
import { AzoaMoneyProvider, AddFundsForm, BalanceSummary, CustodialAccountCard, WithdrawForm, type MoneySlots } from 'azoa-sdk/react'
import { createDirectMoneyTransport, createKeyStore, MONEY_SCOPES, moneyRefusal } from 'azoa-sdk/money'
import { AzoaApiClient } from 'azoa-sdk'
import { azoa } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { migrateLegacyMoneyKeys } from '@/lib/migrate-money-keys'
import { readInitialApiUrl } from '@/lib/runtime-config'

const slots: MoneySlots = {
  Button: ({ variant, ...props }) => <Button {...props} variant={variant === 'secondary' ? 'outline' : 'default'} />,
  Input: props => <Input {...props} />,
  Card: ({ title, description, children, className }) => <section className={'space-y-4 border p-5 ' + (className ?? '')}>
    {title && <h2 className="text-xl font-semibold">{title}</h2>}{description && <div>{description}</div>}{children}
  </section>,
  Alert: ({ tone, children, className }) => <div role={tone === 'error' || tone === 'warning' ? 'alert' : 'status'} className={'border p-3 ' + (className ?? '')}>{children}</div>,
}

/** First-party SDK money surfaces; see AGENTS.md. */
export function MemberMoneySurface({ kind, onRedirect }: { kind: 'funds' | 'cash-out'; onRedirect?: (url: string) => void }) {
  const { userId } = useAzoaAuth()
  const [returnedIntent, setReturnedIntent] = useState<string | null>(null)
  const [origin, setOrigin] = useState('')
  useEffect(() => {
    setOrigin(window.location.origin)
    setReturnedIntent(new URLSearchParams(window.location.search).get('intent'))
  }, [])
  const state = useMemo(() => {
    if (!userId || !origin) return null
    try {
      const keyStore = createKeyStore({ subject: userId })
      migrateLegacyMoneyKeys(localStorage, keyStore, userId, MONEY_SCOPES.checkout, MONEY_SCOPES.cashOut)
      const token = azoa.session.token
      if (!token || azoa.session.userId !== userId) throw new Error('Account session changed.')
      const client = new AzoaApiClient({ baseUrl: readInitialApiUrl(), token })
      const transport = createDirectMoneyTransport(client, userId, { checkoutReturnUrl: origin + '/funds', checkoutPurpose: 'funds-top-up' })
      const beginKyc = transport.account.beginKyc
      transport.account.beginKyc = (...args) => azoa.session.userId === userId && azoa.session.token === token
        ? beginKyc(...args) : Promise.resolve({ ok: false, refusal: moneyRefusal('unconfirmed', 'Your account session changed. Open verification again from your current account.') })
      return { keyStore, transport }
    } catch { return { error: 'An earlier payment could not be restored. Contact support before starting another.' } }
  }, [userId, origin])
  if (!userId) return <p>Sign in to view your funds.</p>
  if (!state) return <p role="status">Reading your account…</p>
  if ('error' in state) return <p role="alert">{state.error}</p>
  return <AzoaMoneyProvider subject={userId} transport={state.transport} keyStore={state.keyStore} slots={slots}
    allowedRedirectOrigins={[origin]} allowLoopbackRedirects={process.env.NODE_ENV !== 'production'}
    onRedirect={url => onRedirect ? onRedirect(url) : window.location.assign(url)}>
    <main className="mx-auto max-w-3xl space-y-6 p-6">
      <h1 className="text-3xl font-bold">{kind === 'funds' ? 'Funds' : 'Cash out'}</h1>
      <CustodialAccountCard />
      <BalanceSummary />
      {kind === 'funds' ? <AddFundsForm currencyCode="USD" returnedIntentId={returnedIntent} /> : <WithdrawForm />}
    </main>
  </AzoaMoneyProvider>
}
