import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'
import { ESSAYS, essayPath, formatPublicDate } from '@/content/public-documents'

export default function NotesPage() {
  return (
    <LegalDocument
      eyebrow="NOTES"
      title="Why AZOA exists."
      lede="Two essays on what it takes to trust a digital process with real value: why a retried step should settle once or not at all, and why anything unverified should stop rather than guess."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">ESSAYS</div>
          <div className="az-arch-body">
            {ESSAYS.map((essay) => (
              <div key={essay.slug} style={{ marginBottom: 40 }}>
                <div className="az-kicker" style={{ marginBottom: 8 }}>
                  {formatPublicDate(essay.publishedAt)}
                </div>
                <h2 className="az-arch-h2" style={{ marginBottom: 12 }}>
                  <Link href={essayPath(essay.slug)}>{essay.title}</Link>
                </h2>
                <p>{essay.summary}</p>
                <p>
                  <Link href={essayPath(essay.slug)}>Read the essay →</Link>
                </p>
              </div>
            ))}
            <p>
              <Link href="/workflows">Explore what you can build →</Link>
              {' · '}
              <Link href="/feed.xml">RSS feed</Link>
              {' · '}
              <Link href="/blog">Blog</Link>
            </p>
            <WorkflowFunnelGate context="blog" />
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
