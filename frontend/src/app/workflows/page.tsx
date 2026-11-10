import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { AUTHOR, SIMULATION, WORKFLOWS } from '@/content/public-documents'

export default function WorkflowsPage() {
  return (
    <LegalDocument
      eyebrow="FIVE WORKFLOWS · ONE MODEL"
      title="Shapes the engine can describe."
      lede={`These pages name workflow shapes the engine models. Figures are not invented here. ${SIMULATION}`}
      active="workflows"
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">INDEX</div>
          <div className="az-arch-body">
            {WORKFLOWS.map((workflow) => (
              <p key={workflow.slug}>
                <Link href={`/workflows/${workflow.slug}`}>{workflow.title}</Link>
                <br />
                {workflow.summary}
              </p>
            ))}
            <p>
              The product pages that carry the same limit are the <Link href="/">homepage</Link> and the <Link href="/architecture">architecture</Link>.
              Author {AUTHOR}.{' '}
              <Link href="/blog">Blog</Link>
              {' · '}
              <Link href="/news">News</Link>
              {' · '}
              <Link href="/feed.xml">RSS</Link>
            </p>
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
