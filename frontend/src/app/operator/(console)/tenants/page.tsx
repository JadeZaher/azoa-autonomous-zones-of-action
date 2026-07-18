'use client'

import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import { Building2, Search, Settings2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  FlatCard,
  OperatorError,
  OperatorLoading,
  OperatorPageHeader,
  StatusPill,
  formatOperatorDate,
} from '@/components/operator/operator-ui'
import { isProviderReady, humanizeReadiness } from '@/lib/kyc-provider-state'
import { operatorRequest, OperatorRequestError } from '@/lib/operator-client'
import type {
  CursorPage,
  KycProviderProfileResponse,
  OperatorTenantKycSummaryResponse,
} from '@/lib/operator-contracts'

export default function OperatorTenantsPage() {
  const [tenants, setTenants] = useState<OperatorTenantKycSummaryResponse[]>([])
  const [providers, setProviders] = useState<KycProviderProfileResponse[]>([])
  const [query, setQuery] = useState('')
  const [activeSearch, setActiveSearch] = useState('')
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [editing, setEditing] = useState<OperatorTenantKycSummaryResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async ({ cursor, search = '', append = false }: { cursor?: string; search?: string; append?: boolean } = {}) => {
    setLoading(true)
    setError(null)
    try {
      const params = new URLSearchParams({ limit: '50' })
      if (cursor) params.set('cursor', cursor)
      if (search) params.set('search', search)
      const [tenantData, providerData] = await Promise.all([
        operatorRequest<CursorPage<OperatorTenantKycSummaryResponse>>(`tenants?${params.toString()}`),
        operatorRequest<KycProviderProfileResponse[]>('kyc/providers'),
      ])
      setTenants((current) => append ? [...current, ...tenantData.items] : tenantData.items)
      setNextCursor(tenantData.nextCursor ?? null)
      setProviders(providerData)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Tenant KYC configuration could not be loaded.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void load() }, [load])

  const eligibleProviders = useMemo(() => providers.filter(isProviderReady), [providers])

  function submitSearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const search = query.trim()
    setActiveSearch(search)
    void load({ search })
  }

  return (
    <div className="space-y-6">
      <OperatorPageHeader
        eyebrow="Tenant boundaries"
        title="Tenant KYC configuration"
        description="See each tenant's provider choice and intervene when requested. Tenants can select only provider profiles that this node has enabled and made ready."
        onRefresh={() => void load({ search: activeSearch })}
        refreshing={loading}
      />
      <div className="grid gap-3 sm:grid-cols-3">
        <Summary label="Loaded tenants" value={tenants.length} />
        <Summary label="Configured in view" value={tenants.filter((tenant) => tenant.providerKey).length} />
        <Summary label="Selectable providers" value={eligibleProviders.length} />
      </div>
      <form onSubmit={submitSearch} className="flex max-w-2xl flex-col gap-2 sm:flex-row">
        <div className="relative flex-1">
          <Label htmlFor="tenant-search" className="sr-only">Search tenants</Label>
          <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input id="tenant-search" type="search" value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search tenant name or id" maxLength={100} className="min-h-11 rounded-none pl-10" />
        </div>
        <Button type="submit" variant="outline" className="min-h-11 rounded-none" disabled={loading}>Search node</Button>
      </form>

      {error && <OperatorError message={error} onRetry={() => void load({ search: activeSearch })} />}
      {loading && tenants.length === 0 ? <OperatorLoading label="Loading tenant configuration" /> : (
        <div className="grid gap-4 lg:grid-cols-2">
          {tenants.map((tenant) => (
            <TenantCard key={tenant.tenantId} tenant={tenant} onConfigure={() => setEditing(tenant)} />
          ))}
          {!loading && tenants.length === 0 && (
            <div className="border border-dashed border-border p-6 text-sm text-muted-foreground lg:col-span-2">
              {activeSearch ? 'No tenant matches this search.' : 'No active tenants are available for KYC configuration.'}
            </div>
          )}
        </div>
      )}
      {nextCursor && (
        <div className="flex justify-center border-t pt-5">
          <Button variant="outline" className="min-h-11 rounded-none" disabled={loading} onClick={() => void load({ cursor: nextCursor, search: activeSearch, append: true })}>
            {loading ? 'Loading more...' : 'Load more tenants'}
          </Button>
        </div>
      )}

      {editing && (
        <TenantAssignment
          tenant={editing}
          providers={eligibleProviders}
          onClose={() => setEditing(null)}
          onSaved={async () => { setEditing(null); await load({ search: activeSearch }) }}
        />
      )}
    </div>
  )
}

function Summary({ label, value }: { label: string; value: number }) {
  return <div className="border border-border bg-card p-4"><p className="font-mono text-[10px] uppercase tracking-[0.15em] text-muted-foreground">{label}</p><p className="mt-1 text-2xl font-semibold tabular-nums">{value}</p></div>
}

