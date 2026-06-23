'use client'

import { useState, useEffect } from 'react'
import { ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
  DialogFooter,
} from '@/components/ui/dialog'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ResultDisplay } from '@/components/shared/result-display'
import { azoa, isOk } from '@/lib/azoa'

// ─── Types ───

interface OdkItem {
  id: string
  name: string
  description?: string
  targetChain?: string
  isActive?: boolean
  publicKey?: string
  avatarId?: string
  [key: string]: unknown
}

// ─── ODK List Row ───

function OdkRow({
  item,
  open,
  onToggle,
  onDeleted,
}: {
  item: OdkItem
  open: boolean
  onToggle: () => void
  onDeleted: () => void
}) {
  return (
    <div>
      <button
        type="button"
        onClick={onToggle}
        className={`flex w-full items-center gap-3 px-3 py-2.5 text-left transition-colors hover:bg-accent/50 ${open ? 'bg-accent/40' : ''}`}
      >
        <ChevronRight className={`h-4 w-4 shrink-0 text-muted-foreground transition-transform ${open ? 'rotate-90' : ''}`} />
        <span className="flex-1 truncate text-sm font-medium">{item.name}</span>
        {item.description && (
          <span className="hidden max-w-[260px] truncate text-xs text-muted-foreground md:inline">
            {item.description}
          </span>
        )}
        {item.targetChain && (
          <Badge variant="outline" className="text-xs">{item.targetChain}</Badge>
        )}
        <Badge
          className={`text-xs ${
            item.isActive
              ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300'
              : 'bg-muted text-muted-foreground'
          }`}
        >
          {item.isActive ? 'Active' : 'Inactive'}
        </Badge>
      </button>
      {open && (
        <div className="border-t bg-background px-4 py-4">
          <OdkDetail odk={item} onDeleted={onDeleted} />
        </div>
      )}
    </div>
  )
}

// ─── Create ODK Dialog ───

