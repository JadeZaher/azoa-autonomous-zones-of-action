'use client'

import { useState, useCallback } from 'react'
import { toast } from 'sonner'
import { oasis, isOk } from '@/lib/oasis'
import type { HolonResult } from '@/lib/oasis'
import { useOasisAuth } from '@/lib/oasis-auth'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { Skeleton } from '@/components/ui/skeleton'
import { Separator } from '@/components/ui/separator'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogTrigger,
} from '@/components/ui/dialog'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table'
import { ChainBadge } from '@/components/shared/chain-badge'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ErrorBanner } from '@/components/shared/error-banner'

interface Filters { name: string; chainId: string; assetType: string; isActive: boolean | undefined }
interface HolonFormState { name: string; description: string; providerName: string; chainId: string; assetType: string; metadataKeys: string[]; metadataValues: string[] }

const DEFAULT_FILTERS: Filters = { name: '', chainId: 'any', assetType: '', isActive: undefined }
const DEFAULT_FORM: HolonFormState = { name: '', description: '', providerName: 'InMemory', chainId: 'algorand', assetType: '', metadataKeys: [''], metadataValues: [''] }

function buildMetadata(keys: string[], values: string[]): Record<string, string> {
  const r: Record<string, string> = {}
  keys.forEach((k, i) => { if (k.trim()) r[k.trim()] = values[i] ?? '' })
  return r
}

function MetadataEditor({ keys, values, onChange }: { keys: string[]; values: string[]; onChange: (k: string[], v: string[]) => void }) {
  return (
    <div className="space-y-2">
      <Label className="text-xs">Metadata</Label>
      {keys.map((k, i) => (
        <div key={i} className="flex gap-2">
          <Input placeholder="Key" value={k} onChange={e => { const ks = [...keys]; ks[i] = e.target.value; onChange(ks, values) }} className="flex-1" />
          <Input placeholder="Value" value={values[i] ?? ''} onChange={e => { const vs = [...values]; vs[i] = e.target.value; onChange(keys, vs) }} className="flex-1" />
          <Button type="button" variant="ghost" size="sm" onClick={() => { const ks = keys.filter((_, idx) => idx !== i); const vs = values.filter((_, idx) => idx !== i); onChange(ks.length ? ks : [''], vs.length ? vs : ['']) }} disabled={keys.length === 1}>×</Button>
        </div>
      ))}
      <Button type="button" variant="outline" size="sm" onClick={() => onChange([...keys, ''], [...values, ''])}>+ Add</Button>
    </div>
  )
}

function HolonForm({ form, onChange }: { form: HolonFormState; onChange: (f: HolonFormState) => void }) {
  const set = (field: keyof HolonFormState) => (val: string) => onChange({ ...form, [field]: val })
  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-3">
        <div className="space-y-1.5"><Label>Name *</Label><Input value={form.name} onChange={e => set('name')(e.target.value)} /></div>
        <div className="space-y-1.5"><Label>Provider</Label><Input value={form.providerName} onChange={e => set('providerName')(e.target.value)} /></div>
      </div>
      <div className="space-y-1.5"><Label>Description</Label><Input value={form.description} onChange={e => set('description')(e.target.value)} /></div>
      <div className="grid grid-cols-2 gap-3">
        <div className="space-y-1.5"><Label>Chain</Label>
          <Select value={form.chainId} onValueChange={v => { if (v) set('chainId')(v) }}><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="algorand">Algorand</SelectItem><SelectItem value="solana">Solana</SelectItem></SelectContent></Select>
        </div>
        <div className="space-y-1.5"><Label>Asset Type</Label><Input value={form.assetType} onChange={e => set('assetType')(e.target.value)} /></div>
      </div>
      <MetadataEditor keys={form.metadataKeys} values={form.metadataValues} onChange={(ks, vs) => onChange({ ...form, metadataKeys: ks, metadataValues: vs })} />
    </div>
  )
}

