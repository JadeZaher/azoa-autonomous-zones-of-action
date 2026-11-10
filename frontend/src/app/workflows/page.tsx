import { LegalDocument } from '@/components/landing/legal'
import { Prose } from '@/components/landing/prose'
import { WORKFLOW_INDEX } from '@/content/workflows'

export default function WorkflowsPage() {
  return (
    <LegalDocument
      eyebrow={WORKFLOW_INDEX.eyebrow}
      title={WORKFLOW_INDEX.h1}
      lede={WORKFLOW_INDEX.lede}
      active="workflows"
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">INDEX</div>
          <div className="az-arch-body">
            <Prose blocks={WORKFLOW_INDEX.blocks} />
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
