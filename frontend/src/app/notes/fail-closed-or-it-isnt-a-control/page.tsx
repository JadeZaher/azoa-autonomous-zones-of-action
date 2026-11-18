import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'

const SOURCES = [
  { label: 'AZOA architecture', href: 'https://azoa.ardanova.com/architecture' },
  { label: 'Fail-closed safeguards', href: 'https://azoa.ardanova.com/workflows/fail-closed' },
  { label: 'Locking terms before approval', href: 'https://azoa.ardanova.com/workflows/version-pinned-execution' },
  { label: 'PrimusNeo', href: 'https://primusneo.com' },
  { label: 'ArdaNova', href: 'https://ardanova.com' },
]

export default function FailClosedPage() {
  return (
    <LegalDocument
      eyebrow="NOTE · 9 NOVEMBER 2026"
      title="Fail closed, or it isn't a control"
      lede="Most software would rather keep a progress bar green than pause when something looks wrong. A safeguard that writes a quiet warning and lets a step proceed anyway isn't a control — it's an assumption waiting to fail."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">ESSAY</div>
          <div className="az-arch-body">
            <p>
              When an approval is missing, terms shifted while someone was reviewing them, or an outside check has not answered back yet, the convenient shortcut is to log a warning and move forward anyway. The screen looks clean and the batch finishes on time — until someone has to reconstruct why funds moved under rules nobody actually approved.
            </p>
            <p>
              Failing closed is the opposite habit, and it is built into AZOA from the ground up: anything that cannot be verified is refused, not waved through. Stopping at a gate is not a glitch to hide; it is the whole point of having a gate in the first place.
            </p>
            <p>
              Built within the <a href="https://primusneo.com">PrimusNeo</a> stack, AZOA applies that rule across identity, consent, and agreements alike. Before any process runs, participants see the exact terms they are approving, and if anything changes before they confirm, the process refuses to start on stale terms. If a person withdraws permission partway through, future steps stop cleanly while preserving an honest history of what already happened.
            </p>
            <p>
              Just as important, AZOA is honest about the boundary between what software can guarantee automatically and what needs human judgment. When a situation is genuinely unclear, the engine pauses and brings it to an operator with full context instead of silently choosing an outcome. Pilots like <a href="https://ardanova.com">ArdaNova</a> run on those exact rules so real communities never have to trade transparency for automation.
            </p>
            <p>
              You can explore the open-source code, read our plain-language guides under{' '}
              <Link href="/workflows">Workflows</Link>, or see how{' '}
              <Link href="/workflows/version-pinned-execution">locking terms before approval</Link> prevents quiet surprises between review and sign-off. A line in a log file never protects an organization — only a system willing to stop when the proof isn&apos;t there does.
            </p>
            <h2 className="az-arch-h2">Related reading</h2>
            <ul>
              {SOURCES.map((source) => (
                <li key={source.href}>
                  <a href={source.href}>{source.label}</a>
                </li>
              ))}
              <li>
                <Link href="/terms">AZOA Terms of Use</Link>
              </li>
            </ul>
            <WorkflowFunnelGate context="workflow" />
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
