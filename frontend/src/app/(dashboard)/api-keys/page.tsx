'use client'

import { useState, useEffect, useCallback } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
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
} from '@/components/ui/dialog'
import { Copy, Check } from 'lucide-react'

// ─── Types ───

interface CreateApiKeyResponse {
  id: string
  key: string
  name: string
  prefix: string
  scopes: string[]
  expiresAt: string | null
  createdAt: string
}

interface ApiKeyInfo {
  id: string
  name: string
  prefix: string
  scopes: string[]
  createdAt: string
  lastUsedAt: string | null
  expiresAt: string | null
  isRevoked: boolean
}

/** One self-issuable scope with its description (from GET /api/apikey/scopes). */
interface SelfIssuableApiKeyScopeInfo {
  scope: string
  description: string
  isSelfIssuable: boolean
}

// ─── Helpers ───

function formatDate(dateStr: string | null): string {
  if (!dateStr) return '—'
  return new Date(dateStr).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}

function getKeyStatus(key: ApiKeyInfo): 'active' | 'revoked' | 'expired' {
  if (key.isRevoked) return 'revoked'
  if (key.expiresAt && new Date(key.expiresAt) < new Date()) return 'expired'
  return 'active'
}

function statusBadgeVariant(status: 'active' | 'revoked' | 'expired') {
  switch (status) {
    case 'active':
      return 'default' as const
    case 'revoked':
      return 'secondary' as const
    case 'expired':
      return 'outline' as const
  }
}

// ─── Copy Button ───

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false)

  const handleCopy = async () => {
    await navigator.clipboard.writeText(text)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  return (
    <Button variant="outline" size="sm" onClick={handleCopy} className="shrink-0">
      {copied ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
      <span className="ml-1.5">{copied ? 'Copied' : 'Copy'}</span>
    </Button>
  )
}

// ─── Create Key Form ───

function CreateKeyForm({ onCreated }: { onCreated: (key: CreateApiKeyResponse) => void }) {
  const [name, setName] = useState('')
  const [expiresInDays, setExpiresInDays] = useState('')
  const [selectedScopes, setSelectedScopes] = useState<Set<string>>(new Set())
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [scopeCatalog, setScopeCatalog] = useState<SelfIssuableApiKeyScopeInfo[]>([])
  const [scopesLoading, setScopesLoading] = useState(true)

  // Fetch the self-issuable scope vocabulary on mount to drive the checkboxes.
  useEffect(() => {
    let active = true
    ;(async () => {
      const res = await azoa.api.listSelfIssuableApiKeyScopes()
      if (!active) return
      if (isOk(res)) setScopeCatalog(res.value)
      setScopesLoading(false)
    })()
    return () => {
      active = false
    }
  }, [])

  const toggleScope = (scope: string, checked: boolean) => {
    setSelectedScopes((prev) => {
      const next = new Set(prev)
      if (checked) next.add(scope)
      else next.delete(scope)
      return next
    })
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (selectedScopes.size === 0) {
      setError('Choose at least one permission for this API key.')
      return
    }
    setLoading(true)
    setError(null)

    const scopeList = Array.from(selectedScopes)

    const body: Record<string, unknown> = { name }
    if (expiresInDays.trim()) body.expiresInDays = parseInt(expiresInDays, 10)
    body.scopes = scopeList.join(',')

    const res = await azoa.api.request<CreateApiKeyResponse>('POST', '/api/apikey', body)

    if (isOk(res)) {
      onCreated(res.value)
      setName('')
      setExpiresInDays('')
      setSelectedScopes(new Set())
    } else {
      setError(res.error.message)
    }
    setLoading(false)
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Create API Key</CardTitle>
        <CardDescription>Generate a new key for programmatic API access</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit} className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-1">
              <Label htmlFor="key-name">Key Name</Label>
              <Input
                id="key-name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="My API Key"
                required
              />
            </div>
            <div className="space-y-1">
              <Label htmlFor="key-expires">Expires In (days)</Label>
              <Input
                id="key-expires"
                type="number"
                min={1}
                value={expiresInDays}
                onChange={(e) => setExpiresInDays(e.target.value)}
                placeholder="Never"
              />
            </div>
          </div>

          <div className="space-y-2">
            <Label>Scopes</Label>
            <p className="text-xs text-muted-foreground">
              Choose only the permissions this key needs. DApp roles control which authoring and management scopes are available.
            </p>
            {scopesLoading ? (
              <p className="text-xs text-muted-foreground">Loading scopes…</p>
            ) : scopeCatalog.length === 0 ? (
              <p className="text-xs text-muted-foreground">No self-issuable scopes available.</p>
            ) : (
              <div className="grid gap-2 sm:grid-cols-2">
                {scopeCatalog.map((s) => (
                  <label key={s.scope} className="flex items-start gap-2 text-sm">
                    <Checkbox
                      className="mt-0.5"
                      checked={selectedScopes.has(s.scope)}
                      onCheckedChange={(v) => toggleScope(s.scope, !!v)}
                    />
                    <span>
                      <code className="font-mono text-xs">{s.scope}</code>
                      <span className="block text-xs text-muted-foreground">{s.description}</span>
                    </span>
                  </label>
                ))}
              </div>
            )}
          </div>

          <Button type="submit" disabled={loading || scopesLoading || !name.trim() || selectedScopes.size === 0}>
            {loading ? 'Creating...' : 'Create API Key'}
          </Button>

          {error && (
            <p className="text-sm text-destructive">{error}</p>
          )}
        </form>
      </CardContent>
    </Card>
  )
}

