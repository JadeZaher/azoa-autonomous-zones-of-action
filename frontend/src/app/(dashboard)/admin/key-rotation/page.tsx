'use client'

// Operator console for wallet wrapping-key rotation (KeyRotationController) — re-wraps every wallet's ciphertext under a new key. Dangerous/irreversible-in-effect; gated behind type-to-confirm.
import { useState, useCallback } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import type { KeyRotationReport } from 'azoa-sdk'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter,
} from '@/components/ui/dialog'
import { ErrorBanner } from '@/components/shared/error-banner'
import { AlertTriangle, ShieldAlert, CheckCircle2 } from 'lucide-react'

const CONFIRM_PHRASE = 'ROTATE ALL KEYS'

interface RotationFormState {
  newEncryptionKey: string
  confirmEncryptionKey: string
  confirmPhrase: string
}

const DEFAULT_FORM: RotationFormState = {
  newEncryptionKey: '',
  confirmEncryptionKey: '',
  confirmPhrase: '',
}

function ReportSummary({ report }: { report: KeyRotationReport }) {
  if (report.rolledBack) {
    return (
      <Card className="border-destructive/60 bg-destructive/5">
        <CardHeader className="pb-3">
          <div className="flex items-center gap-2">
            <ShieldAlert className="h-5 w-5 shrink-0 text-destructive" />
            <CardTitle className="text-sm text-destructive">ROLLBACK — rotation aborted, review required</CardTitle>
          </div>
          <CardDescription>
            A failure occurred mid-batch and the operation was rolled back. No wallets were left in a
            partially-rotated state, but the encryption key was NOT changed. Investigate server logs before retrying.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <RotationCounts report={report} />
        </CardContent>
      </Card>
    )
  }

  return (
    <Card className="border-primary/40 bg-primary/5">
      <CardHeader className="pb-3">
        <div className="flex items-center gap-2">
          <CheckCircle2 className="h-5 w-5 shrink-0 text-primary" />
          <CardTitle className="text-sm">Rotation complete</CardTitle>
        </div>
        <CardDescription>All wallet wrapping keys have been re-wrapped under the new encryption key.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <RotationCounts report={report} />
        <Separator />
        <div className="rounded-md border border-amber-500/40 bg-amber-500/10 p-3">
          <p className="flex items-center gap-2 text-xs font-medium text-amber-700 dark:text-amber-400">
            <AlertTriangle className="h-3.5 w-3.5 shrink-0" />
            Rotation is not complete until the server config is updated
          </p>
          <p className="mt-1 text-xs text-muted-foreground">
            The database now holds ciphertext wrapped under the new key. You must update{' '}
            <code className="rounded bg-muted px-1 py-0.5 font-mono">AZOA:WalletEncryptionKey</code> on the API
            server (and any node hosts) to match the key you just entered, then restart every instance. Until
            the restart completes, the running server is still decrypting with the OLD key and wallet
            operations will fail.
          </p>
        </div>
      </CardContent>
    </Card>
  )
}

function RotationCounts({ report }: { report: KeyRotationReport }) {
  const items: Array<{ label: string; value: number }> = [
    { label: 'Total', value: report.total },
    { label: 'Rewrapped', value: report.rewrapped },
    { label: 'Already rotated', value: report.alreadyRotated },
    { label: 'Skipped', value: report.skipped },
  ]
  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
      {items.map((item) => (
        <div key={item.label} className="rounded-md border border-border bg-background p-3">
          <p className="text-[11px] uppercase tracking-wider text-muted-foreground">{item.label}</p>
          <p className="mt-1 text-xl font-semibold tabular-nums">{item.value}</p>
        </div>
      ))}
    </div>
  )
}