function CreateHolonDialog({ onCreated }: { onCreated: () => void }) {
  const [open, setOpen] = useState(false)
  const [form, setForm] = useState<HolonFormState>(DEFAULT_FORM)
  const [loading, setLoading] = useState(false)
  const handleSubmit = async () => {
    if (!form.name.trim()) { toast.error('Name required'); return }
    setLoading(true)
    const result = await oasis.holons.create({ name: form.name, description: form.description, providerName: form.providerName, chainId: form.chainId, assetType: form.assetType, metadata: buildMetadata(form.metadataKeys, form.metadataValues) })
    setLoading(false)
    if (isOk(result)) { toast.success('Created'); setOpen(false); setForm(DEFAULT_FORM); onCreated() }
    else toast.error(result.error.message)
  }
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button size="sm">Create holon</Button>} />
      <DialogContent className="max-w-lg"><DialogHeader><DialogTitle>Create Holon</DialogTitle></DialogHeader><HolonForm form={form} onChange={setForm} />
        <DialogFooter><Button variant="outline" onClick={() => setOpen(false)}>Cancel</Button><Button onClick={handleSubmit} disabled={loading}>{loading ? 'Creating…' : 'Create'}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function EditHolonDialog({ holon, onUpdated }: { holon: HolonResult; onUpdated: () => void }) {
  const [open, setOpen] = useState(false)
  const mk = holon.metadata ? Object.keys(holon.metadata) : ['']
  const mv = holon.metadata ? Object.values(holon.metadata) : ['']
  const [form, setForm] = useState<HolonFormState>({ name: holon.name ?? '', description: holon.description ?? '', providerName: holon.providerName ?? '', chainId: holon.chainId ?? 'algorand', assetType: holon.assetType ?? '', metadataKeys: mk.length ? mk : [''], metadataValues: mv.length ? mv : [''] })
  const [loading, setLoading] = useState(false)
  const handleSubmit = async () => {
    if (!form.name.trim()) { toast.error('Name required'); return }
    setLoading(true)
    const result = await oasis.holons.update(holon.id, { name: form.name, description: form.description, providerName: form.providerName, chainId: form.chainId, assetType: form.assetType, metadata: buildMetadata(form.metadataKeys, form.metadataValues) })
    setLoading(false)
    if (isOk(result)) { toast.success('Updated'); setOpen(false); onUpdated() } else toast.error(result.error.message)
  }
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button variant="outline" size="sm">Edit</Button>} />
      <DialogContent className="max-w-lg"><DialogHeader><DialogTitle>Edit Holon</DialogTitle></DialogHeader><HolonForm form={form} onChange={setForm} />
        <DialogFooter><Button variant="outline" onClick={() => setOpen(false)}>Cancel</Button><Button onClick={handleSubmit} disabled={loading}>{loading ? 'Saving…' : 'Save'}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function DeleteHolonDialog({ holon, onDeleted }: { holon: HolonResult; onDeleted: () => void }) {
  const [open, setOpen] = useState(false)
  const [loading, setLoading] = useState(false)
  const handleDelete = async () => {
    setLoading(true)
    const result = await oasis.holons.delete(holon.id)
    setLoading(false)
    if (isOk(result)) { toast.success('Deleted'); setOpen(false); onDeleted() } else toast.error(result.error.message)
  }
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button variant="destructive" size="sm">Delete</Button>} />
      <DialogContent>
        <DialogHeader><DialogTitle>Delete Holon</DialogTitle></DialogHeader>
        <p className="text-sm text-muted-foreground">Delete <strong>{holon.name}</strong>?</p>
        <DialogFooter><Button variant="outline" onClick={() => setOpen(false)}>Cancel</Button><Button variant="destructive" onClick={handleDelete} disabled={loading}>{loading ? 'Deleting…' : 'Delete'}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function DetailPanel({ holon, onUpdated, onDeleted }: { holon: HolonResult; onUpdated: () => void; onDeleted: () => void }) {
  const [children, setChildren] = useState<HolonResult[] | null>(null)
  const [peers, setPeers] = useState<HolonResult[] | null>(null)
  const [loadingKey, setLoadingKey] = useState<string | null>(null)

  const load = async (type: 'children' | 'peers') => {
    setLoadingKey(type)
    const result = type === 'children' ? await oasis.holons.getChildren(holon.id) : await oasis.holons.getPeers(holon.id)
    if (isOk(result)) { type === 'children' ? setChildren(result.value) : setPeers(result.value) } else toast.error(result.error.message)
    setLoadingKey(null)
  }

  const renderList = (label: string, items: HolonResult[] | null) => {
    if (!items) return null
    return (
      <div className="space-y-1 mt-2">
        <p className="text-[11px] font-semibold uppercase text-muted-foreground">{label}</p>
        {items.length === 0 ? <p className="text-xs text-muted-foreground">None</p> : items.map(h => <div key={h.id} className="flex items-center gap-2 text-xs"><ChainBadge chain={h.chainId ?? 'unknown'} /><span>{h.name}</span></div>)}
      </div>
    )
  }

  return (
    <Card className="h-fit">
      <CardHeader className="pb-3">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0"><CardTitle className="text-sm truncate">{holon.name}</CardTitle><p className="mt-0.5 truncate text-[11px] text-muted-foreground font-mono">{holon.id}</p></div>
          <ChainBadge chain={holon.chainId ?? 'unknown'} />
        </div>
      </CardHeader>
      <CardContent>
        <Tabs defaultValue="details">
          <TabsList className="mb-3"><TabsTrigger value="details">Details</TabsTrigger><TabsTrigger value="tree">Tree</TabsTrigger><TabsTrigger value="actions">Actions</TabsTrigger></TabsList>
          <TabsContent value="details" className="space-y-3">
            <div className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-xs">
              <span className="text-muted-foreground">Asset Type</span><span>{holon.assetType || '—'}</span>
              <span className="text-muted-foreground">Status</span><span><Badge variant={holon.isActive ? 'default' : 'secondary'} className="text-[10px]">{holon.isActive ? 'Active' : 'Inactive'}</Badge></span>
              <span className="text-muted-foreground">Created</span><span>{holon.createdDate ? new Date(holon.createdDate).toLocaleDateString() : '—'}</span>
            </div>
            <Separator />
            <div><p className="mb-1.5 text-[11px] font-semibold uppercase text-muted-foreground">Raw</p><div className="rounded-md bg-muted/50 p-3 text-xs"><JsonViewer data={holon} /></div></div>
          </TabsContent>
          <TabsContent value="tree" className="space-y-2">
            <div className="flex gap-2">
              <Button variant="outline" size="sm" onClick={() => load('children')} disabled={loadingKey === 'children'}>{loadingKey === 'children' ? '…' : 'Children'}</Button>
              <Button variant="outline" size="sm" onClick={() => load('peers')} disabled={loadingKey === 'peers'}>{loadingKey === 'peers' ? '…' : 'Peers'}</Button>
            </div>
            {renderList('Children', children)}{renderList('Peers', peers)}
          </TabsContent>
          <TabsContent value="actions" className="flex flex-wrap gap-2">
            <EditHolonDialog holon={holon} onUpdated={onUpdated} />
            <DeleteHolonDialog holon={holon} onDeleted={onDeleted} />
            <Button variant="outline" size="sm" onClick={async () => {
              const r = await oasis.holons.create({ name: `${holon.name} (clone)`, chainId: holon.chainId, assetType: holon.assetType, metadata: holon.metadata as Record<string, string> })
              if (isOk(r)) { toast.success('Cloned'); onUpdated() } else toast.error(r.error.message)
            }}>Clone</Button>
          </TabsContent>
        </Tabs>
      </CardContent>
    </Card>
  )
}