function CreateOdkDialog({ onCreated }: { onCreated: () => void }) {
  const [open, setOpen] = useState(false)
  const [form, setForm] = useState({
    name: '',
    description: '',
    publicKey: '',
    avatarId: '',
  })
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const set = (field: string, value: string) =>
    setForm((prev) => ({ ...prev, [field]: value }))

  const handleCreate = async () => {
    if (!form.name) return
    setLoading(true)
    setError(null)
    try {
      const body: Record<string, string> = { name: form.name }
      if (form.description) body.description = form.description
      if (form.publicKey) body.publicKey = form.publicKey
      if (form.avatarId) body.avatarId = form.avatarId

      const result = await azoa.api.request('POST', '/api/starodk', body)
      if (isOk(result)) {
        setOpen(false)
        setForm({ name: '', description: '', publicKey: '', avatarId: '' })
        onCreated()
      } else {
        setError((result as { error: { message: string } }).error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoading(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button size="sm">Create ODK</Button>} />
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Create STAR ODK</DialogTitle>
        </DialogHeader>
        <div className="space-y-3 py-2">
          <div className="space-y-1.5">
            <Label>Name *</Label>
            <Input
              placeholder="ODK name"
              value={form.name}
              onChange={(e) => set('name', e.target.value)}
            />
          </div>
          <div className="space-y-1.5">
            <Label>Description</Label>
            <Textarea
              placeholder="Optional description"
              value={form.description}
              onChange={(e) => set('description', e.target.value)}
              rows={2}
            />
          </div>
          <div className="space-y-1.5">
            <Label>Public Key (optional)</Label>
            <Input
              placeholder="Public key"
              value={form.publicKey}
              onChange={(e) => set('publicKey', e.target.value)}
            />
          </div>
          <div className="space-y-1.5">
            <Label>Avatar ID (optional)</Label>
            <Input
              placeholder="Avatar ID"
              value={form.avatarId}
              onChange={(e) => set('avatarId', e.target.value)}
            />
          </div>
          {error && <p className="text-sm text-destructive">{error}</p>}
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={() => setOpen(false)}>
            Cancel
          </Button>
          <Button onClick={handleCreate} disabled={loading || !form.name}>
            {loading ? 'Creating...' : 'Create'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

// ─── Generate dApp Dialog ───

function GenerateDAppDialog({ odkId }: { odkId: string }) {
  const [open, setOpen] = useState(false)
  const [targetChain, setTargetChain] = useState('')
  const [boundHolonIds, setBoundHolonIds] = useState('')
  const [configJson, setConfigJson] = useState('')
  const [generatedCode, setGeneratedCode] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleGenerate = async () => {
    setLoading(true)
    setError(null)
    setGeneratedCode(null)
    try {
      const body: Record<string, unknown> = { targetChain }
      if (boundHolonIds.trim()) {
        body.boundHolonIds = boundHolonIds
          .split(',')
          .map((s) => s.trim())
          .filter(Boolean)
      }
      if (configJson.trim()) {
        try {
          body.config = JSON.parse(configJson)
        } catch {
          body.config = configJson
        }
      }
      const result = await azoa.api.request<{ code?: string; generatedCode?: string }>(
        'POST',
        `/api/starodk/${odkId}/generate`,
        body
      )
      if (isOk(result)) {
        const val = result.value
        setGeneratedCode(
          typeof val === 'string'
            ? val
            : val?.code ?? val?.generatedCode ?? JSON.stringify(val, null, 2)
        )
      } else {
        setError((result as { error: { message: string } }).error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoading(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button size="sm" variant="outline">Generate dApp</Button>} />
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Generate dApp</DialogTitle>
        </DialogHeader>
        <div className="space-y-3 py-2">
          <div className="space-y-1.5">
            <Label>Target Chain</Label>
            <Select value={targetChain} onValueChange={(v) => setTargetChain(v ?? 'Algorand')}>
              <SelectTrigger>
                <SelectValue placeholder="Select chain" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="Algorand">Algorand</SelectItem>
                <SelectItem value="Solana">Solana</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="space-y-1.5">
            <Label>Bound Holon IDs</Label>
            <Input
              placeholder="id1, id2, id3 (comma-separated)"
              value={boundHolonIds}
              onChange={(e) => setBoundHolonIds(e.target.value)}
            />
          </div>
          <div className="space-y-1.5">
            <Label>Config JSON</Label>
            <Textarea
              placeholder='{"key": "value"}'
              value={configJson}
              onChange={(e) => setConfigJson(e.target.value)}
              rows={4}
              className="font-mono text-xs"
            />
          </div>
          {error && <p className="text-sm text-destructive">{error}</p>}
          {generatedCode && (
            <div className="space-y-1">
              <Label>Generated Code</Label>
              <pre className="max-h-64 overflow-auto rounded-md bg-muted p-3 text-xs font-mono whitespace-pre-wrap">
                {generatedCode}
              </pre>
            </div>
          )}
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={() => setOpen(false)}>
            Close
          </Button>
          <Button onClick={handleGenerate} disabled={loading || !targetChain}>
            {loading ? 'Generating...' : 'Generate'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

// ─── Selected ODK Detail ───

function OdkDetail({
  odk,
  onDeleted,
}: {
  odk: OdkItem
  onDeleted: () => void
}) {
  const [deployResult, setDeployResult] = useState<unknown>(null)
  const [deployLoading, setDeployLoading] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [deleteLoading, setDeleteLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleDeploy = async () => {
    setDeployLoading(true)
    setError(null)
    setDeployResult(null)
    try {
      const result = await azoa.api.request('POST', `/api/starodk/${odk.id}/deploy`)
      if (isOk(result)) {
        setDeployResult(result.value)
      } else {
        setError((result as { error: { message: string } }).error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setDeployLoading(false)
    }
  }

  const handleDelete = async () => {
    setDeleteLoading(true)
    setError(null)
    try {
      const result = await azoa.api.request('DELETE', `/api/starodk/${odk.id}`)
      if (isOk(result)) {
        onDeleted()
      } else {
        setError((result as { error: { message: string } }).error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setDeleteLoading(false)
      setConfirmDelete(false)
    }
  }

  return (
    <div className="space-y-4">
      {/* Action buttons row */}
      <div className="flex flex-wrap gap-2">
        <GenerateDAppDialog odkId={odk.id} />
        <Button
          size="sm"
          variant="outline"
          onClick={handleDeploy}
          disabled={deployLoading}
        >
          {deployLoading ? 'Deploying...' : 'Deploy'}
        </Button>
        {!confirmDelete ? (
          <Button
            size="sm"
            variant="destructive"
            onClick={() => setConfirmDelete(true)}
          >
            Delete
          </Button>
        ) : (
          <div className="flex gap-1">
            <Button
              size="sm"
              variant="destructive"
              onClick={handleDelete}
              disabled={deleteLoading}
            >
              {deleteLoading ? 'Deleting...' : 'Confirm'}
            </Button>
            <Button
              size="sm"
              variant="ghost"
              onClick={() => setConfirmDelete(false)}
            >
              Cancel
            </Button>
          </div>
        )}
      </div>

      {error && <p className="text-sm text-destructive">{error}</p>}

      <div className="rounded-md bg-muted p-3 text-xs">
        <JsonViewer data={odk} />
      </div>

      {deployResult !== null && deployResult !== undefined && (
        <>
          <Separator />
          <div className="space-y-1">
            <p className="text-xs text-muted-foreground">Deploy Result</p>
            <ResultDisplay result={deployResult} />
          </div>
        </>
      )}
    </div>
  )
}

// ─── Page ───

export default function StarOdkPage() {
  const [odks, setOdks] = useState<OdkItem[]>([])
  const [selected, setSelected] = useState<OdkItem | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const loadOdks = async () => {
    setLoading(true)
    setError(null)
    try {
      const result = await azoa.api.request<OdkItem[]>('GET', '/api/starodk')
      if (isOk(result)) {
        setOdks(result.value)
      } else {
        setError((result as { error: { message: string } }).error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    loadOdks()
  }, [])

  const handleDeleted = () => {
    setSelected(null)
    loadOdks()
  }

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">STAR ODK</h1>
        <p className="text-sm text-muted-foreground">
          Manage on-chain development kits and generate dApp scaffolding
        </p>
      </div>

      {/* Action row on top */}
      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" variant="outline" onClick={loadOdks} disabled={loading}>
          {loading ? 'Refreshing...' : 'Refresh'}
        </Button>
        <CreateOdkDialog onCreated={loadOdks} />
        <span className="text-sm text-muted-foreground">{odks.length} ODKs</span>
      </div>

      {error && <p className="text-sm text-destructive">{error}</p>}

      {/* Full-width list with inline-expanding detail */}
      <div className="divide-y rounded-md border">
        {odks.map((item) => (
          <OdkRow
            key={item.id}
            item={item}
            open={selected?.id === item.id}
            onToggle={() => setSelected(selected?.id === item.id ? null : item)}
            onDeleted={handleDeleted}
          />
        ))}
        {odks.length === 0 && !loading && (
          <p className="px-3 py-6 text-center text-sm text-muted-foreground">
            No ODKs found. Create one above.
          </p>
        )}
      </div>
    </div>
  )
}
