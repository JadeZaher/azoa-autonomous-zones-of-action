'use client'

// D-3 stuck client-signed operations: the outcome is re-proven from chain state on resolve, never taken from the form.
import { useState } from 'react'
import type { BlockchainOperationResult, StuckClientSignedOperationView } from 'azoa-sdk'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { FlatCard, OperatorError, StatusPill, formatOperatorDate } from '@/components/operator/operator-ui'
import { useOperatorMutation, useOperatorResource } from '@/lib/use-operator-resource'

export function StuckSignatures() {
  const { data, error, loading, reload } = useOperatorResource<StuckClientSignedOperationView[]>('blockchain-operations/stuck?limit=100', 'Stuck operations could not be read.')
  const mutation = useOperatorMutation()
  const [selected, setSelected] = useState<StuckClientSignedOperationView | null>(null)
  const [reason, setReason] = useState('')
  const [transactionId, setTransactionId] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const rows = data ?? []

  async function resolve() {
    if (!selected) return
    setNotice(null)
    const body = transactionId.trim() ? { reason: reason.trim(), transactionId: transactionId.trim() } : { reason: reason.trim() }
    const result = await mutation.run<BlockchainOperationResult>(`blockchain-operations/${selected.operationId}/resolve`, 'POST', body)
    if (result) {
      setNotice(`Operation ${selected.operationId} is now ${result.status ?? 'unchanged'} after the chain re-check.`)
      setSelected(null)
      setReason('')
      setTransactionId('')
      await reload()
    }
  }

  return <FlatCard>
    <CardHeader><CardTitle>Waiting on an external signature</CardTitle></CardHeader>
    <CardContent className="space-y-4">
      <p className="text-sm text-muted-foreground">Client-signed operations left unsigned past their window or parked for a person. Resolving re-reads the chain: an expired Algorand/Solana plan closes as abandoned; an Ethereum plan stays parked unless another transaction consumed its nonce.</p>
      {(error || mutation.error) && <OperatorError message={(mutation.error ?? error)!} onRetry={() => void reload()} />}
      {notice && <p role="status" className="text-sm">{notice}</p>}
      {loading ? <p className="text-sm text-muted-foreground">Reading…</p> : rows.length === 0 ? <p className="text-sm text-muted-foreground">No client-signed operation is stuck.</p> : rows.map(row => (
        <div key={row.operationId} className="space-y-1 border border-border p-3 text-sm">
          <div className="flex flex-wrap items-center gap-2">
            <strong>{row.chainType} {row.network}</strong>
            <StatusPill tone="attention">{row.status}</StatusPill>
            {row.submitted && <StatusPill tone="neutral">signed bytes received</StatusPill>}
          </div>
          <p className="break-all">Operation {row.operationId} · signer {row.signer} · created {formatOperatorDate(row.createdDate)}</p>
          {row.transactionId && <p className="break-all">Transaction {row.transactionId}</p>}
          {row.detail && <p className="text-muted-foreground">{row.detail}</p>}
          <Button variant="outline" disabled={mutation.busy} onClick={() => setSelected(row)}>Re-check and resolve</Button>
        </div>
      ))}
      {selected && <form className="space-y-3 border border-border p-4" onSubmit={event => { event.preventDefault(); void resolve() }}>
        <p className="font-medium">Resolve {selected.operationId}</p>
        <Label htmlFor="stuck-tx">Transaction id you found (optional; the node verifies it)</Label>
        <Input id="stuck-tx" value={transactionId} maxLength={128} onChange={event => setTransactionId(event.target.value)} />
        <Label htmlFor="stuck-reason">What you checked (at least 10 characters)</Label>
        <Input id="stuck-reason" value={reason} minLength={10} maxLength={1000} required onChange={event => setReason(event.target.value)} />
        <div className="flex gap-2">
          <Button type="submit" disabled={mutation.busy || reason.trim().length < 10}>Re-check chain and resolve</Button>
          <Button type="button" variant="outline" onClick={() => setSelected(null)}>Cancel</Button>
        </div>
      </form>}
    </CardContent>
  </FlatCard>
}
