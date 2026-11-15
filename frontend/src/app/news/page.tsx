import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'

export default function NewsPage() {
  return (
    <LegalDocument
      eyebrow="RELEASES &amp; DISPATCHES"
      title="News"
      lede="Public release announcements and milestones will appear here as dated engineering facts ship."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">UPDATES</div>
          <div className="az-arch-body">
            <p>No standalone release bulletins have been posted yet. Ongoing architectural field notes and pattern documentation are published in the engineering blog and workflows catalog.</p>
            <p>
              <Link href="/blog">Engineering Blog →</Link>
              {' · '}
              <Link href="/workflows">Workflow Patterns →</Link>
              {' · '}
              <Link href="/architecture">Architecture →</Link>
            </p>
            <WorkflowFunnelGate context="blog" />
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
