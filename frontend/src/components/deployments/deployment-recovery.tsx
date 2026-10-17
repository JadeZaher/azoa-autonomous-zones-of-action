'use client'

import { useEffect, useRef, useState } from 'react'
import type { DeploymentRecoveryAncestry, DeploymentOriginalBudgetRecoveryRequest, DeploymentReceipt, DeploymentRecoveryMode, DeploymentRecoveryQuote, DeploymentRecoveryStartRequest } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoa } from '@/lib/azoa-context'
import { Button } from '@/components/ui/button'

type SavedRecovery = { kind: 'original'; request: DeploymentOriginalBudgetRecoveryRequest } | { kind: 'linked'; request: DeploymentRecoveryStartRequest }

function recoveryMode(value: unknown): DeploymentRecoveryMode {
  if (value !== 'AbortCleanup' && value !== 'ContinueCleanup' && value !== 'FinalizeAuthority') throw new Error('Invalid saved recovery mode')
  return value
}

function recoveryInputField(input: unknown, camel: string, pascal: string): unknown {
  if (!input || typeof input !== 'object') return undefined
  const fields = input as Record<string, unknown>
  if (camel in fields && pascal in fields && fields[camel] !== fields[pascal]) throw new Error('Conflicting saved recovery fields')
  return fields[camel] ?? fields[pascal]
}

