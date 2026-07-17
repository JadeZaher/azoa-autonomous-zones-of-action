'use client'

import { GITHUB, LandingHead, LandingNav, LandingFooter } from '@/components/landing/chrome'

// AZOA Architecture page — imported from the Claude Design project
// "AZOA — Autonomous Zones of Action" (Architecture.dc.html). Shares the landing
// chrome. See src/app/AGENTS.md §landing.

export default function Architecture() {
  return (
    <div className="azoa-landing">
      <LandingHead />
      <LandingNav active="architecture" />

      <header className="az-fed-header">
        <div className="az-wrap">
          <div className="az-eyebrow">ARCHITECTURE</div>
          <h1 className="az-h1">A workflow you can<br />trust with value.</h1>
          <p className="az-fed-lede">
            Financial processes want two things that usually fight each other: to be dynamic — branch, wait, span systems and days — and to be structured — auditable, exact, never losing a step. AZOA is the substrate that gives you both.
          </p>
        </div>
      </header>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">01 — THE QUEST GRAPH</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">A durable state machine, drawn as a graph.</h2>
            <p>
              A quest is a graph of steps — gates, transfers, swaps, grants, calls to the outside world. It parks when it needs to wait. It resumes on a signal or a timer. And before it takes any irreversible action, it reconciles against the source of truth: a chain confirmation, a settlement callback, the real world.
            </p>
            <p>
              Steps carry their results forward as bindings, failed checks cascade to stop downstream payouts, and every run survives restarts. Build quests visually — drag, drop, connect — and publish only when the graph validates.
            </p>
            <svg viewBox="0 0 720 140" className="az-arch-diagram">
              <line x1="60" y1="70" x2="200" y2="30" stroke="#16120D" strokeWidth="1.5" />
              <line x1="60" y1="70" x2="200" y2="110" stroke="#16120D" strokeWidth="1.5" />
              <line x1="200" y1="30" x2="360" y2="70" stroke="#16120D" strokeWidth="1.5" />
              <line x1="200" y1="110" x2="360" y2="70" stroke="#C8501E" strokeWidth="2" />
              <line x1="360" y1="70" x2="520" y2="40" stroke="#16120D" strokeWidth="1.5" />
              <line x1="360" y1="70" x2="520" y2="100" stroke="#16120D" strokeWidth="1.5" />
              <line x1="520" y1="40" x2="660" y2="70" stroke="#16120D" strokeWidth="1.5" />
              <line x1="520" y1="100" x2="660" y2="70" stroke="#16120D" strokeWidth="1.5" />
              <circle cx="60" cy="70" r="9" fill="#16120D" />
              <circle cx="200" cy="30" r="9" fill="none" stroke="#16120D" strokeWidth="2" />
              <circle cx="200" cy="110" r="9" fill="#C8501E" />
              <circle cx="360" cy="70" r="9" fill="none" stroke="#16120D" strokeWidth="2" />
              <circle cx="520" cy="40" r="9" fill="none" stroke="#16120D" strokeWidth="2" />
              <circle cx="520" cy="100" r="9" fill="none" stroke="#16120D" strokeWidth="2" />
              <circle cx="660" cy="70" r="9" fill="#16120D" />
              <text x="60" y="105" fontFamily="IBM Plex Mono" fontSize="11" fill="#6f665a" textAnchor="middle">START</text>
              <text x="200" y="140" fontFamily="IBM Plex Mono" fontSize="11" fill="#C8501E" textAnchor="middle">ACTIVE</text>
              <text x="360" y="105" fontFamily="IBM Plex Mono" fontSize="11" fill="#6f665a" textAnchor="middle">GATE</text>
              <text x="660" y="105" fontFamily="IBM Plex Mono" fontSize="11" fill="#6f665a" textAnchor="middle">SETTLE</text>
            </svg>
          </div>
        </div>
      </section>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">02 — RAILS</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">Blockchain and fiat, the same shape.</h2>
            <p>
              AZOA treats a blockchain and a fiat partner as two kinds of settlement rail behind one uniform seam. On-chain assets move through provider adapters. Fiat clears in the partner&apos;s own checkout — then makes one exact, verified call into AZOA to materialize the value. Once, and exactly once.
            </p>
            <p>
              A quest doesn&apos;t care which rail a step settles on. That is the point: the same workflow can move on-chain value and fiat-originated value in a single run — multichain, multi-wallet, one graph.
            </p>
          </div>
        </div>
      </section>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">03 — IDENTITY &amp; CONSENT</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">Every action has an owner.</h2>
            <p>
              At the center is a self-sovereign avatar — an identity a person actually owns, proven by keys they hold, not an account a platform lends them. Applications act on an avatar&apos;s behalf only under a live, explicit, revocable grant of consent.
            </p>
            <p>
              That makes every step in every workflow attributable to a real subject, and makes delegated action safe: withdraw consent, and the app&apos;s hands leave your value immediately.
            </p>
          </div>
        </div>
      </section>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">04 — HOLONS</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">Wholes made of wholes.</h2>
            <p>
              Everything a workflow can hold or move is a holon — a whole that can contain other wholes. A token, a credit, a right, a bundle of all three. Because value is modeled one way, a quest built for one economy composes with value from any other. This is what makes a game reward, a material credit, and a payroll line the same kind of thing to the engine.
            </p>
          </div>
        </div>
      </section>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">05 — STAR</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">From primitive to product.</h2>
            <p>
              STAR generates and deploys whole applications on top of the identity and quest layers. A new marketplace, a co-op ledger, a game economy — scaffolded from the primitives instead of rebuilt from scratch. New chains, new exchanges, new settlement partners, and new step types all plug in through the same open interfaces.
            </p>
          </div>
        </div>
      </section>

      <section className="az-arch-section az-arch-section--dark">
        <div className="az-arch-grid">
          <div className="az-kicker">06 — THE OPERATOR PATTERN</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">Honest about the line.</h2>
            <p>
              AZOA is deliberate about what software can guarantee and what a human must own. The safe path is automatic: exactly-once settlement, fail-closed verification, reconciliation before action. The ambiguous path is explicit: when a workflow is genuinely stuck, the engine flags it for a person — with full context — instead of silently resolving it.
            </p>
            <p>
              Some decisions belong to the operator. AZOA surfaces them instead of faking them.
            </p>
            <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--outline-inv az-btn--md" style={{ marginTop: '36px' }}>READ THE SOURCE ↗</a>
          </div>
        </div>
      </section>

      <LandingFooter />
    </div>
  )
}