export default function KeyRotationPage() {
  useAzoaAuth()
  const [form, setForm] = useState<RotationFormState>(DEFAULT_FORM)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [report, setReport] = useState<KeyRotationReport | null>(null)

  const set = <K extends keyof RotationFormState>(field: K) => (val: string) =>
    setForm((prev) => ({ ...prev, [field]: val }))

  const keysMatch = form.newEncryptionKey.length > 0 && form.newEncryptionKey === form.confirmEncryptionKey
  const canOpenConfirm = keysMatch
  const canExecute = canOpenConfirm && form.confirmPhrase === CONFIRM_PHRASE

  const runRotation = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const result = await azoa.api.rotateWalletKeys({ newEncryptionKey: form.newEncryptionKey })
      if (isOk(result)) {
        setReport(result.value)
        setConfirmOpen(false)
        setForm(DEFAULT_FORM)
      } else {
        setError(result.error.message)
      }
    } catch (err) {
      // assertNonEmpty throws synchronously if the key is blank — surfaced the same way as an SDK error.
      setError(err instanceof Error ? err.message : 'Failed to start key rotation.')
    } finally {
      setLoading(false)
    }
  }, [form.newEncryptionKey])

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Key Rotation</h1>
        <p className="text-sm text-muted-foreground">
          Re-wrap every wallet's ciphertext under a new master encryption key
        </p>
      </div>

      <Card className="border-destructive/50">
        <CardHeader className="pb-3">
          <div className="flex items-center gap-2">
            <AlertTriangle className="h-5 w-5 shrink-0 text-destructive" />
            <CardTitle className="text-sm text-destructive">Dangerous operator action</CardTitle>
          </div>
          <CardDescription>
            This re-wraps <strong>every</strong> wallet's wrapping key in the system under the encryption key you
            provide below. It is a heavy, whole-database batch operation that touches live production key
            material. It is idempotent and rolls back automatically on failure, but it should only be run during
            a planned maintenance window, and only by someone who has already staged the new encryption key
            for the server config.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="new-key">New encryption key</Label>
              <Input
                id="new-key"
                type="password"
                autoComplete="new-password"
                value={form.newEncryptionKey}
                onChange={(e) => set('newEncryptionKey')(e.target.value)}
                placeholder="Paste the new AZOA:WalletEncryptionKey value"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="confirm-key">Confirm new encryption key</Label>
              <Input
                id="confirm-key"
                type="password"
                autoComplete="new-password"
                value={form.confirmEncryptionKey}
                onChange={(e) => set('confirmEncryptionKey')(e.target.value)}
                placeholder="Re-enter to confirm"
              />
              {form.confirmEncryptionKey.length > 0 && !keysMatch && (
                <p className="text-xs text-destructive">Keys do not match.</p>
              )}
            </div>
          </div>

          {error && <ErrorBanner message={error} />}

          <div className="flex justify-end">
            <Button
              variant="destructive"
              disabled={!canOpenConfirm || loading}
              onClick={() => setConfirmOpen(true)}
            >
              Rotate all wallet keys…
            </Button>
          </div>
        </CardContent>
      </Card>

      {report && <ReportSummary report={report} />}

      <Dialog
        open={confirmOpen}
        onOpenChange={(open) => {
          setConfirmOpen(open)
          if (!open) set('confirmPhrase')('')
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2 text-destructive">
              <ShieldAlert className="h-4 w-4" /> Confirm key rotation
            </DialogTitle>
            <DialogDescription>
              This will immediately re-wrap every wallet's ciphertext in the database. To proceed, type{' '}
              <strong className="font-mono">{CONFIRM_PHRASE}</strong> below.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-1.5">
            <Label htmlFor="confirm-phrase">Type &quot;{CONFIRM_PHRASE}&quot; to confirm</Label>
            <Input
              id="confirm-phrase"
              value={form.confirmPhrase}
              onChange={(e) => set('confirmPhrase')(e.target.value)}
              placeholder={CONFIRM_PHRASE}
              autoComplete="off"
            />
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirmOpen(false)} disabled={loading}>
              Cancel
            </Button>
            <Button variant="destructive" onClick={runRotation} disabled={!canExecute || loading}>
              {loading ? 'Rotating…' : 'Rotate all wallet keys'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {loading && (
        <p className="flex items-center gap-2 text-xs text-muted-foreground">
          <Badge variant="secondary" className="animate-pulse">In progress</Badge>
          Rotating wallet keys — this can take a while for large wallet counts, do not close this tab.
        </p>
      )}
    </div>
  )
}
