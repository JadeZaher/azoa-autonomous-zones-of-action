'use client'

// Operator chain recovery: DEX settle-review, WTT inspection/recovery + stage keys, Ethereum prepared-transaction recovery.
import { useState } from 'react'
import type { EthereumPreparationView, SwapOperationView, WttOperationView, WttStageKeyReadinessReport, WttStageKeyRewrapReport } from 'azoa-sdk'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { FlatCard, OperatorError, StatusPill, formatOperatorDate } from '@/components/operator/operator-ui'
import { useOperatorMutation, useOperatorResource } from '@/lib/use-operator-resource'
import { operatorRequest } from '@/lib/operator-client'

const HEX64 = /^[0-9a-f]{64}$/

export function DexSettleReview() {
  const mutation = useOperatorMutation()
  const [operationId, setOperationId] = useState('')
  const [expectedVersion, setExpectedVersion] = useState('')
  const [reason, setReason] = useState('')
  const [settled, setSettled] = useState<SwapOperationView | null>(null)
  const valid = HEX64.test(operationId.trim()) && /^\d+$/.test(expectedVersion) && reason.trim().length >= 10

  async function settle() {
    setSettled(null)
    const result = await mutation.run<SwapOperationView>(`dex/operations/${operationId.trim()}/settle-review`, 'POST',
      { expectedVersion: Number(expectedVersion), reason: reason.trim() })
    if (result) setSettled(result)
  }

  return <FlatCard>
    <CardHeader><CardTitle>Swap parked for review</CardTitle></CardHeader>
    <CardContent className="space-y-3 text-sm">
      <p className="text-muted-foreground">Settles a swap the node parked for review at its exact version. The node re-checks the settlement evidence; a refusal leaves the swap parked.</p>
      {mutation.error && <OperatorError message={mutation.error} />}
      <div className="grid gap-3 sm:grid-cols-3">
        <div className="space-y-1.5 sm:col-span-2"><Label htmlFor="dex-op">Swap operation id (64 hex)</Label><Input id="dex-op" value={operationId} onChange={event => setOperationId(event.target.value)} /></div>
        <div className="space-y-1.5"><Label htmlFor="dex-version">Expected version</Label><Input id="dex-version" inputMode="numeric" value={expectedVersion} onChange={event => setExpectedVersion(event.target.value)} /></div>
      </div>
      <Label htmlFor="dex-reason">Review notes (at least 10 characters)</Label>
      <Input id="dex-reason" value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} />
      <Button disabled={mutation.busy || !valid} onClick={() => void settle()}>Settle review</Button>
      {settled && <p role="status">Swap {settled.operationId} is now {settled.state} ({settled.stage}), version {settled.version}.</p>}
    </CardContent>
  </FlatCard>
}

export function WttOperator() {
  const readiness = useOperatorResource<WttStageKeyReadinessReport>('wtt/stage-keys/readiness', 'Stage-key readiness could not be read.')
  const mutation = useOperatorMutation()
  const [operationId, setOperationId] = useState('')
  const [view, setView] = useState<WttOperationView | null>(null)
  const [inspectError, setInspectError] = useState<string | null>(null)
  const [rewrap, setRewrap] = useState<WttStageKeyRewrapReport | null>(null)
  const id = operationId.trim()

  async function inspect() {
    setInspectError(null)
    setView(null)
    try { setView(await operatorRequest<WttOperationView>(`wtt/operations/${id}`)) }
    catch (cause) { setInspectError(cause instanceof Error ? cause.message : 'The operation could not be read.') }
  }

  async function act(action: 'reconcile' | 'refetch-proof' | 'resume-proof') {
    const result = await mutation.run<WttOperationView>(`wtt/operations/${id}/${action}`, 'POST', {})
    if (result) setView(result)
  }

  async function runRewrap() {
    const result = await mutation.run<WttStageKeyRewrapReport>('wtt/stage-keys/rewrap', 'POST', {})
    if (result) { setRewrap(result); await readiness.reload() }
  }

  const report = readiness.data
  return <FlatCard>
    <CardHeader><CardTitle>Token transfers (WTT)</CardTitle></CardHeader>
    <CardContent className="space-y-4 text-sm">
      {(readiness.error || mutation.error || inspectError) && <OperatorError message={(mutation.error ?? inspectError ?? readiness.error)!} />}
      <div className="space-y-1 border border-border p-3">
        <div className="flex flex-wrap items-center gap-2">
          <strong>Stage-key keyring</strong>
          {report && <StatusPill tone={report.status === 'Ready' ? 'ready' : 'attention'}>{report.status}</StatusPill>}
        </div>
        {report && <p className="text-muted-foreground">{report.detail} Checked {formatOperatorDate(report.checkedAt)}.</p>}
        {report?.status !== 'Ready' && report && <p>New stage-key transfers are refused until the keyring is ready.</p>}
        <Button variant="outline" disabled={mutation.busy || !report?.rewrapPending} onClick={() => void runRewrap()}>Run one rewrap pass</Button>
        {rewrap && <p role="status">Rewrapped {rewrap.rewrapped}, conflicted {rewrap.conflicted}; {rewrap.rewrapPending ? 'more remain — run again.' : 'none pending.'}</p>}
      </div>
      <div className="space-y-2">
        <Label htmlFor="wtt-op">Transfer operation id (64 hex)</Label>
        <div className="flex flex-wrap gap-2">
          <Input id="wtt-op" className="max-w-xl" value={operationId} onChange={event => setOperationId(event.target.value)} />
          <Button variant="outline" disabled={!HEX64.test(id)} onClick={() => void inspect()}>Inspect</Button>
        </div>
      </div>
      {view && <div className="space-y-2 border border-border p-3">
        <p><strong>{view.publicState}</strong> · {view.state} · phase {view.phase} · version {view.version}</p>
        <p>Proof verified: {view.proofVerified ? 'yes' : 'no'} · next: {view.nextAction}</p>
        {view.detail && <p className="text-muted-foreground">{view.detail}</p>}
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" disabled={mutation.busy} onClick={() => void act('reconcile')}>Reconcile</Button>
          <Button variant="outline" disabled={mutation.busy} onClick={() => void act('refetch-proof')}>Re-fetch guardian proof</Button>
          <Button variant="outline" disabled={mutation.busy} onClick={() => void act('resume-proof')}>Resume proof verification</Button>
        </div>
      </div>}
    </CardContent>
  </FlatCard>
}