export default function HolonsPage() {
  useOasisAuth()
  const [filters, setFilters] = useState<Filters>(DEFAULT_FILTERS)
  const [holons, setHolons] = useState<HolonResult[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [searched, setSearched] = useState(false)
  const [selected, setSelected] = useState<HolonResult | null>(null)

  const search = useCallback(async () => {
    setLoading(true); setError(null); setSelected(null)
    const q: Record<string, unknown> = {}
    if (filters.name) q.name = filters.name
    if (filters.chainId !== 'any') q.chainId = filters.chainId
    if (filters.assetType) q.assetType = filters.assetType
    if (filters.isActive !== undefined) q.isActive = filters.isActive
    const result = await oasis.holons.where(q).execute()
    setLoading(false); setSearched(true)
    if (isOk(result)) setHolons(result.value); else setError(result.error.message)
  }, [filters])

  const clearFilters = () => { setFilters(DEFAULT_FILTERS); setHolons([]); setSearched(false); setSelected(null); setError(null) }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div><h1 className="text-lg font-semibold tracking-tight">Holons</h1><p className="text-sm text-muted-foreground">Query and manage holons</p></div>
        <CreateHolonDialog onCreated={search} />
      </div>

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Filters</CardTitle></CardHeader>
        <CardContent>
          <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
            <div className="space-y-1.5"><Label className="text-xs">Name</Label><Input placeholder="Search…" value={filters.name} onChange={e => setFilters(f => ({ ...f, name: e.target.value }))} onKeyDown={e => e.key === 'Enter' && search()} /></div>
            <div className="space-y-1.5"><Label className="text-xs">Chain</Label>
              <Select value={filters.chainId} onValueChange={v => setFilters(f => ({ ...f, chainId: v ?? 'any' }))}><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="any">Any</SelectItem><SelectItem value="algorand">Algorand</SelectItem><SelectItem value="solana">Solana</SelectItem></SelectContent></Select>
            </div>
            <div className="space-y-1.5"><Label className="text-xs">Asset Type</Label><Input placeholder="e.g. NFT" value={filters.assetType} onChange={e => setFilters(f => ({ ...f, assetType: e.target.value }))} onKeyDown={e => e.key === 'Enter' && search()} /></div>
            <div className="flex items-end gap-2 pb-0.5"><Checkbox id="isActive" checked={filters.isActive === true} onCheckedChange={c => setFilters(f => ({ ...f, isActive: c === true ? true : undefined }))} /><Label htmlFor="isActive" className="text-xs">Active only</Label></div>
          </div>
          <div className="mt-3 flex gap-2">
            <Button onClick={search} disabled={loading} size="sm">{loading ? 'Searching…' : 'Search'}</Button>
            <Button variant="outline" onClick={clearFilters} size="sm">Clear</Button>
          </div>
        </CardContent>
      </Card>

      {error && <ErrorBanner message={error} onRetry={search} />}

      <div className="flex gap-6">
        <div className="min-w-0 flex-1">
          <Card>
            <CardHeader className="pb-3"><CardTitle className="text-sm">Results {searched && !loading && `(${holons.length})`}</CardTitle></CardHeader>
            <CardContent className="p-0">
              {loading ? <div className="space-y-2 p-4">{[...Array(4)].map((_, i) => <Skeleton key={i} className="h-10 w-full" />)}</div>
              : !searched ? <p className="p-6 text-center text-sm text-muted-foreground">Use filters to search.</p>
              : holons.length === 0 ? <p className="p-6 text-center text-sm text-muted-foreground">No holons found.</p>
              : (
                <Table>
                  <TableHeader><TableRow><TableHead>Name</TableHead><TableHead>Chain</TableHead><TableHead>Type</TableHead><TableHead>Status</TableHead></TableRow></TableHeader>
                  <TableBody>{holons.map(h => (
                    <TableRow key={h.id} className={`cursor-pointer ${selected?.id === h.id ? 'bg-accent' : ''}`} onClick={() => setSelected(h)}>
                      <TableCell className="font-medium text-sm">{h.name}</TableCell>
                      <TableCell><ChainBadge chain={h.chainId ?? 'unknown'} /></TableCell>
                      <TableCell className="text-xs">{h.assetType || '—'}</TableCell>
                      <TableCell><Badge variant={h.isActive ? 'default' : 'secondary'} className="text-[10px]">{h.isActive ? 'Active' : 'Inactive'}</Badge></TableCell>
                    </TableRow>
                  ))}</TableBody>
                </Table>
              )}
            </CardContent>
          </Card>
        </div>
        {selected && <div className="w-80 shrink-0"><DetailPanel holon={selected} onUpdated={search} onDeleted={search} /></div>}
      </div>
    </div>
  )
}
