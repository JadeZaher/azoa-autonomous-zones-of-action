'use client'

import { useEffect, useState } from 'react'
import { operatorRequest } from '@/lib/operator-client'
import { Button } from '@/components/ui/button'

type Delivery = { to: string; subject: string; text: string }
type Entry = { requestId: string; status: string; expiresAt: string; updatedAt: string }

export default function MemberDeliveriesPage() {
  const [entries, setEntries] = useState<Entry[]>([])
  const [delivery, setDelivery] = useState<Delivery | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    void operatorRequest<Entry[]>('member-handoff-deliveries').then(setEntries)
      .catch(() => setError('Deliveries could not be read.'))
  }, [])
  async function reveal(id: string) {
    setDelivery(null); setError(null)
    try { setDelivery(await operatorRequest<Delivery>('member-handoff-deliveries/' + encodeURIComponent(id))) }
    catch { setError('This delivery expired, changed, or is unavailable.') }
  }
  return <main className="space-y-6 p-6">
    <h1 className="text-2xl font-bold">Member deliveries</h1>
    <p>Delivery stub. Nothing is emailed automatically. Deliver only to the recorded recipient.</p>
    {error && <p role="alert">{error}</p>}
    <ul className="space-y-3">{entries.map(entry => <li key={entry.requestId} className="flex flex-wrap items-center gap-3 border p-3">
      <span>{entry.requestId}</span><span>{entry.status}</span>
      <Button disabled={entry.status !== 'pending'} onClick={() => void reveal(entry.requestId)}>Read delivery</Button>
    </li>)}</ul>
    {delivery && <section className="space-y-3 border p-4">
      <h2 className="font-semibold">To: {delivery.to}</h2><p>{delivery.subject}</p>
      <textarea aria-label="Protected delivery message" className="min-h-48 w-full border p-3" readOnly value={delivery.text} />
      <Button variant="outline" onClick={() => setDelivery(null)}>Hide message</Button>
    </section>}
  </main>
}
