'use client'

// Wallet-identity migration conflicts: clearing one binding is audited and pinned to the observed migration version.
import { useState } from 'react'
import type { WalletIdentityConflict, WalletIdentityMigrationView } from 'azoa-sdk'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { FlatCard, OperatorError, OperatorLoading, OperatorPageHeader, StatusPill } from '@/components/operator/operator-ui'
import { useOperatorMutation, useOperatorResource } from '@/lib/use-operator-resource'

export default function OperatorWalletIdentitiesPage() {
  const { data, error, loading, reload } = useOperatorResource<WalletIdentityMigrationView>('wallet-identities', 'Wallet identity status could not be read.')
  const mutation = useOperatorMutation()
  const [selected, setSelected] = useState<WalletIdentityConflict | null>(null)
  const [reason, setReason] = useState('')
  const [notice, setNotice] = useState<string | null>(null)

  async function clear() {
    if (!data || !selected) return
    setNotice(null)
    // The node answers `AZOAResult<bool>`; the updated status comes from re-reading the view.
    const cleared = await mutation.run<boolean>('wallet-identities/clear-conflicting-binding', 'POST',
      { version: data.version, userId: selected.userId, expectedIdentity: selected.expectedIdentity, reason: reason.trim() })
    if (cleared === null) return
    if (cleared !== true) {
      mutation.setError('The node did not confirm the binding was cleared. Refresh and check the conflict list.')
      return
    }
    setSelected(null)
    setReason('')
    const view = await reload()
    setNotice(view
      ? `Cleared. Migration is ${view.status} with ${view.conflicts.length} conflict(s) left.`
      : 'Cleared. The updated migration status could not be read; refresh to check it.')
  }

  return (
    <div className="flex flex-col gap-8">
      <OperatorPageHeader eyebrow="Identity" title="Wallet identity migration"
        description="Canonical wallet sign-in identities are migrated under a lease. A conflict means two accounts claim one wallet; clearing removes the stale binding only."
        onRefresh={() => void reload()} refreshing={loading} />
      {(error || mutation.error) && <OperatorError message={(mutation.error ?? error)!} onRetry={() => void reload()} />}
      {notice && <p role="status" className="text-sm">{notice}</p>}
      {loading || !data ? <OperatorLoading label="Reading migration status" /> : (
        <FlatCard>
          <CardHeader><CardTitle className="flex items-center gap-2">Status <StatusPill tone={data.conflicts.length === 0 ? 'ready' : 'attention'}>{data.status}</StatusPill></CardTitle></CardHeader>
          <CardContent className="space-y-3 text-sm">
            <p>Version {data.version} · {data.conflicts.length} conflict(s)</p>
            {data.conflicts.map(conflict => (
              <div key={`${conflict.userId}:${conflict.expectedIdentity}`} className="space-y-1 border border-border p-3">
                <p className="break-all">User {conflict.userId} · identity {conflict.expectedIdentity}</p>
                <p className="text-muted-foreground">{conflict.problem}</p>
                <Button variant="outline" size="sm" onClick={() => setSelected(conflict)}>Clear binding…</Button>
              </div>
            ))}
            {selected && <form className="space-y-2 border border-border p-3" onSubmit={event => { event.preventDefault(); void clear() }}>
              <Label htmlFor="identity-reason">Why this binding is stale (at least 10 characters)</Label>
              <Input id="identity-reason" value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} />
              <div className="flex gap-2">
                <Button type="submit" disabled={mutation.busy || reason.trim().length < 10}>Clear conflicting binding</Button>
                <Button type="button" variant="outline" onClick={() => setSelected(null)}>Cancel</Button>
              </div>
            </form>}
          </CardContent>
        </FlatCard>
      )}
    </div>
  )
}