// ─── Newly Created Key Display ───

function NewKeyDisplay({ apiKey, onDismiss }: { apiKey: CreateApiKeyResponse; onDismiss: () => void }) {
  return (
    <Card className="border-primary/50 bg-primary/5">
      <CardHeader className="pb-2">
        <CardTitle className="text-sm">Key Created: {apiKey.name}</CardTitle>
        <CardDescription className="text-destructive font-medium">
          Copy this key now. You won't be able to see it again.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="flex items-center gap-2 rounded-md bg-muted p-3">
          <code className="flex-1 break-all font-mono text-sm">{apiKey.key}</code>
          <CopyButton text={apiKey.key} />
        </div>
        <Button variant="outline" size="sm" onClick={onDismiss}>
          Dismiss
        </Button>
      </CardContent>
    </Card>
  )
}

// ─── Confirm Action Dialog ───

function ConfirmDialog({
  title,
  description,
  confirmLabel,
  triggerVariant,
  triggerLabel,
  onConfirm,
}: {
  title: string
  description: string
  confirmLabel: string
  triggerVariant: 'outline' | 'destructive'
  triggerLabel: string
  onConfirm: () => Promise<void>
}) {
  const [open, setOpen] = useState(false)
  const [loading, setLoading] = useState(false)

  const handleConfirm = async () => {
    setLoading(true)
    await onConfirm()
    setLoading(false)
    setOpen(false)
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button variant={triggerVariant} size="sm" />}>
        {triggerLabel}
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
        </DialogHeader>
        <p className="text-sm text-muted-foreground">{description}</p>
        <div className="flex justify-end gap-2 pt-2">
          <Button variant="outline" onClick={() => setOpen(false)} disabled={loading}>
            Cancel
          </Button>
          <Button variant="destructive" onClick={handleConfirm} disabled={loading}>
            {loading ? 'Processing...' : confirmLabel}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}

// ─── Key List ───

