import type { Metadata } from 'next'
import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'

export const metadata: Metadata = {
  title: 'Notes — AZOA',
  description:
    'Two notes on settlement and controls for this hosted simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet.',
}

const NOTES = [
  {
    href: '/notes/settle-once-or-not-at-all',
    title: 'A retried step should settle once, or not at all',
    date: '9 November 2026',
    summary:
      'A timeout is not a decision. AZOA is being built so a retried step can be found later as a yes or a no. This public site is a simulation, and it does not move value.',
  },
  {
    href: '/notes/fail-closed-or-it-isnt-a-control',
    title: "Fail closed, or it isn't a control",
    date: '9 November 2026',
    summary:
      'A control that fails open is a decoration. AZOA is built so that anything it cannot verify is refused. This public site is a simulation, and that sentence is part of the control.',
  },
]

export default function NotesPage() {
  return (
    <LegalDocument
      eyebrow="NOTES"
      title="Notes"
      lede="Two notes on how this hosted site talks about settlement and controls. This public site is a simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">INDEX</div>
          <div className="az-arch-body">
            {NOTES.map((note) => (
              <p key={note.href}>
                <span className="mono">{note.date}</span>
                <br />
                <Link href={note.href}>{note.title}</Link>
                <br />
                {note.summary}
              </p>
            ))}
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
