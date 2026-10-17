'use client'

import { useEffect, useRef, useState } from 'react'
import type { HoldCommandView, HoldConsentCommand, HoldRecoveryCommand, HoldOpeningAcceptance, HoldRequestResolution } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'

type SavedCommand = { operationId: string } & (
  | { kind: 'Transfer' | 'Acceptance'; request: HoldConsentCommand }
  | { kind: 'Replace' | 'Cancel' | 'WorkflowOpeningReplace' | 'WorkflowOpeningWithdraw'; request: HoldRecoveryCommand }
  | { kind: 'WorkflowOpeningAccept'; request: HoldOpeningAcceptance })

export function HoldRecoveryPanel({ actorId, scope }: { actorId: string; scope: string }) {
  return <ScopedHoldRecoveryPanel key={scope} actorId={actorId} scope={scope} />
}

function ScopedHoldRecoveryPanel({ actorId, scope }: { actorId: string; scope: string }) {
  const key = `azoa.holdRecovery.v1.${scope}`
  const [rows, setRows] = useState<HoldCommandView[]>([])
  const [saved, setSaved] = useState<SavedCommand[]>([])
  const journal = useRef<SavedCommand[]>([])
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [nextOffset, setNextOffset] = useState<number | null>(null)
  const alive = useRef(false)
  const locked = useRef(false)

  function merge(row: HoldCommandView) {
    if (alive.current) setRows(current => [row, ...current.filter(value => value.operationId !== row.operationId)])
  }
  function persist(commands: SavedCommand[]) {
    localStorage.setItem(key, JSON.stringify(commands))
    journal.current = commands
    if (alive.current) setSaved(commands)
  }
  function archive(command: SavedCommand) {
    localStorage.setItem(`${key}.${command.request.requestId}`, JSON.stringify(command))
    persist(journal.current.filter(value => value.request.requestId !== command.request.requestId))
  }
  async function discover(offset = 0) {
    const result = await azoa.api.listHoldCommands(offset, 25)
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    for (const row of result.value.items) merge(row)
    setNextOffset(result.value.nextOffset ?? null)
  }
  useEffect(() => {
    alive.current = true
    try {
      const stored = JSON.parse(localStorage.getItem(key) ?? '[]') as SavedCommand[]
      if (!Array.isArray(stored) || stored.some(value => !value.operationId || !value.request?.requestId)) throw new Error('Saved Hold requests are invalid. Preserve browser storage for recovery.')
      journal.current = stored; setSaved(stored); setReady(true)
      void discover().catch(failure => { if (alive.current) setError(String(failure)) })
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Recovery storage is unavailable.') }
    return () => { alive.current = false }
    // Scope changes remount this component, including its asynchronous response guards.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])

  async function run(work: () => Promise<void>) {
    if (!ready || locked.current) return
    locked.current = true; setBusy(true); setError(null)
    try { await work() } catch (failure) {
      if (alive.current) setError(failure instanceof Error ? failure.message : 'Keep the saved request and reconcile its outcome.')
    } finally { locked.current = false; if (alive.current) setBusy(false) }
  }
  async function send(command: SavedCommand) {
    const id = command.operationId
    const result = command.kind === 'Transfer' ? await azoa.api.continueHoldCommand(id, command.request)
      : command.kind === 'Acceptance' ? await azoa.api.acceptHoldRecipient(id, command.request)
      : command.kind === 'WorkflowOpeningAccept' ? await azoa.api.acceptHoldOpening(id, command.request)
      : command.kind === 'WorkflowOpeningReplace' ? await azoa.api.replaceHoldOpening(id, command.request)
      : command.kind === 'WorkflowOpeningWithdraw' ? await azoa.api.withdrawHoldOpening(id, command.request)
      : command.kind === 'Replace' ? await azoa.api.replaceHoldCommand(id, command.request)
      : await azoa.api.cancelHoldCommand(id, command.request)
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    if (result.value.operationId !== id) throw new Error('Response does not match the saved Hold operation.')
    merge(result.value); archive(command)
  }
  async function reconcile(command: SavedCommand) {
    const id = command.operationId
    const result = command.kind === 'WorkflowOpeningAccept' ? await azoa.api.reconcileHoldOpening(id, command.request)
      : command.kind === 'WorkflowOpeningReplace' || command.kind === 'WorkflowOpeningWithdraw'
        ? await azoa.api.reconcileHoldOpeningRecovery(id, { kind: command.kind, request: command.request })
        : command.kind === 'Transfer' || command.kind === 'Acceptance'
          ? await azoa.api.reconcileHoldCommand(id, { kind: command.kind, consent: command.request })
          : await azoa.api.reconcileHoldCommand(id, { kind: command.kind, recovery: command.request })
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    const value: HoldRequestResolution = result.value
    if (value.command && value.command.operationId !== id) throw new Error('Recovery evidence belongs to another Hold operation.')
    if (value.command) merge(value.command)
    if (value.state === 'Applied' || value.state === 'Obsolete') archive(command)
    else setError('This request remains unresolved. Retry its exact saved request or reconcile again. Refreshing costs or withdrawing an unaccepted opening can fence an expired pending consent.')
  }
  async function create(row: HoldCommandView, kind: SavedCommand['kind']) {
    const requestId = crypto.randomUUID()
    let command: SavedCommand
    if (kind === 'WorkflowOpeningAccept') {
      if (!row.mandateDraftHash) throw new Error('The exact opening disclosure is unavailable.')
      command = { operationId: row.operationId, kind, request: { requestId, version: row.version, draftHash: row.mandateDraftHash } }
    } else if (kind === 'Transfer' || kind === 'Acceptance') {
      if (!row.costQuote) throw new Error('The exact cost quote is unavailable.')
      command = { operationId: row.operationId, kind, request: { requestId, version: row.version,
        quoteId: row.costQuote.quoteId, maximumNetworkFeeBaseUnits: kind === 'Transfer'
          ? row.costQuote.transferNetworkFeeBaseUnits : row.costQuote.acceptanceNetworkFeeBaseUnits } }
    } else command = { operationId: row.operationId, kind, request: { requestId, version: row.version, reason: 'Participant requested this exact recovery action.' } }
    localStorage.setItem(`${key}.${requestId}`, JSON.stringify(command))
    persist([...journal.current, command])
    await send(command)
  }

  return <section className="space-y-4">
    <p>Review exact custody commands, their costs and recovery evidence. Refresh only observes previously authorized movement.</p>
    <Button disabled={!ready || busy} onClick={() => void run(() => discover())}>Refresh Hold discovery</Button>
    {saved.map(command => <div key={command.request.requestId} className="rounded border p-3 space-y-2">
      <p className="text-sm break-all">Saved {command.kind}: {command.operationId}</p>
      <Button disabled={busy} onClick={() => void run(() => send(command))}>Retry saved {command.kind}</Button>{' '}
      <Button disabled={busy} variant="outline" onClick={() => void run(() => reconcile(command))}>Reconcile saved {command.kind}</Button>
    </div>)}
    {rows.map(row => <article key={row.operationId} className="rounded border p-4 space-y-3">
      <p className="font-medium">{row.holdId}: {row.kind} - {row.state}</p>
      <p className="text-sm">{row.quantityBaseUnits} base units of {row.asset.assetId}, {row.asset.chainType} {row.asset.network}</p>
      <p className="text-xs break-all">Operation {row.operationId}. Beneficiary {row.beneficiaryUserId}</p>
      {row.detail && <p>{row.detail}</p>}
      {row.costQuote && <div className="text-sm space-y-1">
        <p>Quote {row.costQuote.quoteId}. Expires {row.costQuote.expiresAt}. Native currency: {row.costQuote.nativeCurrency} ({row.costQuote.nativeDecimals} decimals).</p>
        <p>Transfer network cost: {row.costQuote.transferNetworkFeeBaseUnits} atomic units.</p>
        {row.costQuote.transferCosts && <p>Payer {row.costQuote.transferCosts.payerAddress}; refund destination {row.costQuote.transferCosts.refundRecipientAddress}. Locked balance {row.costQuote.transferCosts.lockedBalanceBaseUnits}; nonrefundable protocol cost {row.costQuote.transferCosts.nonrefundableProtocolCostBaseUnits} atomic units.</p>}
        <p>Recipient acceptance network cost {row.costQuote.acceptanceNetworkFeeBaseUnits}; minimum balance {row.costQuote.acceptanceMinimumBalanceBaseUnits} atomic units. Only the actual recipient may authorize acceptance.</p>
      </div>}
      {row.mandateTerms && <div className="text-sm">
        <p>Automatic release to {row.mandateTerms.recipient.address}, after {row.mandateTerms.notBefore} and before {row.mandateTerms.expiresAt}.</p>
        <p>Maximum native atomic units: network {row.mandateTerms.maximumCosts.networkFeeBaseUnits}, locked balance {row.mandateTerms.maximumCosts.lockedBalanceBaseUnits}, nonrefundable protocol cost {row.mandateTerms.maximumCosts.nonrefundableProtocolCostBaseUnits}. Payer {row.mandateTerms.maximumCosts.payerAddress}; refund destination {row.mandateTerms.maximumCosts.refundRecipientAddress}.</p>
      </div>}
      <div className="flex flex-wrap gap-2">
        <Button disabled={busy} variant="outline" onClick={() => void run(async () => {
          const result = await azoa.api.refreshHoldCommand(row.operationId)
          if (!alive.current) return
          if (isOk(result)) { if (result.value.operationId !== row.operationId) throw new Error('Unexpected Hold identity.'); merge(result.value) }
          else setError(result.error.message)
        })}>Observe progress</Button>
        {row.state === 'AwaitingConsent' && row.mandateDraftHash && row.actorUserId === actorId && <>
          <Button disabled={busy} onClick={() => void run(() => create(row, 'WorkflowOpeningAccept'))}>Accept opening and release limits</Button>
          <Button disabled={busy} variant="outline" onClick={() => void run(() => create(row, 'WorkflowOpeningReplace'))}>Review fresh opening costs</Button>
          <Button disabled={busy} variant="outline" onClick={() => void run(() => create(row, 'WorkflowOpeningWithdraw'))}>Withdraw unaccepted opening</Button>
        </>}
        {row.costQuote && !row.mandateDraftHash && !['Completed', 'Cancelled', 'Submitted'].includes(row.state) && <>
          {row.actorUserId === actorId && <Button disabled={busy} onClick={() => void run(() => create(row, 'Transfer'))}>Authorize these transfer costs</Button>}
          {row.beneficiaryUserId === actorId && !row.costQuote.recipientReady && row.acceptanceState !== 'Confirmed' && <Button disabled={busy} onClick={() => void run(() => create(row, 'Acceptance'))}>Authorize recipient acceptance</Button>}
          {row.actorUserId === actorId && <>
            <Button disabled={busy} variant="outline" onClick={() => void run(() => create(row, 'Replace'))}>Review replacement costs</Button>
            <Button disabled={busy} variant="outline" onClick={() => void run(() => create(row, 'Cancel'))}>Cancel if no value moved</Button>
          </>}
        </>}
      </div>
    </article>)}
    {nextOffset !== null && <Button disabled={busy} onClick={() => void run(() => discover(nextOffset))}>Load more Hold commands</Button>}
    {error && <p role="alert" className="text-destructive">{error}</p>}
  </section>
}
