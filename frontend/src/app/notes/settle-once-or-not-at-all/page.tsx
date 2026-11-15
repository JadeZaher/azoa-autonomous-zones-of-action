import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'

const SOURCES = [
  { label: 'AZOA architecture', href: 'https://azoa.ardanova.com/architecture' },
  { label: 'Reconciliation over blind retry', href: 'https://azoa.ardanova.com/workflows/reconciliation-over-blind-retry' },
  { label: 'Settle once workflow pattern', href: 'https://azoa.ardanova.com/workflows/settle-once' },
  { label: 'PrimusNeo', href: 'https://primusneo.com' },
]

export default function SettleOncePage() {
  return (
    <LegalDocument
      eyebrow="ENGINEERING NOTE · 9 NOVEMBER 2026"
      title="A retried step should settle once, or not at all"
      lede="A timeout is silence, not a verdict. Most systems treat a missing reply as permission to retry, which is how the same supplier or recipient gets paid twice — or left unpaid with two logs that both look complete."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">NOTE</div>
          <div className="az-arch-body">
            <p>
              A timeout is easy to misread. When a settlement call goes out and no response returns, one reasonable caller assumes it dropped and retries. Another assumes it went through and records completion. Those two assumptions applied to the same logical task produce either a duplicate transfer or an unpaid party holding what looks like a clean confirmation receipt. Nobody has to be malicious; a dropped TCP connection and an optimistic retry loop will do it on their own.
            </p>
            <p>
              AZOA — the Autonomous Zones of Action workflow engine built within the <a href="https://primusneo.com">PrimusNeo</a> engineering stack — exists to keep that record straight. Each workflow compiles into explicit steps that can pause, persist caller-supplied idempotency keys, reconcile against external state before taking irreversible actions, and avoid optimistic guesses.
            </p>
            <p>
              Honest state boundaries matter before code hits production. Hosted AZOA pages demonstrate action graphs against simulated payloads while live value bridging stays disabled; self-hosted deployments bind real clearing partners explicitly through narrow backend adapters.
            </p>
            <p>
              <a href="https://primusneo.com">PrimusNeo</a> documented this failure mode in public without dressing up a parked adapter as live money movement. Separating simulated verification from live clearing allows engineers to test exact idempotency semantics today: attach a durable key before sending, persist the initial response body, and probe status before dispatching a second attempt.
            </p>
            <p>
              The rest of AZOA&apos;s construction rules reinforce that invariant. Fail closed when any required permission or published version hash is missing. Transition indeterminate external responses into <code>AwaitingReconciliation</code> so sweeps confirm destination truth instead of spinning blind timers. Escalate truly ambiguous cases to an operator rather than fabricating confidence.
            </p>
            <p>
              When real-value adapters run inside self-hosted deployments or future pilots such as <a href="https://ardanova.com">ArdaNova</a>, the test will never be whether the marketing headline promises &quot;exactly once.&quot; The test is whether every retried step can be traced later to an unambiguous yes or no — and whether silence was never mistaken for either.
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