type Network = 'Devnet' | 'Testnet' | 'Mainnet'

export function EthereumRecovery() {
  const [network, setNetwork] = useState<Network>('Devnet')
  const list = useOperatorResource<EthereumPreparationView[]>(`ethereum/recovery/${network}`, 'Ethereum preparations could not be read.')
  const mutation = useOperatorMutation()
  const [sender, setSender] = useState('')
  const [selected, setSelected] = useState<EthereumPreparationView | null>(null)
  const [cancel, setCancel] = useState(false)
  const [maximumFeePerGas, setMaximumFeePerGas] = useState('')
  const [reason, setReason] = useState('')
  const [notice, setNotice] = useState<string | null>(null)

  async function activate() {
    setNotice(null)
    const result = await mutation.run<boolean>(`ethereum/recovery/${network}/activate`, 'POST', { sender: sender.trim() })
    if (result === true) setNotice('Platform signer activated.')
    else if (result === false) mutation.setError('The node did not activate the signer.')
  }

  async function recover() {
    if (!selected) return
    setNotice(null)
    const result = await mutation.run<EthereumPreparationView>(`ethereum/recovery/${network}/${selected.id}`, 'POST',
      { cancel, maximumFeePerGas: maximumFeePerGas.trim(), reason: reason.trim() })
    if (result) { setNotice(`Preparation ${result.id} is now ${result.state}.`); setSelected(null); await list.reload() }
  }

  const rows = list.data ?? []
  // ETHEREUM_RECOVERY_UNAVAILABLE is a configuration state (no Ethereum rail on this node), not a failure to retry (QA M2).
  const unavailable = list.errorCode === 'ETHEREUM_RECOVERY_UNAVAILABLE'
  return <FlatCard>
    <CardHeader><CardTitle>Ethereum prepared transactions</CardTitle></CardHeader>
    <CardContent className="space-y-4 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        {(['Devnet', 'Testnet', 'Mainnet'] as const).map(option => (
          <Button key={option} size="sm" variant={option === network ? 'default' : 'outline'} onClick={() => setNetwork(option)}>{option}</Button>
        ))}
      </div>
      {unavailable && <p role="status" className="text-muted-foreground">{list.error}</p>}
      {((list.error && !unavailable) || mutation.error) && <OperatorError message={(mutation.error ?? list.error)!} onRetry={() => void list.reload()} />}
      {notice && <p role="status">{notice}</p>}
      {list.error ? null : rows.length === 0 && !list.loading ? <p className="text-muted-foreground">No platform preparations need recovery on {network}.</p> : rows.map(row => (
        <div key={row.id} className="space-y-1 border border-border p-3">
          <p><strong>{row.state}</strong> · nonce {row.nonce} · sender {row.sender}</p>
          {row.transactionId && <p className="break-all">Transaction {row.transactionId}</p>}
          <p className="text-muted-foreground">Updated {formatOperatorDate(row.updatedAt)}</p>
          <Button variant="outline" size="sm" onClick={() => setSelected(row)}>Replace or cancel</Button>
        </div>
      ))}
      {selected && <form className="space-y-2 border border-border p-3" onSubmit={event => { event.preventDefault(); void recover() }}>
        <p className="font-medium">Recover {selected.id}</p>
        <label className="flex items-center gap-2"><input type="checkbox" checked={cancel} onChange={event => setCancel(event.target.checked)} />Cancel (self-send at the same nonce) instead of re-pricing</label>
        <Label htmlFor="eth-fee">Maximum fee per gas (wei)</Label>
        <Input id="eth-fee" inputMode="numeric" value={maximumFeePerGas} onChange={event => setMaximumFeePerGas(event.target.value)} />
        <Label htmlFor="eth-reason">Reason (at least 10 characters)</Label>
        <Input id="eth-reason" value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} />
        <div className="flex gap-2">
          <Button type="submit" disabled={mutation.busy || !/^\d+$/.test(maximumFeePerGas.trim()) || reason.trim().length < 10}>Submit recovery</Button>
          <Button type="button" variant="outline" onClick={() => setSelected(null)}>Close</Button>
        </div>
      </form>}
      {!unavailable && <div className="flex flex-wrap items-end gap-2 border-t pt-3">
        <div className="space-y-1.5"><Label htmlFor="eth-sender">Activate platform signer (address)</Label><Input id="eth-sender" className="w-96" value={sender} onChange={event => setSender(event.target.value)} /></div>
        <Button variant="outline" disabled={mutation.busy || !/^0x[0-9a-fA-F]{40}$/.test(sender.trim())} onClick={() => void activate()}>Activate</Button>
      </div>}
    </CardContent>
  </FlatCard>
}
