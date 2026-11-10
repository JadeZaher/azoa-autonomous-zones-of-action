import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { ESSAYS, essayPath, formatPublicDate } from '@/content/public-documents'

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
            {ESSAYS.map((essay) => (
              <p key={essay.slug}>
                <span className="mono">{formatPublicDate(essay.publishedAt)}</span>
                <br />
                <Link href={essayPath(essay.slug)}>{essay.title}</Link>
                <br />
                {essay.summary}
              </p>
            ))}
            <p><Link href="/blog">Blog</Link></p>
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
