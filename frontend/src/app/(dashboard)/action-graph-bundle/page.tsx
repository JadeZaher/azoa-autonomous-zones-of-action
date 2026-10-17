'use client'

import { useState, useEffect } from 'react'
import { ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
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
import { DeploymentConsole } from '@/components/deployments/deployment-console'
import { EcosystemTreeFlow, type EcosystemTree } from '@/components/ecosystem-tree/ecosystem-tree-flow'
import { azoa, isOk } from '@/lib/azoa'

// ─── Types ───

interface BundleItem {
  id: string
  name: string
  description?: string
  targetChain?: string
  isActive?: boolean
  publicKey?: string
  userId?: string
  [key: string]: unknown
}

// ─── Bundle List Row ───

function BundleRow({
  item,
  open,
  onToggle,
  onDeleted,
}: {
  item: BundleItem
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
          <BundleDetail bundle={item} onDeleted={onDeleted} />
        </div>
      )}
    </div>
  )
}

// ─── Create Bundle Dialog ───

function CreateBundleDialog({ onCreated }: { onCreated: () => void }) {
  const [open, setOpen] = useState(false)
  const [form, setForm] = useState({
    name: '',
    description: '',
    publicKey: '',
    userId: '',
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
      if (form.userId) body.userId = form.userId

      const result = await azoa.api.request('POST', '/api/actiongraphbundle', body)
      if (isOk(result)) {
        setOpen(false)
        setForm({ name: '', description: '', publicKey: '', userId: '' })
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
      <DialogTrigger render={<Button size="sm">Create bundle</Button>} />
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Create action graph bundle</DialogTitle>
        </DialogHeader>
        <div className="space-y-3 py-2">
          <div className="space-y-1.5">
            <Label>Name *</Label>
            <Input
              placeholder="Bundle name"
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
            <Label>User ID (optional)</Label>
            <Input
              placeholder="User ID"
              value={form.userId}
              onChange={(e) => set('userId', e.target.value)}
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

function EcosystemSection({ bundleId }: { bundleId: string }) {
  const [tree, setTree] = useState<EcosystemTree | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [refId, setRefId] = useState('')
  const [parentNodeId, setParentNodeId] = useState('')
  const [label, setLabel] = useState('')
  const [attaching, setAttaching] = useState(false)

  const load = async () => {
    setLoading(true)
    setError(null)
    try {
      const result = await azoa.api.request<EcosystemTree>('GET', `/api/actiongraphbundle/${bundleId}/ecosystem`)
      if (isOk(result)) setTree(result.value)
      else setError((result as { error: { message: string } }).error.message)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bundleId])

  const handleAttach = async () => {
    if (!refId.trim()) return
    setAttaching(true)
    setError(null)
    try {
      const body: Record<string, unknown> = { refId: refId.trim(), refKind: 'DappSeries' }
      if (parentNodeId.trim()) body.parentNodeId = parentNodeId.trim()
      if (label.trim()) body.label = label.trim()
      const result = await azoa.api.request<EcosystemTree>(
        'POST',
        `/api/actiongraphbundle/${bundleId}/ecosystem/dapp-series`,
        body
      )
      if (isOk(result)) {
        setTree(result.value)
        setRefId('')
        setParentNodeId('')
        setLabel('')
      } else {
        setError((result as { error: { message: string } }).error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setAttaching(false)
    }
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <p className="text-sm font-medium">Ecosystem Tree</p>
        <Button size="sm" variant="ghost" onClick={load} disabled={loading}>
          {loading ? 'Loading...' : 'Refresh'}
        </Button>
      </div>

      <div className="grid gap-2 sm:grid-cols-4">
        <Input placeholder="DappSeries ID *" value={refId} onChange={(e) => setRefId(e.target.value)} />
        <Input placeholder="Parent node ID (optional)" value={parentNodeId} onChange={(e) => setParentNodeId(e.target.value)} />
        <Input placeholder="Label (optional)" value={label} onChange={(e) => setLabel(e.target.value)} />
        <Button size="sm" onClick={handleAttach} disabled={attaching || !refId.trim()}>
          {attaching ? 'Attaching...' : 'Attach DappSeries'}
        </Button>
      </div>

      {error && <p className="text-sm text-destructive">{error}</p>}

      {tree ? (
        <EcosystemTreeFlow tree={tree} />
      ) : (
        !loading && (
          <p className="py-6 text-center text-sm text-muted-foreground">
            No ecosystem yet. Attach a DappSeries to create one.
          </p>
        )
      )}
    </div>
  )
}

// ─── Selected Bundle Detail ───

function BundleDetail({
  bundle,
  onDeleted,
}: {
  bundle: BundleItem
  onDeleted: () => void
}) {
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [deleteLoading, setDeleteLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleDelete = async () => {
    setDeleteLoading(true)
    setError(null)
    try {
      const result = await azoa.api.request('DELETE', `/api/actiongraphbundle/${bundle.id}`)
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
        <JsonViewer data={bundle} />
      </div>

      <Separator />
      <EcosystemSection bundleId={bundle.id} />

      <DeploymentConsole bundleId={bundle.id} />
    </div>
  )
}

// ─── Page ───

export default function ActionGraphBundlePage() {
  const [bundles, setBundles] = useState<BundleItem[]>([])
  const [selected, setSelected] = useState<BundleItem | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const loadBundles = async () => {
    setLoading(true)
    setError(null)
    try {
      const result = await azoa.api.request<BundleItem[]>('GET', '/api/actiongraphbundle')
      if (isOk(result)) {
        setBundles(result.value)
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
    loadBundles()
  }, [])

  const handleDeleted = () => {
    setSelected(null)
    loadBundles()
  }

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Action graph bundles</h1>
        <p className="text-sm text-muted-foreground">
          Manage on-chain development kits and generate dApp scaffolding
        </p>
      </div>

      {/* Action row on top */}
      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" variant="outline" onClick={loadBundles} disabled={loading}>
          {loading ? 'Refreshing...' : 'Refresh'}
        </Button>
        <CreateBundleDialog onCreated={loadBundles} />
        <span className="text-sm text-muted-foreground">{bundles.length} bundles</span>
      </div>

      {error && <p className="text-sm text-destructive">{error}</p>}

      {/* Full-width list with inline-expanding detail */}
      <div className="divide-y rounded-md border">
        {bundles.map((item) => (
          <BundleRow
            key={item.id}
            item={item}
            open={selected?.id === item.id}
            onToggle={() => setSelected(selected?.id === item.id ? null : item)}
            onDeleted={handleDeleted}
          />
        ))}
        {bundles.length === 0 && !loading && (
          <p className="px-3 py-6 text-center text-sm text-muted-foreground">
            No bundles found. Create one above.
          </p>
        )}
      </div>
    </div>
  )
}
