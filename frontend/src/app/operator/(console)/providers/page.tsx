'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertTriangle, KeyRound, Pencil, ShieldCheck, Webhook } from 'lucide-react'
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
import {
  KYC_PROVIDER_DISPLAY_NAME_MAX_LENGTH,
  type KycProviderProfileResponse,
  type KycProviderProfileUpdate,
} from '@/lib/operator-contracts'

export default function OperatorProvidersPage() {
  const [profiles, setProfiles] = useState<KycProviderProfileResponse[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<KycProviderProfileResponse | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setProfiles(await operatorRequest<KycProviderProfileResponse[]>('kyc/providers'))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Provider profiles could not be loaded.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void load() }, [load])

  const readyCount = useMemo(() => profiles.filter(isProviderReady).length, [profiles])

  return (
    <div className="space-y-6">
      <OperatorPageHeader
        eyebrow="Identity policy"
        title="KYC providers"
        description="Choose which deployed adapters tenants may use. Credentials stay in the host secret store; this console only changes secret-free policy and reports configuration state."
        onRefresh={() => void load()}
        refreshing={loading}
      />

      <div className="flex flex-wrap gap-2" aria-label="Provider summary">
        <StatusPill tone={readyCount > 0 ? 'ready' : 'attention'}>{readyCount} ready</StatusPill>
        <StatusPill tone="neutral">{profiles.filter((item) => item.enabled).length} enabled</StatusPill>
        <StatusPill tone="neutral">{profiles.length} known</StatusPill>
      </div>

      {error && <OperatorError message={error} onRetry={() => void load()} />}
      {loading && profiles.length === 0 ? <OperatorLoading label="Loading provider profiles" /> : (
        <div className="grid gap-4 lg:grid-cols-2">
          {profiles.map((profile) => (
            <ProviderCard key={profile.providerKey} profile={profile} onEdit={() => setEditing(profile)} />
          ))}
          {!loading && profiles.length === 0 && (
            <div className="border border-dashed border-border p-6 text-sm text-muted-foreground lg:col-span-2">
              No KYC provider profiles are installed. Deploy or seed an adapter profile before tenants can select one.
            </div>
          )}
        </div>
      )}

      {editing && (
        <ProviderEditor
          profile={editing}
          onClose={() => setEditing(null)}
          onSaved={async () => { setEditing(null); await load() }}
        />
      )}
    </div>
  )
}

function ProviderCard({ profile, onEdit }: { profile: KycProviderProfileResponse; onEdit: () => void }) {
  const ready = isProviderReady(profile)
  const manual = profile.adapterKey === 'manual'
  return (
    <FlatCard>
      <CardHeader>
        <div className="flex items-start justify-between gap-4">
          <div className="min-w-0">
            <p className="truncate font-mono text-[10px] uppercase tracking-[0.14em] text-muted-foreground">{profile.providerKey}</p>
            <CardTitle className="mt-1 text-lg">{profile.displayName}</CardTitle>
          </div>
          <StatusPill tone={ready ? 'ready' : profile.enabled ? 'attention' : 'neutral'}>
            {ready ? 'Ready' : profile.enabled ? humanizeReadiness(profile.readinessCode) : 'Disabled'}
          </StatusPill>
        </div>
      </CardHeader>
      <CardContent className="space-y-4 text-sm">
        <dl className="grid gap-3 sm:grid-cols-2">
          <ProviderDatum label="Adapter" value={profile.adapterKey} />
          <ProviderDatum label="Assurance" value={profile.assuranceLevel} />
          <ProviderDatum label="Policy" value={profile.policyVersion} />
          <ProviderDatum label="Profile revision" value={String(profile.version)} />
          <ProviderDatum label="Trust revision" value={String(profile.trustRevision)} />
        </dl>
        <div className="grid gap-2 border-y py-3 text-xs sm:grid-cols-2">
          <SecretState icon={KeyRound} configured={profile.apiKeyConfigured} requiredMissing={profile.readinessCode === 'SECRETS_NOT_CONFIGURED'} label="Provider API key" />
          <SecretState icon={Webhook} configured={profile.webhookSecretConfigured} requiredMissing={profile.readinessCode === 'SECRETS_NOT_CONFIGURED'} label="Webhook secret" />
        </div>
        {profile.requiredConfigurationKeys.length > 0 && (
          <div className="border-b pb-3">
            <p className="text-xs font-medium">Host configuration</p>
            <ul className="mt-2 grid gap-2" aria-label={`${profile.displayName} host configuration`}>
              {profile.requiredConfigurationKeys.map((key) => {
                const missing = profile.missingConfigurationKeys.includes(key)
                return (
                  <li key={key} className="flex min-w-0 items-center gap-2 text-xs">
                    <code className="min-w-0 flex-1 break-all">{key}</code>
                    <StatusPill tone={missing ? 'attention' : 'ready'}>{missing ? 'Missing' : 'Configured'}</StatusPill>
                  </li>
                )
              })}
            </ul>
            <p className="mt-2 text-xs leading-5 text-muted-foreground">Values are write-only host secrets and are never returned to this console.</p>
          </div>
        )}
        {manual && (
          <p className="flex gap-2 border border-amber-700 bg-amber-50 p-3 text-xs leading-5 text-amber-900 dark:bg-amber-950/30 dark:text-amber-200">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
            Manual verification is a Development-only adapter. Production readiness remains blocked by the API.
          </p>
        )}
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <p className="text-xs text-muted-foreground">Updated {formatOperatorDate(profile.updatedAt)}</p>
          <Button variant="outline" className="min-h-11 rounded-none" onClick={onEdit}>
            <Pencil className="mr-2 h-4 w-4" aria-hidden="true" /> Edit policy
          </Button>
        </div>
      </CardContent>
    </FlatCard>
  )
}

