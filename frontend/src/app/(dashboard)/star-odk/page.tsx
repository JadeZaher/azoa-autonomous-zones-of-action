'use client'

import { useState, useEffect } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
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
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
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
import { oasis, isOk } from '@/lib/oasis'

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

// ─── ODK List ───

function OdkTable({
  items,
  selected,
  onSelect,
}: {
  items: OdkItem[]
  selected: OdkItem | null
  onSelect: (item: OdkItem) => void
}) {
  if (items.length === 0) {
    return <p className="text-sm text-muted-foreground">No ODKs found.</p>
  }
  return (
    <div className="overflow-x-auto">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Name</TableHead>
            <TableHead>Description</TableHead>
            <TableHead>Chain</TableHead>
            <TableHead>Active</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {items.map((item) => (
            <TableRow
              key={item.id}
              className={`cursor-pointer ${selected?.id === item.id ? 'bg-muted' : ''}`}
              onClick={() => onSelect(item)}
            >
              <TableCell className="font-medium">{item.name}</TableCell>
              <TableCell className="text-muted-foreground max-w-[200px] truncate">
                {item.description ?? '—'}
              </TableCell>
              <TableCell>
                {item.targetChain ? (
                  <Badge variant="outline" className="text-xs">
                    {item.targetChain}
                  </Badge>
                ) : (
                  '—'
                )}
              </TableCell>
              <TableCell>
                <Badge
                  className={`text-xs ${
                    item.isActive
                      ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300'
                      : 'bg-muted text-muted-foreground'
                  }`}
                >
                  {item.isActive ? 'Active' : 'Inactive'}
                </Badge>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
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

      const result = await oasis.api.request('POST', '/api/starodk', body)
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
      const result = await oasis.api.request<{ code?: string; generatedCode?: string }>(
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
      const result = await oasis.api.request('POST', `/api/starodk/${odk.id}/deploy`)
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
      const result = await oasis.api.request('DELETE', `/api/starodk/${odk.id}`)
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
    <Card>
      <CardHeader className="pb-3">
        <div className="flex items-center justify-between flex-wrap gap-2">
          <CardTitle className="text-sm">{odk.name}</CardTitle>
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
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
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
      </CardContent>
    </Card>
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
      const result = await oasis.api.request<OdkItem[]>('GET', '/api/starodk')
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
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight tracking-tight">STAR ODK</h1>
        <p className="text-sm text-muted-foreground">
          Manage on-chain development kits and generate dApp scaffolding
        </p>
      </div>

      {/* ODK List Card */}
      <Card>
        <CardHeader className="flex flex-row items-center justify-between pb-3">
          <CardTitle className="text-sm">
            ODK List
            {odks.length > 0 && (
              <span className="ml-2 text-sm font-normal text-muted-foreground">
                ({odks.length})
              </span>
            )}
          </CardTitle>
          <div className="flex gap-2">
            <Button
              size="sm"
              variant="outline"
              onClick={loadOdks}
              disabled={loading}
            >
              {loading ? 'Refreshing...' : 'Refresh'}
            </Button>
            <CreateOdkDialog onCreated={loadOdks} />
          </div>
        </CardHeader>
        <CardContent>
          {error && <p className="text-sm text-destructive mb-3">{error}</p>}
          <OdkTable
            items={odks}
            selected={selected}
            onSelect={(item) =>
              setSelected(selected?.id === item.id ? null : item)
            }
          />
        </CardContent>
      </Card>

      {/* Selected ODK Detail */}
      {selected && (
        <OdkDetail
          odk={selected}
          onDeleted={handleDeleted}
        />
      )}
    </div>
  )
}
