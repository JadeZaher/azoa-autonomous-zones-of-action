'use client'

import { useEffect, useRef, useState, type FormEvent } from 'react'
import type { MemberHandoffDescriptor, PayoutAccountStatus } from 'azoa-sdk'
import { azoa } from '@/lib/azoa'
import { recoverClaimLogin, takeHandoffFragment } from '@/lib/member-handoff'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

type Invitation = NonNullable<ReturnType<typeof takeHandoffFragment>>

/** Hosted member account and permission steps; see AGENTS.md. */
export function MemberHandoff({ claim }: { claim: boolean }) {
  const captured = useRef(false)
  const [invitation, setInvitation] = useState<Invitation | null>(null)
  const [descriptor, setDescriptor] = useState<MemberHandoffDescriptor | null>(null)
  const [password, setPassword] = useState('')
  const [memberId, setMemberId] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [bank, setBank] = useState<PayoutAccountStatus | null>(null)
  const [expired, setExpired] = useState(false)
  const invitationExpiry = descriptor?.expiresAt

  useEffect(() => {
    const update = () => setExpired(Boolean(invitationExpiry && Date.parse(invitationExpiry) <= Date.now()))
    const initial = window.setTimeout(update, 0)
    const clock = window.setInterval(update, 1000)
    return () => { window.clearTimeout(initial); window.clearInterval(clock) }
  }, [invitationExpiry])

  useEffect(() => {
    if (captured.current) return
    captured.current = true
    const value = takeHandoffFragment()
    setInvitation(value)
    if (!value) { setError('Open your invitation again, or ask for a fresh link.'); return }
    setMemberId(value.targetUserId)
    void (async () => {
      await azoa.session.restore()
      const result = claim
        ? await azoa.api.previewMemberHandoff(value.requestId, value.token)
        : await azoa.api.readMemberHandoff(value.requestId)
      if (result.ok) setDescriptor(result.value)
      else setError(claim ? 'This invitation expired or was replaced. If you already chose a password, sign in below.' : 'Sign in to review this request.')
    })().catch(() => setError('This request could not be read. Retry using your invitation.'))
  }, [claim])

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!invitation || busy) return
    setBusy(true); setError(null)
    try {
      let signedIn = false
      if (claim && descriptor && invitation.token) {
        const result = await azoa.api.claimWalletOwnership({ targetUserId: descriptor.targetUserId, claimToken: invitation.token, newPassword: password })
        if (result.ok) {
          await azoa.session.acceptAuthenticationToken(result.value.token, descriptor.targetUserId)
          signedIn = true
        }
      }
      if (!signedIn) signedIn = await recoverClaimLogin(memberId, password)
      if (!signedIn) { setError('The account could not be confirmed. Retry the same password, or request a fresh invitation.'); return }
      setPassword('')
      window.location.assign('/consent#requestId=' + encodeURIComponent(invitation.requestId))
    } catch { setError('The answer did not arrive. Retry the same password to sign in.'); }
    finally { setBusy(false) }
  }

  async function decide(approve: boolean) {
    if (!descriptor || busy) return
    setBusy(true); setError(null)
    try {
      const result = approve ? await azoa.api.approveMemberHandoff(descriptor.requestId) : await azoa.api.declineMemberHandoff(descriptor.requestId)
      if (result.ok) setDescriptor(result.value)
      else setError(result.error.serverCode === 'HANDOFF_EXPIRED'
        ? 'This invitation expired. Decline can resolve an earlier approval attempt. Return to your platform for a fresh request.'
        : 'Your decision could not be confirmed. Retry this same request.')
    } catch { setError('The answer did not arrive. Retry this same request.') }
    finally { setBusy(false) }
  }

  async function readBank(begin: boolean) {
    if (!descriptor || busy) return
    setBusy(true); setError(null)
    try {
      const result = begin ? await azoa.api.beginPayoutAccountSetup(descriptor.targetUserId) : await azoa.api.getPayoutAccountStatus(descriptor.targetUserId)
      if (result.ok) setBank(result.value)
      else setError('Bank setup is not available yet. Your account permission is saved.')
    } catch { setError('Bank setup could not be confirmed. Check again before continuing.') }
    finally { setBusy(false) }
  }

  const authenticated = Boolean(!claim && descriptor)
  return <main className="mx-auto min-h-screen max-w-xl space-y-6 px-6 py-16">
    <h1 className="text-3xl font-bold">{claim ? 'Your account' : 'Your permission'}</h1>
    {error && <p role="alert">{error}</p>}
    {!authenticated && <form onSubmit={submit} className="space-y-4">
      <p>{claim ? 'Choose a password you control. Your platform will then ask separately for permission to keep serving you.' : 'Sign in with the account you claimed. Your platform cannot approve this for you.'}</p>
      {!claim && <><Label htmlFor="member-id">Account reference</Label><Input id="member-id" value={memberId} onChange={event => setMemberId(event.target.value)} required /></>}
      <Label htmlFor="member-password">Password</Label>
      <Input id="member-password" type="password" autoComplete={claim ? 'new-password' : 'current-password'} value={password} minLength={claim ? 12 : undefined} maxLength={128} onChange={event => setPassword(event.target.value)} required />
      {descriptor && <p className="text-sm">Account reference: {descriptor.targetUserId}</p>}
      <Button disabled={busy || !invitation} type="submit">{busy ? 'Checking…' : claim ? 'Claim account' : 'Sign in'}</Button>
    </form>}
    {authenticated && descriptor && <>
      <p>{descriptor.tenantName} requests these permissions until {new Date(descriptor.grantExpiresAt).toLocaleDateString()}.</p>
      <ul className="list-disc pl-6">{descriptor.scopes.map(scope => <li key={scope}>{scopeLabels[scope] ?? scope}</li>)}</ul>
      <p>This invitation expires at {new Date(descriptor.expiresAt).toLocaleString()}. You can withdraw permission later. Declining does not approve this request; existing permissions are managed separately.</p>
      {expired && descriptor.status !== 'approved' && descriptor.status !== 'declined' && <p role="status">This invitation expired. An earlier approval can still be checked or resolved with Decline. Return to your platform for a fresh request.</p>}
      {(descriptor.status === 'pending' || descriptor.status === 'approving') && <div className="flex gap-3">
        <Button disabled={busy || (expired && descriptor.status === 'pending')} onClick={() => void decide(true)}>{expired ? 'Check earlier approval' : 'Approve permission'}</Button>
        <Button variant="outline" disabled={busy} onClick={() => void decide(false)}>Decline</Button>
      </div>}
      {descriptor.status === 'declined' && <p role="status">This request was declined. Other existing permissions are managed separately.</p>}
      {descriptor.status === 'approved' && <section className="space-y-4">
        <h2 className="text-xl font-semibold">Bank destination</h2>
        <p>This request was approved. Permission can be withdrawn separately. Only you can set up your bank destination.</p>
        <Button disabled={busy} onClick={() => void readBank(true)}>Set up bank</Button>
        <Button variant="outline" disabled={busy} onClick={() => void readBank(false)}>Check bank setup</Button>
        {bank?.setupUrl && safeBankUrl(bank.setupUrl) && <a className="block underline" href={bank.setupUrl} target="_blank" rel="noopener noreferrer">Continue bank setup</a>}
        {bank?.readyToReceive && <p role="status">Bank destination ready.</p>}
      </section>}
      {(expired || descriptor.status === 'approved' || descriptor.status === 'declined') && safeReturnUrl(descriptor.returnUrl) && <a className="block underline" href={descriptor.returnUrl} rel="noreferrer">{expired && descriptor.status !== 'approved' ? 'Return to platform for a fresh request' : 'Return to platform'}</a>}
    </>}
  </main>
}

const scopeLabels: Record<string, string> = {
  'kyc:read': 'Read verification status', 'kyc:submit': 'Submit verification requests',
  'units:issue': 'Receive purchased funds and project units', 'swap:sign': 'Exchange funds and project units when you request it',
  'transfer:sign': 'Send funds when you request it',
}
function safeBankUrl(value: string) {
  try { const url = new URL(value); return url.protocol === 'https:' && !url.username && !url.password && (url.hostname === 'connect.stripe.com' || url.hostname.endsWith('.stripe.com')) } catch { return false }
}
function safeReturnUrl(value: string) {
  try { const url = new URL(value); return ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password && url.pathname === '/portfolio/withdraw' && !url.search && !url.hash } catch { return false }
}
