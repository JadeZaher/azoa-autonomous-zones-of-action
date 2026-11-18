import Link from 'next/link'
import { LegalDocument } from '@/components/landing/legal'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'

const SOURCES = [
  { label: 'AZOA architecture', href: 'https://azoa.ardanova.com/architecture' },
  { label: 'Checking real state before retrying', href: 'https://azoa.ardanova.com/workflows/reconciliation-over-blind-retry' },
  { label: 'Settle once guide', href: 'https://azoa.ardanova.com/workflows/settle-once' },
  { label: 'PrimusNeo', href: 'https://primusneo.com' },
]

export default function SettleOncePage() {
  return (
    <LegalDocument
      eyebrow="NOTE · 9 NOVEMBER 2026"
      title="A retried step should settle once, or not at all"
      lede="A timeout is silence, not a verdict. Most software treats a missing reply as permission to try again — which is how the same work gets paid twice, or missed completely while everyone thinks it finished."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">ESSAY</div>
          <div className="az-arch-body">
            <p>
              A silence is easy to misread. When an approval or payout goes out and no answer comes back, one reasonable person assumes the request got lost and sends it again. Another person — or an automatic retry a minute later — assumes it went through and marks the work done. Both reactions make sense in isolation, yet together they create duplicate payments or leave contributors unpaid while every dashboard says complete. Nobody has to be dishonest for that to happen; an everyday network hiccup is enough.
            </p>
            <p>
              AZOA — the Autonomous Zones of Action engine built in the{' '}
              <a href="https://primusneo.com">PrimusNeo</a> lab — exists to keep that record honest. Instead of gluing together fragile scripts and background timers, AZOA models financial workflows, circular economies, organizational approvals, and grants as living graphs of action. Every step knows what it has already asked for, can pause for hours or days while the real world catches up, and checks what actually happened before making any irreversible move.
            </p>
            <p>
              One rule sits at the center of that design: a retried or duplicated step settles once, or not at all. Never twice. When an outside partner or network goes quiet mid-step, AZOA does not fire off blind retries hoping for the best. It pauses, remembers the original attempt, and checks the source of truth before deciding what comes next.
            </p>
            <p>
              <a href="https://primusneo.com">PrimusNeo</a> wrote openly about this exact gap because most workflow systems gloss over it. By separating the core rules of an agreement from whatever network or bank sits underneath, teams can test and prove those guarantees cleanly before real money ever enters the picture.
            </p>
            <p>
              The rest of AZOA&apos;s principles reinforce the same promise. Anything that cannot be verified is refused instead of waved through. Every action belongs to an identity a person actually owns, under consent they can see and revoke. And when a decision is genuinely ambiguous, the engine surfaces the full context to a human operator rather than inventing a guess.
            </p>
            <p>
              Whether someone runs a community fund, a supplier agreement, or a creator workspace like{' '}
              <a href="https://ardanova.com">ArdaNova</a>, the real test of an economy engine is never a marketing promise — it is whether every step can be traced back to an honest yes or no, and whether silence was never mistaken for either.
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
