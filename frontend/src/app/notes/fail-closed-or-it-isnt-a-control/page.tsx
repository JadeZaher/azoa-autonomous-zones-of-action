import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'

const SOURCES = [
  { label: 'AZOA architecture', href: 'https://azoa.ardanova.com/architecture' },
  { label: 'Fail-closed workflow controls', href: 'https://azoa.ardanova.com/workflows/fail-closed' },
  { label: 'Version-pinned execution', href: 'https://azoa.ardanova.com/workflows/version-pinned-execution' },
  { label: 'PrimusNeo', href: 'https://primusneo.com' },
  { label: 'ArdaNova', href: 'https://ardanova.com' },
]

export default function FailClosedPage() {
  return (
    <LegalDocument
      eyebrow="ENGINEERING NOTE · 9 NOVEMBER 2026"
      title="Fail closed, or it isn't a control"
      lede="Most distributed systems would rather keep returning 200 OK than halt when verification stalls. A control that logs a warning and lets execution proceed anyway is not a safeguard — it is an unnoticed breach waiting for traffic."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">NOTE</div>
          <div className="az-arch-body">
            <p>
              When an upstream verification check cannot finish — a disclosure manifest changed concurrently, an approval expired, or a downstream adapter stopped replying — the tempting engineering shortcut is to log a warning and advance the job anyway. The dashboard stays green. The batch completes on schedule. What you have built at that moment is not a control; it is a decoration that hides broken assumptions until audit time.
            </p>
            <p>
              Fail closed is the opposite engineering habit: anything that cannot be cleanly verified is explicitly refused. Refusal is a first-class state outcome, never an embarrassing exception to swallow silently. An action graph that cannot park or refuse is simply a checklist wearing workflow labels.
            </p>
            <p>
              AZOA enforces that rule across identity, consent, and version boundaries as part of the <a href="https://primusneo.com">PrimusNeo</a> engineering stack. Before starting a workflow, callers fetch an economic disclosure preview and pin <code>expectedPublishedHash</code>; concurrent graph edits reject execution cleanly rather than running outdated terms. Withdrawn consent halts upcoming steps cleanly without corrupting historical records.
            </p>
            <p>
              The same honesty governs product scope. Hosted AZOA environments run simulated workflow payloads with live fiat bridging disabled, while real clearing adapters stay isolated behind explicit configuration for self-hosted deployments. Pilots such as <a href="https://ardanova.com">ArdaNova</a> integrate with AZOA under those exact boundaries rather than loosening checks to look launch-ready.
            </p>
            <p>
              Read the Apache-2.0 source, explore the pattern guides under <Link href="/workflows">Workflows</Link>, or inspect how <Link href="/workflows/version-pinned-execution">version-pinned execution</Link> prevents race conditions between preview and approval. A warning line in a logfile never protects an operation — only a step willing to stop at the gate does.
            </p>
            <h2 className="az-arch-h2">Sources &amp; related reading</h2>
            <ul>
              {SOURCES.map((source) => (
                <li key={source.href}><a href={source.href}>{source.label}</a></li>
              ))}
              <li><Link href="/terms">AZOA Terms of Use</Link></li>
            </ul>
            <WorkflowFunnelGate context="workflow" />
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
