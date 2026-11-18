import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'
import { AUTHOR, ESSAYS, essayPath, formatPublicDate, WORKFLOWS } from '@/content/public-documents'

const FEATURED_WORKFLOWS = WORKFLOWS.filter((w) =>
  [
    'reconciliation-over-blind-retry',
    'version-pinned-execution',
    'durable-waits-and-signals',
    'settle-once',
    'fail-closed',
    'consent-records',
  ].includes(w.slug),
)

export default function BlogPage() {
  return (
    <LegalDocument
      eyebrow="BLOG"
      title="Why AZOA exists, and what you can build with it."
      lede={`Essays and plain-language guides from ${AUTHOR} on self-owned identity, durable agreements, and workflows an organization can actually trust.`}
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

            <h2 className="az-arch-h2" style={{ marginTop: 48 }}>
              Core Ideas &amp; Guides
            </h2>
            <ul>
              {FEATURED_WORKFLOWS.map((workflow) => (
                <li key={workflow.slug}>
                  <Link href={`/workflows/${workflow.slug}`}>
                    <strong>{workflow.title}</strong>
                  </Link>
                  {' — '}
                  {workflow.description}
                </li>
              ))}
            </ul>
            <p>
              <Link href="/workflows">Explore all guides →</Link>
              {' · '}
              <Link href="/feed.xml">RSS feed</Link>
              {' · '}
              <Link href="/notes">All notes</Link>
              {' · '}
              <Link href="/news">News</Link>
            </p>
            <WorkflowFunnelGate context="blog" />
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
