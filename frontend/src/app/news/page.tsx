import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'

export default function NewsPage() {
  return (
    <LegalDocument
      eyebrow="NEWS"
      title="News"
      lede="Announcements, milestones, and updates on AZOA and its community pilots will appear here as they ship."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">UPDATES</div>
          <div className="az-arch-body">
            <p>
              No standalone announcements have been posted yet. In the meantime, you can read our essays on why AZOA exists and explore what the engine makes possible.
            </p>
            <p>
              <Link href="/blog">Blog →</Link>
              {' · '}
              <Link href="/workflows">What you can build →</Link>
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
