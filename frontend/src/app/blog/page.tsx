import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { AUTHOR, ESSAYS, essayPath, formatPublicDate, SIMULATION } from '@/content/public-documents'

export default function BlogPage() {
  return (
    <LegalDocument
      eyebrow="BLOG"
      title="Blog"
      lede={`Two notes by ${AUTHOR}. ${SIMULATION}`}
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">INDEX</div>
          <div className="az-arch-body">
            {ESSAYS.map((essay) => (
              <p key={essay.slug}>
                <span className="mono">{formatPublicDate(essay.publishedAt)} · {AUTHOR}</span>
                <br />
                <Link href={essayPath(essay.slug)}>{essay.title}</Link>
                <br />
                {essay.summary}
              </p>
            ))}
            <p>
              <Link href="/feed.xml">RSS</Link>
              {' · '}
              <Link href="/news">News</Link>
              {' · '}
              <Link href="/workflows">Workflows</Link>
            </p>
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