function TenantCard({ tenant, onConfigure }: { tenant: OperatorTenantKycSummaryResponse; onConfigure: () => void }) {
  const selectedReady = Boolean(tenant.providerKey && tenant.providerEnabled && tenant.providerAvailable && tenant.readinessCode === 'READY')
  return (
    <FlatCard>
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <p className="truncate font-mono text-[10px] uppercase tracking-[0.14em] text-muted-foreground" title={tenant.tenantId}>{tenant.tenantId.slice(0, 8)}…</p>
            <CardTitle className="mt-1 text-lg">{tenant.username}</CardTitle>
          </div>
          <StatusPill tone={selectedReady ? 'ready' : tenant.providerKey ? 'attention' : 'neutral'}>
            {selectedReady ? 'Ready' : tenant.providerKey ? humanizeReadiness(tenant.readinessCode) : 'No provider'}
          </StatusPill>
        </div>
      </CardHeader>
      <CardContent className="space-y-4 text-sm">
        <div className="border-y py-3">
          <p className="text-xs text-muted-foreground">Selected provider</p>
          <p className="mt-1 font-medium">{tenant.providerDisplayName ?? tenant.providerKey ?? 'Not selected'}</p>
        </div>
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <p className="text-xs text-muted-foreground">Selection revision {tenant.selectionVersion} · {formatOperatorDate(tenant.updatedAt)}</p>
          <Button variant="outline" className="min-h-11 rounded-none" onClick={onConfigure}>
            <Settings2 className="mr-2 h-4 w-4" aria-hidden="true" /> Configure
          </Button>
        </div>
      </CardContent>
    </FlatCard>
  )
}

function TenantAssignment({
  tenant,
  providers,
  onClose,
  onSaved,
}: {
  tenant: OperatorTenantKycSummaryResponse
  providers: KycProviderProfileResponse[]
  onClose: () => void
  onSaved: () => Promise<void>
}) {
  const [providerKey, setProviderKey] = useState(tenant.providerKey ?? '')
  const [confirmed, setConfirmed] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const changed = providerKey !== (tenant.providerKey ?? '')

  async function save() {
    setSaving(true)
    setError(null)
    try {
      await operatorRequest<OperatorTenantKycSummaryResponse>(`tenants/${tenant.tenantId}/kyc-provider`, {
        method: 'PUT',
        body: { providerKey, expectedVersion: tenant.selectionVersion },
      })
      await onSaved()
    } catch (reason) {
      setError(reason instanceof OperatorRequestError && reason.status === 409
        ? 'This tenant configuration changed elsewhere. Close this dialog and refresh before trying again.'
        : reason instanceof Error ? reason.message : 'The tenant provider could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !saving) onClose() }}>
      <DialogContent className="rounded-none sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Configure {tenant.username}</DialogTitle>
          <DialogDescription>The tenant can also make this choice from its own KYC settings. Only currently ready providers appear here.</DialogDescription>
        </DialogHeader>
        <div className="space-y-2">
          <Label htmlFor="tenant-provider">KYC provider</Label>
          <select
            id="tenant-provider"
            value={providerKey}
            onChange={(event) => { setProviderKey(event.target.value); setConfirmed(false) }}
            className="min-h-11 w-full rounded-none border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <option value="" disabled>Select a ready provider</option>
            {providers.map((provider) => <option key={provider.providerKey} value={provider.providerKey}>{provider.displayName} · {provider.assuranceLevel}</option>)}
          </select>
          {providers.length === 0 && <p className="text-xs text-destructive">No enabled provider is ready. Configure a provider before assigning tenants.</p>}
        </div>
        {changed && (
          <label className="flex items-start gap-3 border border-amber-700 bg-amber-50 p-4 text-sm leading-6 text-amber-950 dark:bg-amber-950/30 dark:text-amber-100">
            <input type="checkbox" checked={confirmed} onChange={(event) => setConfirmed(event.target.checked)} className="mt-1 h-5 w-5 shrink-0 accent-primary" />
            <span>I understand that changing the provider makes active attempts and prior approvals stale, so affected people must verify again.</span>
          </label>
        )}
        {error && <OperatorError message={error} />}
        <DialogFooter className="rounded-none">
          <Button variant="outline" className="min-h-11 rounded-none" onClick={onClose} disabled={saving}>Cancel</Button>
          <Button className="min-h-11 rounded-none" onClick={() => void save()} disabled={saving || !changed || !providerKey || !confirmed}>
            <Building2 className="mr-2 h-4 w-4" aria-hidden="true" />
            {saving ? 'Saving assignment…' : 'Confirm provider change'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
