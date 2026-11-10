import Link from 'next/link'
import { notFound } from 'next/navigation'
import { LegalDocument } from '@/components/landing/legal'
import { Prose } from '@/components/landing/prose'
import { workflowBySlug } from '@/content/workflows'

export default async function WorkflowPage({ params }: { params: Promise<{ slug: string }> }) {
  const { slug } = await params
  const workflow = workflowBySlug(slug)
  if (!workflow) notFound()

  return (
    <LegalDocument
      eyebrow={workflow.eyebrow}
      title={workflow.h1}
      lede={workflow.lede}
      active="workflows"
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">WORKFLOW</div>
          <div className="az-arch-body">
            <p>
              <Link href="/">Home</Link>
              {' / '}
              <Link href="/workflows">Workflows</Link>
              {' / '}
              {workflow.title}
            </p>
            <Prose blocks={workflow.blocks} sources={workflow.sources} />
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
