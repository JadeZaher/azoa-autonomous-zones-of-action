import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'
import { ESSAYS, essayPath, formatPublicDate } from '@/content/public-documents'

export default function NotesPage() {
  return (
    <LegalDocument
      eyebrow="ENGINEERING NOTES"
      title="Notes on settlement and controls."
      lede="Two core architectural notes on turning ambiguous timeouts into settled records and ensuring every verification check fails closed."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">POSTS</div>
          <div className="az-arch-body">
            {ESSAYS.map((essay) => (
              <div key={essay.slug} style={{ marginBottom: 40 }}>
                <div className="az-kicker" style={{ marginBottom: 8 }}>{formatPublicDate(essay.publishedAt)}</div>
                <h2 className="az-arch-h2" style={{ marginBottom: 12 }}>
                  <Link href={essayPath(essay.slug)}>{essay.title}</Link>
                </h2>
                <p>{essay.summary}</p>
                <p><Link href={essayPath(essay.slug)}>Read the note →</Link></p>
              </div>
            ))}
            <p>
              <Link href="/workflows">Explore all workflow patterns →</Link>
              {' · '}
              <Link href="/feed.xml">RSS feed</Link>
              {' · '}
              <Link href="/blog">Blog index</Link>
            </p>
            <WorkflowFunnelGate context="blog" />
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