function KeyList({
  keys,
  onRefresh,
  onRotated,
}: {
  keys: ApiKeyInfo[]
  loading: boolean
  onRefresh: () => void
  onRotated: (key: CreateApiKeyResponse) => void
}) {
  const [error, setError] = useState<string | null>(null)

  const handleRevoke = async (id: string) => {
    setError(null)
    const res = await azoa.api.request('POST', `/api/apikey/${id}/revoke`)
    if (isOk(res)) {
      onRefresh()
    } else {
      setError(res.error.message)
    }
  }

  const handleRotate = async (id: string) => {
    setError(null)
    const res = await azoa.api.rotateApiKey(id)
    if (isOk(res)) {
      onRotated(res.value as unknown as CreateApiKeyResponse)
      onRefresh()
    } else {
      setError(res.error.message)
    }
  }

  const handleDelete = async (id: string) => {
    setError(null)
    const res = await azoa.api.request('DELETE', `/api/apikey/${id}`)
    if (isOk(res)) {
      onRefresh()
    } else {
      setError(res.error.message)
    }
  }

  if (keys.length === 0) {
    return (
      <Card>
        <CardContent className="pt-6 text-center text-sm text-muted-foreground">
          No API keys yet. Create one above.
        </CardContent>
      </Card>
    )
  }

  return (
    <div className="space-y-2">
      {error && <p className="text-sm text-destructive">{error}</p>}
      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Prefix</TableHead>
                <TableHead>Scopes</TableHead>
                <TableHead>Created</TableHead>
                <TableHead>Last Used</TableHead>
                <TableHead>Expires</TableHead>
                <TableHead>Status</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {keys.map((key) => {
                const status = getKeyStatus(key)
                return (
                  <TableRow key={key.id}>
                    <TableCell className="font-medium">{key.name}</TableCell>
                    <TableCell>
                      <code className="rounded bg-muted px-1.5 py-0.5 text-xs font-mono">
                        {key.prefix}...
                      </code>
                    </TableCell>
                    <TableCell>
                      {key.scopes && key.scopes.length > 0 ? (
                        <div className="flex gap-1 flex-wrap">
                          {key.scopes.map((s) => (
                            <Badge key={s} variant="outline" className="text-xs">
                              {s}
                            </Badge>
                          ))}
                        </div>
                      ) : (
                        <span className="text-xs text-muted-foreground">full access</span>
                      )}
                    </TableCell>
                    <TableCell className="text-xs">{formatDate(key.createdAt)}</TableCell>
                    <TableCell className="text-xs">{formatDate(key.lastUsedAt)}</TableCell>
                    <TableCell className="text-xs">{formatDate(key.expiresAt)}</TableCell>
                    <TableCell>
                      <Badge variant={statusBadgeVariant(status)} className="capitalize text-xs">
                        {status}
                      </Badge>
                    </TableCell>
                    <TableCell className="text-right">
                      {status === 'active' && (
                        <div className="flex justify-end gap-2">
                          <ConfirmDialog
                            title="Rotate API Key"
                            description={`Rotate "${key.name}"? A new key inheriting its name, scopes, and expiry is minted and shown once; this key is immediately revoked.`}
                            confirmLabel="Rotate"
                            triggerVariant="outline"
                            triggerLabel="Rotate"
                            onConfirm={() => handleRotate(key.id)}
                          />
                          <ConfirmDialog
                            title="Revoke API Key"
                            description={`Are you sure you want to revoke "${key.name}"? This key will immediately stop working.`}
                            confirmLabel="Revoke"
                            triggerVariant="outline"
                            triggerLabel="Revoke"
                            onConfirm={() => handleRevoke(key.id)}
                          />
                        </div>
                      )}
                      {(status === 'revoked' || status === 'expired') && (
                        <ConfirmDialog
                          title="Delete API Key"
                          description={`Are you sure you want to permanently delete "${key.name}"? This action cannot be undone.`}
                          confirmLabel="Delete"
                          triggerVariant="destructive"
                          triggerLabel="Delete"
                          onConfirm={() => handleDelete(key.id)}
                        />
                      )}
                    </TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </div>
  )
}

// ─── Page ───

export default function ApiKeysPage() {
  const [keys, setKeys] = useState<ApiKeyInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [newKey, setNewKey] = useState<CreateApiKeyResponse | null>(null)

  const fetchKeys = useCallback(async () => {
    setLoading(true)
    setError(null)
    const res = await azoa.api.request<ApiKeyInfo[]>('GET', '/api/apikey')
    if (isOk(res)) {
      setKeys(res.value)
    } else {
      setError(res.error.message)
    }
    setLoading(false)
  }, [])

  useEffect(() => {
    fetchKeys()
  }, [fetchKeys])

  const handleKeyCreated = (key: CreateApiKeyResponse) => {
    setNewKey(key)
    fetchKeys()
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">API Keys</h1>
        <p className="text-sm text-muted-foreground">
          Create and manage API keys for programmatic access to the AZOA API
        </p>
      </div>

      <CreateKeyForm onCreated={handleKeyCreated} />

      {newKey && (
        <NewKeyDisplay apiKey={newKey} onDismiss={() => setNewKey(null)} />
      )}

      <div>
        <h2 className="mb-3 text-sm font-medium text-muted-foreground uppercase tracking-wider">
          Your API Keys
        </h2>
        {loading ? (
          <div className="space-y-2">
            {Array.from({ length: 2 }).map((_, i) => (
              <Card key={i} className="animate-pulse">
                <CardContent className="h-16 pt-6" />
              </Card>
            ))}
          </div>
        ) : error ? (
          <Card className="border-destructive/40">
            <CardContent className="pt-6 text-sm text-destructive">{error}</CardContent>
          </Card>
        ) : (
          <KeyList keys={keys} loading={loading} onRefresh={fetchKeys} onRotated={setNewKey} />
        )}
      </div>
    </div>
  )
}
