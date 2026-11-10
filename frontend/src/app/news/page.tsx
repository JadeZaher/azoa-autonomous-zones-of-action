import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { SIMULATION } from '@/content/public-documents'

export default function NewsPage() {
  return (
    <LegalDocument
      eyebrow="NEWS"
      title="News"
      lede="Nothing has been announced yet. This page stays empty until a public, dated fact exists."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">EMPTY</div>
          <div className="az-arch-body">
            <p>There are no news items. {SIMULATION}</p>
            <p>
              <Link href="/blog">Blog</Link>
              {' · '}
              <Link href="/workflows">Workflows</Link>
            </p>
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
