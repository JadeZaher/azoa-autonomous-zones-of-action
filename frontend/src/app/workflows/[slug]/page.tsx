import Link from 'next/link'
import { notFound } from 'next/navigation'
import { LegalDocument } from '@/components/landing/legal'
import { AUTHOR, formatPublicDate, SIMULATION, workflowBySlug, WORKFLOWS } from '@/content/public-documents'

const SOURCES = [
  { label: 'AZOA homepage', href: 'https://azoa.ardanova.com' },
  { label: 'AZOA architecture', href: 'https://azoa.ardanova.com/architecture' },
  { label: 'AZOA terms', href: 'https://azoa.ardanova.com/terms' },
  { label: 'Settle-once note', href: 'https://azoa.ardanova.com/notes/settle-once-or-not-at-all' },
  { label: "Fail-closed note", href: 'https://azoa.ardanova.com/notes/fail-closed-or-it-isnt-a-control' },
]

export default async function WorkflowPage({ params }: { params: Promise<{ slug: string }> }) {
  const { slug } = await params
  const workflow = workflowBySlug(slug)
  if (!workflow) notFound()
  const siblings = WORKFLOWS.filter((item) => item.slug !== workflow.slug)

  return (
    <LegalDocument
      eyebrow={formatPublicDate(workflow.publishedAt).toUpperCase()}
      title={workflow.title}
      lede={`${workflow.summary} ${SIMULATION}`}
      active="workflows"
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">WORKFLOW</div>
          <div className="az-arch-body">
            <p className="mono">{formatPublicDate(workflow.publishedAt)} · {AUTHOR}</p>
            <p>
              <Link href="/">Home</Link>
              {' / '}
              <Link href="/workflows">Workflows</Link>
              {' / '}
              {workflow.title}
            </p>
            {workflow.paragraphs.map((paragraph) => (
              <p key={paragraph}>{paragraph}</p>
            ))}
            <h2 className="az-arch-h2">Sources</h2>
            <ul>
              {SOURCES.map((source) => (
                <li key={source.href}><a href={source.href}>{source.label}</a></li>
              ))}
            </ul>
            <h2 className="az-arch-h2">Also in this section</h2>
            <ul>
              {siblings.map((sibling) => (
                <li key={sibling.slug}><Link href={`/workflows/${sibling.slug}`}>{sibling.title}</Link></li>
              ))}
            </ul>
            <p>
              <a href="https://primusneo.com">PrimusNeo, the lab whose stack this engine belongs to</a>
              {' · '}
              <a href="https://ardanova.com">ArdaNova, a planned pilot whose economy is still simulated</a>
            </p>
            <p><Link href="/workflows">Back to workflows</Link></p>
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
