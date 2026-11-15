'use client'

import { useEffect, useState } from 'react'
import { operatorRequest } from '@/lib/operator-client'
import { Button } from '@/components/ui/button'

type Delivery = { to: string; subject: string; text: string; accessLinkUrl?: string | null }
type Entry = { requestId: string; status: string; expiresAt: string; updatedAt: string }

function extractSafeAccessLink(text: string, explicit?: string | null): string | null {
  const candidates = [explicit ?? '', ...(text.match(/https?:\/\/[^\s<>"']+/gi) ?? [])]
  for (const raw of candidates) {
    const cleaned = raw.trim().replace(/[).,;]+$/, '')
    if (!cleaned) continue
    try {
      const parsed = new URL(cleaned)
      if (!parsed.username && !parsed.password && (parsed.protocol === 'https:' || (parsed.protocol === 'http:' && (parsed.hostname === 'localhost' || parsed.hostname === '127.0.0.1' || parsed.hostname === '[::1]')))) {
        return parsed.href
      }
    } catch {
      // Skip malformed URL candidates
    }
  }
  return null
}

export default function MemberDeliveriesPage() {
  const [entries, setEntries] = useState<Entry[]>([])
  const [delivery, setDelivery] = useState<Delivery | null>(null)
  const [copiedKind, setCopiedKind] = useState<'link' | 'text' | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    void operatorRequest<Entry[]>('member-handoff-deliveries').then(setEntries)
      .catch(() => setError('Deliveries could not be read.'))
  }, [])
  async function reveal(id: string) {
    setDelivery(null); setError(null); setCopiedKind(null)
    try { setDelivery(await operatorRequest<Delivery>('member-handoff-deliveries/' + encodeURIComponent(id))) }
    catch { setError('This delivery expired, changed, or is unavailable.') }
  }
  async function copyValue(kind: 'link' | 'text', value: string) {
    if (typeof navigator === 'undefined' || !navigator.clipboard?.writeText) return
    try {
      await navigator.clipboard.writeText(value)
      setCopiedKind(kind)
    } catch {
      setCopiedKind(null)
    }
  }
  const accessLink = delivery ? extractSafeAccessLink(delivery.text, delivery.accessLinkUrl) : null
  return <main className="space-y-6 p-6">
    <div>
      <h1 className="text-2xl font-bold">Member deliveries</h1>
      <p className="text-sm text-muted-foreground">Operator dispatch queue. Inspect or copy the verified access link to deliver directly to the recorded recipient.</p>
    </div>
    {error && <p role="alert">{error}</p>}
    <ul className="space-y-3">{entries.map(entry => <li key={entry.requestId} className="flex flex-wrap items-center justify-between gap-3 rounded-md border p-3">
      <div className="space-y-0.5 text-sm">
        <span className="font-mono text-xs">{entry.requestId}</span>
        <span className="ml-3 rounded bg-muted px-2 py-0.5 text-xs">{entry.status}</span>
      </div>
      <Button disabled={entry.status !== 'pending'} onClick={() => void reveal(entry.requestId)}>Inspect delivery</Button>
    </li>)}</ul>
    {delivery && <section className="space-y-4 rounded-md border p-4" aria-label="Delivery inspection">
      <div className="space-y-1">
        <h2 className="font-semibold">Recipient: {delivery.to}</h2>
        <p className="text-sm text-muted-foreground">{delivery.subject}</p>
      </div>
      {accessLink && (
        <div className="flex flex-wrap items-center gap-2 rounded-md border bg-muted/30 p-3">
          <a href={accessLink} target="_blank" rel="noopener noreferrer" className="min-w-0 flex-1 truncate font-mono text-xs underline underline-offset-4">
            {accessLink}
          </a>
          <Button type="button" size="sm" onClick={() => void copyValue('link', accessLink)}>
            {copiedKind === 'link' ? 'Access link copied' : 'Copy access link'}
          </Button>
        </div>
      )}
      <pre aria-label="Protected delivery message" className="max-h-64 overflow-auto whitespace-pre-wrap rounded-md border bg-muted/40 p-3 font-mono text-xs">{delivery.text}</pre>
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="secondary" onClick={() => void copyValue('text', delivery.text)}>
          {copiedKind === 'text' ? 'Message copied' : 'Copy message'}
        </Button>
        <Button type="button" variant="outline" onClick={() => { setDelivery(null); setCopiedKind(null) }}>Hide delivery</Button>
      </div>
    </section>}
  </main>
}
