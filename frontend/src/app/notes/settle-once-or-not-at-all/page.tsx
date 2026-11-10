import type { Metadata } from 'next'
import { LegalDocument } from '@/components/landing/legal'

export const metadata: Metadata = {
  title: 'A retried step should settle once, or not at all — AZOA',
  description:
    'A timeout is not a decision. AZOA is being built so a retried step can be found later as a yes or a no. This public site is a simulation, and it does not move value.',
}

const SOURCES = [
  { label: 'AZOA homepage', href: 'https://azoa.ardanova.com' },
  { label: 'AZOA architecture', href: 'https://azoa.ardanova.com/architecture' },
  { label: 'AZOA terms', href: 'https://azoa.ardanova.com/terms' },
  { label: 'PrimusNeo', href: 'https://primusneo.com' },
]

export default function SettleOncePage() {
  return (
    <LegalDocument
      eyebrow="9 NOVEMBER 2026"
      title="A retried step should settle once, or not at all"
      lede="A timeout is not a decision. Most payment systems treat a missing reply as a reason to try again, which is how the same work gets paid twice, or not at all. AZOA is being built so a retried step can be found later as a yes or a no. This public site is a simulation, and it does not move value."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">NOTE</div>
          <div className="az-arch-body">
            <p>
              A timeout is not a decision. It is a silence, and silences are easy to misread. If a payment request goes out and no reply comes back, one reasonable person will assume it failed and try again. Another reasonable person, or the same person a minute later, will assume it went through and try to be sure. Those two assumptions, applied to the same work, are how a supplier gets paid twice, or how nobody gets paid and both sides can produce a log that looks complete. Nobody has to be cheating. A timeout and a retry will do it.
            </p>
            <p>
              AZOA, the Autonomous Zones of Action engine, is the layer in the <a href="https://primusneo.com">PrimusNeo</a> stack that is being built to keep that kind of record: a graph of steps that can wait, resume, and reconcile against the world outside the graph before an irreversible move. The public homepage already names the habit as &quot;exactly once&quot;: a retried or duplicated step settles once, or not at all. Never twice. That is the right ambition. It is not, on this hosted site, a fact about money, because this hosted site does not move money.
            </p>
            <p>
              The API health check is blunt about the present tense. Real-value bridging is disabled. No provider data has been recorded yet. The marketing page matches it, which is rarer than it should be: this public site is a simulation; fiat settlement is not enabled; there is no live fiat bridge, and no bank is connected. Screens that mention a payroll, a grant, or a payout describe the model. The terms put it in a sentence you could print: do not send funds here, and do not expect a payment from it.
            </p>
            <p>
              That limit is not a footnote to be admired and then forgotten when the prose gets going. It is the condition under which it is honest to talk about idempotency at all. An engine can be designed so that a step carries an identifier, so that a retry finds the original attempt, so that a second request does not become a second settlement. <a href="https://primusneo.com">PrimusNeo has already written about that bug in public</a>, as a lab problem, without pretending a parked rail is a live one. AZOA is the engine being built to hold the record. Until real-value bridging is enabled — and it is not enabled on this site — the claim has to stay in the model. &quot;They never double-spend&quot; is the wrong sentence for a simulation. &quot;A retried step is modelled to settle once, or not at all&quot; is the right one.
            </p>
            <p>
              The rest of the trust list on the homepage is the same shape. Fail closed: anything that cannot be verified is refused, not waved through. Reconcile, never guess: the engine is supposed to re-check the real world before an irreversible move. Escalate to a human when the decision is genuinely ambiguous, rather than inventing a confident answer. Those are construction rules. On this site they run against a simulation. They would be easy to dress as a consumer promise — a safer wallet, a smarter payroll, a bridge that just works. That dress is how a pre-launch engine becomes a sentence someone later has to unsay.
            </p>
            <p>
              <a href="https://ardanova.com">ArdaNova</a>, the planned first pilot, has the same present tense. It is a separate app, a workspace for shared work, and its own economy is still simulated. AZOA does not pay creators for it, and it does not bridge fiat for it. Linking the two is honest only if that sentence travels with the link. A planned consumer is not a live rail.
            </p>
            <p>
              The useful offer, today, is narrower and better. The source is Apache-2.0. You can run it, audit it, and host it yourself. The hosted site will show you the shape of an action graph and will not settle one. When the dangerous path is eventually opened, the test will not be whether the marketing page still says &quot;exactly once.&quot; The test will be whether a retried step can be found later as a yes or a no, and whether a silence was never mistaken for either.
            </p>
            <h2 className="az-arch-h2">Sources</h2>
            <ul>
              {SOURCES.map((source) => (
                <li key={source.href}><a href={source.href}>{source.label}</a></li>
              ))}
            </ul>
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