export function DeploymentRecovery({ userId, receipt, onReceipt }: { userId: string; receipt: DeploymentReceipt; onReceipt: (value: DeploymentReceipt) => void }) {
  const { wallets } = useAzoa()
  const [mode, setMode] = useState<DeploymentRecoveryMode>(receipt.resourceEffect?.kind === 'Deployed' || receipt.resourceEffect?.kind === 'Upgraded' ? 'FinalizeAuthority' : 'AbortCleanup')
  const [payer, setPayer] = useState('')
  const [maximumFee, setMaximumFee] = useState('')
  const [quoted, setQuoted] = useState<{ key: string; quote: DeploymentRecoveryQuote } | null>(null)
  const [saved, setSaved] = useState<SavedRecovery | null>(null)
  const [acknowledged, setAcknowledged] = useState(false)
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [ancestry, setAncestry] = useState<DeploymentRecoveryAncestry | null>(null)
  const alive = useRef(true)
  const storageKey = `azoa:deployment-recovery:${userId}:${receipt.operationId}`
  useEffect(() => {
    alive.current = true
    try {
      const raw = localStorage.getItem(storageKey)
      if (raw) {
        const value = JSON.parse(raw) as SavedRecovery
        if (!value.request || !['original', 'linked'].includes(value.kind)) throw new Error('Invalid saved recovery')
        if (value.kind === 'original') setMode(recoveryMode(value.request.mode))
        else {
          const input = value.request.quote?.intent?.input
          setMode(recoveryMode(recoveryInputField(input, 'mode', 'Mode')))
          if (typeof recoveryInputField(input, 'payerAddress', 'PayerAddress') !== 'string') throw new Error('Missing saved recovery payer')
          recoveryInputField(input, 'predecessor', 'Predecessor')
          if (!value.request.quote.intent.quote || !value.request.quote.intent.budget) throw new Error('Missing saved recovery costs')
        }
        setSaved(value); setAcknowledged(true)
      }
      setReady(true)
    } catch { setError('Saved recovery cannot be read. Preserve it and recover the original operation before authorizing another request.') }
    return () => { alive.current = false }
  }, [storageKey])

  const inspectAncestry = async () => {
    if (busy) return
    setBusy(true); setError(null)
    try {
      const result = await azoa.api.inspectDeploymentRecoveryAncestry(receipt.operationId)
      if (!alive.current) return
      if (isOk(result)) setAncestry(result.value); else setError(result.error.message)
    } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Ancestry inspection failed. Retry to resume the saved position.') }
    finally { if (alive.current) setBusy(false) }
  }

  const quote = async () => {
    if (busy || saved || !receipt.authorization || !/^(0|[1-9][0-9]*)$/.test(maximumFee)) return
    const wallet = wallets.find(item => item.id === payer && item.walletType === 'Platform' && item.chainType.toLowerCase() === receipt.target.chainType.toLowerCase())
    if (!wallet) { setError('Select a fee-paying wallet.'); return }
    setBusy(true); setError(null); setQuoted(null); setAcknowledged(false)
    const key = crypto.randomUUID()
    const originalAuthorities = receipt.authorization.authorities
    const context = originalAuthorities[0]?.signingContext
    if (!context) { setBusy(false); setError('Original signing authority is unavailable.'); return }
    const authorities = [...originalAuthorities.filter(item => item.address !== wallet.address), { address: wallet.address, signingContext: { ...context, walletId: wallet.id, chainType: wallet.chainType } }]
    try {
      const result = await azoa.api.quoteDeploymentRecovery(receipt.operationId, { expectedVersion: receipt.version, requestKey: key, mode,
        payerAddress: wallet.address, authorities, budget: { maximumNetworkFee: maximumFee, maximumLockedRent: '0', maximumValue: '0', quoteId: '' } })
      if (!alive.current) return
      if (isOk(result)) setQuoted({ key, quote: result.value }); else setError(result.error.message)
    } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Recovery quote failed.') }
    finally { if (alive.current) setBusy(false) }
  }

  const send = async (kind: 'original' | 'linked') => {
    if (busy || !ready || !acknowledged || (!saved && kind === 'linked' && !quoted)) return
    setBusy(true); setError(null)
    try {
      const command: SavedRecovery = saved ?? (kind === 'original'
        ? { kind, request: { expectedVersion: receipt.version, mode, acknowledgeRecovery: true } }
        : { kind, request: { requestKey: quoted!.key, quote: quoted!.quote, acknowledgeCosts: true } })
      localStorage.setItem(storageKey, JSON.stringify(command)); setSaved(command)
      const result = command.kind === 'original'
        ? await azoa.api.recoverDeploymentWithinBudget(receipt.operationId, command.request)
        : await azoa.api.startDeploymentRecovery(receipt.operationId, command.request)
      if (!alive.current) return
      if (isOk(result)) {
        if (command.kind === 'original' && result.value.state === 'RecoveryQuoteRequired') {
          localStorage.removeItem(storageKey); setSaved(null); setAcknowledged(false)
        }
        onReceipt(result.value)
      } else setError(result.error.message)
    } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Recovery request failed.') }
    finally { if (alive.current) setBusy(false) }
  }

  const reconcile = async () => {
    if (busy || !saved) return
    setBusy(true); setError(null)
    try {
      const result = await azoa.api.getDeployment(receipt.operationId)
      if (!alive.current) return
      if (!isOk(result)) { setError(result.error.message); return }
      const current = result.value
      const expectedVersion = saved.kind === 'original' ? saved.request.expectedVersion : saved.request.quote.parentVersion
      const neverAdmitted = current.version > expectedVersion && !current.recovery?.childOperationId
        && (!current.recovery || current.recovery.mode === 'None')
      const originalCapResolved = saved.kind === 'original' && current.state === 'RecoveryQuoteRequired'
        && current.recovery?.mode === saved.request.mode && !current.recovery.childOperationId
      const originalSuperseded = saved.kind === 'original' && current.version > expectedVersion && !!current.recovery?.childOperationId
      if (neverAdmitted || originalCapResolved || originalSuperseded) {
        localStorage.removeItem(storageKey); setSaved(null); setQuoted(null); setAcknowledged(false)
      } else setError('The saved authorization is still active or its admission is unresolved. Retry the exact saved request.')
      onReceipt(current)
    } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Recovery reconciliation failed.') }
    finally { if (alive.current) setBusy(false) }
  }

  const invalidate = async () => {
    if (busy || saved?.kind !== 'linked') return
    setBusy(true); setError(null)
    try {
      const result = await azoa.api.invalidateDeploymentRecovery(receipt.operationId, saved.request)
      if (!alive.current) return
      if (!isOk(result)) { setError(result.error.message); return }
      localStorage.removeItem(storageKey); setSaved(null); setQuoted(null); setAcknowledged(false)
      onReceipt(result.value)
    } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Recovery invalidation is unresolved. Retry with the saved request.') }
    finally { if (alive.current) setBusy(false) }
  }

  const disclosedIntent = saved?.kind === 'linked' ? saved.request.quote.intent : quoted?.quote.intent
  const disclosedPredecessor = disclosedIntent ? recoveryInputField(disclosedIntent.input, 'predecessor', 'Predecessor') : null
  return <section className="space-y-2 rounded border p-3" aria-label="Deployment recovery">
    <h4 className="font-medium">{receipt.recovery?.childOperationId ? 'Replace recovery attempt' : 'Recover allocated resources'}</h4>
    {receipt.recovery?.childOperationId && <p className="text-sm">A new quote can replace attempt {receipt.recovery.childOperationId} only after the server proves its transactions are resolved or were never authorized. Previous fees and receipts remain unchanged.</p>}
    {receipt.recovery?.childOperationId && <div>
      {ancestry?.headOperationId === receipt.recovery.childOperationId && <p role="status">Recovery history: {ancestry.state}. Inspected attempts: {ancestry.inspectedCount}.{ancestry.detail && ` ${ancestry.detail}`}</p>}
      <Button variant="outline" disabled={busy || (ancestry?.headOperationId === receipt.recovery.childOperationId && ancestry.state !== 'Running')} onClick={inspectAncestry}>Inspect next recovery history page</Button>
    </div>}
    <p className="text-sm">Recovery preserves confirmed deployment effects and prior network fees. The server must resolve every original transaction before it can authorize cleanup.</p>
    {disclosedPredecessor != null && <details><summary>Exact recovery attempt being replaced</summary><pre className="overflow-auto text-xs">{JSON.stringify(disclosedPredecessor, null, 2)}</pre></details>}
    <label>Recovery action<select aria-label="Recovery action" disabled={busy || !!saved || !!quoted} value={mode} onChange={event => setMode(event.target.value as DeploymentRecoveryMode)} className="block rounded border p-2">
      <option value="AbortCleanup">Abort deployment and return buffer rent</option><option value="ContinueCleanup">Finish required buffer cleanup</option><option value="FinalizeAuthority">Finish requested authority removal</option>
    </select></label>
    {saved?.kind === 'linked' && <div aria-label="Saved recovery authorization">
      <p>Saved request: {saved.request.requestKey}. This retry uses the original authorization.</p>
      <p>Quoted fee: {saved.request.quote.intent.quote?.networkFee} native base units. Maximum fee: {saved.request.quote.intent.budget.maximumNetworkFee}. Maximum locked rent: {saved.request.quote.intent.budget.maximumLockedRent}. Maximum value: {saved.request.quote.intent.budget.maximumValue}.</p>
      <p>Fee payer: {String(recoveryInputField(saved.request.quote.intent.input, 'payerAddress', 'PayerAddress') ?? 'Unavailable')}. Return destination: {saved.request.quote.intent.quote?.disclosure?.refundDestination}.</p>
    </div>}
    {!saved && <>
      <label>Fee-paying wallet<select aria-label="Recovery fee-paying wallet" value={payer} disabled={busy || !!quoted} onChange={event => setPayer(event.target.value)} className="block rounded border p-2"><option value="">Select wallet for a new quote</option>{wallets.filter(wallet => wallet.walletType === 'Platform' && wallet.chainType.toLowerCase() === receipt.target.chainType.toLowerCase()).map(wallet => <option key={wallet.id} value={wallet.id}>{wallet.label || wallet.address}</option>)}</select></label>
      <label>Additional fee cap (native base units)<input aria-label="Recovery additional fee cap" inputMode="numeric" className="block rounded border p-2" value={maximumFee} disabled={busy || !!quoted} onChange={event => setMaximumFee(event.target.value)} /></label>
      <Button variant="outline" disabled={busy || !ready || !payer || !/^[1-9][0-9]*$/.test(maximumFee) || !!quoted} onClick={quote}>Get separately funded recovery quote</Button>
      {quoted && <div><p>Quoted fee: {quoted.quote.intent.quote?.networkFee} native base units. Maximum: {quoted.quote.intent.budget.maximumNetworkFee}. Return destination: {quoted.quote.intent.quote?.disclosure?.refundDestination}.</p><Button variant="outline" disabled={busy} onClick={() => { setQuoted(null); setAcknowledged(false) }}>Discard unsigned quote</Button></div>}
    </>}
    <label className="flex gap-2"><input type="checkbox" checked={acknowledged} disabled={busy || !!saved} onChange={event => setAcknowledged(event.target.checked)} />I authorize the selected recovery action and its displayed fee limits.</label>
    {saved ? <div className="flex gap-2"><Button disabled={busy || !ready} onClick={() => send(saved.kind)}>Retry saved recovery request</Button><Button variant="outline" disabled={busy || !ready} onClick={reconcile}>Check saved request status</Button></div> : <div className="flex gap-2">
      {!receipt.recovery?.childOperationId && <Button disabled={busy || !ready || !acknowledged || !!quoted} onClick={() => send('original')}>Recover within original limits</Button>}
      {quoted && <Button disabled={busy || !acknowledged} onClick={() => send('linked')}>Authorize quoted recovery</Button>}
    </div>}
    {saved?.kind === 'linked' && <Button variant="outline" disabled={busy || !ready} onClick={invalidate}>Withdraw unadmitted recovery quote</Button>}
    {error && <p role="alert" className="text-destructive">{error}</p>}
  </section>
}