function ProviderDatum({ label, value }: { label: string; value: string }) {
  return (
    <div className="border-t pt-2">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="mt-1 break-all font-mono text-xs">{value || 'Not set'}</dd>
    </div>
  )
}

function SecretState({ icon: Icon, configured, requiredMissing, label }: { icon: typeof KeyRound; configured: boolean; requiredMissing: boolean; label: string }) {
  return (
    <div className="flex items-center gap-2">
      <Icon className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
      <span>{label}</span>
      <span className="ml-auto"><StatusPill tone={configured ? 'ready' : requiredMissing ? 'attention' : 'neutral'}>{configured ? 'Configured' : requiredMissing ? 'Missing' : 'Not configured'}</StatusPill></span>
    </div>
  )
}

function ProviderEditor({
  profile,
  onClose,
  onSaved,
}: {
  profile: KycProviderProfileResponse
  onClose: () => void
  onSaved: () => Promise<void>
}) {
  const [draft, setDraft] = useState<KycProviderProfileUpdate>({
    displayName: profile.displayName,
    adapterKey: profile.adapterKey,
    enabled: profile.enabled,
    policyVersion: profile.policyVersion,
    assuranceLevel: profile.assuranceLevel,
    expectedVersion: profile.version,
  })
  const [confirming, setConfirming] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const impactsVerification =
    draft.enabled !== profile.enabled ||
    draft.adapterKey !== profile.adapterKey ||
    draft.policyVersion !== profile.policyVersion ||
    draft.assuranceLevel !== profile.assuranceLevel

  async function save() {
    setSaving(true)
    setError(null)
    try {
      await operatorRequest<KycProviderProfileResponse>(`kyc/providers/${encodeURIComponent(profile.providerKey)}`, {
        method: 'PUT',
        body: draft,
      })
      await onSaved()
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'The provider policy could not be saved.'
      setError(reason instanceof OperatorRequestError && reason.status === 409
        ? 'This provider changed while you were editing. Close and refresh before trying again.'
        : message)
      setConfirming(false)
    } finally {
      setSaving(false)
    }
  }

  function requestSave() {
    if (impactsVerification && !confirming) {
      setConfirming(true)
      return
    }
    void save()
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !saving) onClose() }}>
      <DialogContent className="max-h-[calc(100vh-2rem)] overflow-y-auto rounded-none sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>Edit {profile.displayName}</DialogTitle>
          <DialogDescription>
            Policy fields are stored by Azoa. Provider credentials remain in Railway or your host secret store and are never returned here.
          </DialogDescription>
        </DialogHeader>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Display name" id="provider-name">
            <Input id="provider-name" value={draft.displayName} onChange={(event) => { setDraft({ ...draft, displayName: event.target.value }); setConfirming(false) }} maxLength={KYC_PROVIDER_DISPLAY_NAME_MAX_LENGTH} className="min-h-11 rounded-none" />
          </Field>
          <Field label="Adapter key" id="adapter-key">
            <Input id="adapter-key" value={draft.adapterKey} readOnly aria-readonly="true" className="min-h-11 rounded-none bg-muted font-mono text-xs" />
            <p className="text-xs text-muted-foreground">Adapter identity is fixed by the installed provider profile.</p>
          </Field>
          <Field label="Policy version" id="policy-version">
            <Input id="policy-version" value={draft.policyVersion} onChange={(event) => { setDraft({ ...draft, policyVersion: event.target.value }); setConfirming(false) }} maxLength={64} className="min-h-11 rounded-none font-mono text-xs" />
          </Field>
          <Field label="Assurance level" id="assurance-level">
            <Input id="assurance-level" value={draft.assuranceLevel} onChange={(event) => { setDraft({ ...draft, assuranceLevel: event.target.value }); setConfirming(false) }} maxLength={64} className="min-h-11 rounded-none" />
          </Field>
          <label className="flex min-h-12 items-center gap-3 border p-3 sm:col-span-2" htmlFor="provider-enabled">
            <input
              id="provider-enabled"
              type="checkbox"
              checked={draft.enabled}
              onChange={(event) => { setDraft({ ...draft, enabled: event.target.checked }); setConfirming(false) }}
              className="h-5 w-5 accent-primary"
            />
            <span>
              <span className="block font-medium">Allow tenants to select this provider</span>
              <span className="block text-xs text-muted-foreground">Enabling still fails closed if the adapter or its environment credentials are not ready.</span>
            </span>
          </label>
        </div>

        {confirming && (
          <div role="alert" className="border border-amber-700 bg-amber-50 p-4 text-sm text-amber-950 dark:bg-amber-950/30 dark:text-amber-100">
            <p className="font-semibold">Confirm verification policy change</p>
            <p className="mt-2 leading-6">Existing attempts and approvals tied to the prior provider policy become stale. Affected people must verify again before KYC-gated value actions resume.</p>
          </div>
        )}
        {error && <OperatorError message={error} />}
        <DialogFooter className="rounded-none">
          <Button variant="outline" className="min-h-11 rounded-none" onClick={onClose} disabled={saving}>Cancel</Button>
          <Button className="min-h-11 rounded-none" onClick={requestSave} disabled={saving || !draft.displayName.trim() || !draft.adapterKey.trim() || !draft.policyVersion.trim() || !draft.assuranceLevel.trim()}>
            <ShieldCheck className="mr-2 h-4 w-4" aria-hidden="true" />
            {saving ? 'Saving policy…' : confirming ? 'Confirm and save' : impactsVerification ? 'Review change' : 'Save policy'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function Field({ label, id, children }: { label: string; id: string; children: React.ReactNode }) {
  return <div className="space-y-2"><Label htmlFor={id}>{label}</Label>{children}</div>
}
