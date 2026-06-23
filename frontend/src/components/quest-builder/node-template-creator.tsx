'use client'

import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { ErrorBanner } from '@/components/shared/error-banner'
import { ResultDisplay } from '@/components/shared/result-display'
import { azoa, isOk } from '@/lib/azoa'
import { NODE_CATALOG, NODE_CATALOG_BY_TYPE, CATEGORY_ORDER } from './node-catalog'

/**
 * Form to author a reusable Quest Node Template (POST /api/quest/node-templates).
 * Node templates seed the builder palette with a named, schema-carrying node.
 */
export function NodeTemplateCreator({ onCreated }: { onCreated?: () => void }) {
  const [name, setName] = useState('')
  const [nodeType, setNodeType] = useState('HolonCreate')
  const [description, setDescription] = useState('')
  const [defaultConfig, setDefaultConfig] = useState(NODE_CATALOG_BY_TYPE['HolonCreate'].defaultConfig)
  const [configSchema, setConfigSchema] = useState('{}')
  const [inputSchema, setInputSchema] = useState('{}')
  const [outputSchema, setOutputSchema] = useState('{}')
  const [version, setVersion] = useState('1.0.0')
  const [isPublic, setIsPublic] = useState(false)
  const [tags, setTags] = useState('')

  const [result, setResult] = useState<unknown>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const onTypeChange = (t: string) => {
    setNodeType(t)
    // Seed the default config from the catalog when the type changes, but only
    // if the user hasn't diverged from a previous catalog default.
    const prevDefault = NODE_CATALOG.find((c) => c.defaultConfig === defaultConfig)
    if (prevDefault || defaultConfig.trim() === '' || defaultConfig.trim() === '{}') {
      setDefaultConfig(NODE_CATALOG_BY_TYPE[t]?.defaultConfig ?? '{}')
    }
  }

  const validateJson = (label: string, raw: string): string | null => {
    try {
      JSON.parse(raw || '{}')
      return null
    } catch {
      return `${label} is not valid JSON`
    }
  }

  const handleCreate = async () => {
    setError(null)
    setResult(null)
    if (!name.trim()) {
      setError('Name is required')
      return
    }
    for (const [label, raw] of [
      ['Default config', defaultConfig],
      ['Config schema', configSchema],
      ['Input schema', inputSchema],
      ['Output schema', outputSchema],
    ] as const) {
      const err = validateJson(label, raw)
      if (err) {
        setError(err)
        return
      }
    }

    setLoading(true)
    try {
      const body = {
        name: name.trim(),
        nodeType,
        description: description.trim() || undefined,
        defaultConfig,
        configSchema,
        inputSchema,
        outputSchema,
        version: version.trim() || '1.0.0',
        isPublic,
        tags: tags.split(',').map((t) => t.trim()).filter(Boolean),
      }
      const res = await azoa.api.request('POST', '/api/quest/node-templates', body)
      if (isOk(res)) {
        setResult(res.value)
        onCreated?.()
      } else {
        setError(res.error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Request failed')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="space-y-3">
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1.5">
          <Label>Name</Label>
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Mint Reward NFT" />
        </div>
        <div className="space-y-1.5">
          <Label>Node Type</Label>
          <Select value={nodeType} onValueChange={onTypeChange}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {CATEGORY_ORDER.flatMap((cat) =>
                NODE_CATALOG.filter((c) => c.category === cat).map((c) => (
                  <SelectItem key={c.type} value={c.type}>
                    {c.type} · {cat}
                  </SelectItem>
                )),
              )}
            </SelectContent>
          </Select>
        </div>
      </div>

      <div className="space-y-1.5">
        <Label>Description</Label>
        <Input value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Optional" />
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1.5">
          <Label>Default Config (JSON)</Label>
          <Textarea
            className="min-h-[100px] font-mono text-xs"
            value={defaultConfig}
            onChange={(e) => setDefaultConfig(e.target.value)}
          />
        </div>
        <div className="space-y-1.5">
          <Label>Config Schema (JSON)</Label>
          <Textarea
            className="min-h-[100px] font-mono text-xs"
            value={configSchema}
            onChange={(e) => setConfigSchema(e.target.value)}
          />
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1.5">
          <Label>Input Schema (JSON)</Label>
          <Textarea
            className="min-h-[80px] font-mono text-xs"
            value={inputSchema}
            onChange={(e) => setInputSchema(e.target.value)}
          />
        </div>
        <div className="space-y-1.5">
          <Label>Output Schema (JSON)</Label>
          <Textarea
            className="min-h-[80px] font-mono text-xs"
            value={outputSchema}
            onChange={(e) => setOutputSchema(e.target.value)}
          />
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-3">
        <div className="space-y-1.5">
          <Label>Version</Label>
          <Input value={version} onChange={(e) => setVersion(e.target.value)} placeholder="1.0.0" />
        </div>
        <div className="space-y-1.5 sm:col-span-2">
          <Label>Tags (comma-separated)</Label>
          <Input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="reward, nft" />
        </div>
      </div>

      <label className="flex items-center gap-2 text-sm">
        <Checkbox checked={isPublic} onCheckedChange={(v) => setIsPublic(!!v)} />
        Public (visible to other avatars)
      </label>

      <Button onClick={handleCreate} disabled={loading || !name.trim()}>
        {loading ? 'Creating…' : 'Create Node Template'}
      </Button>

      {error ? <ErrorBanner message={error} /> : null}
      {result !== null && result !== undefined && <ResultDisplay result={result} />}
    </div>
  )
}
