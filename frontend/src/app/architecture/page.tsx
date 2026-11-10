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
          <h1 className="az-h1">A workflow the<br />engine can check.</h1>
          <p className="az-fed-lede">
            Financial processes want two things that usually fight each other: to be dynamic — branch, wait, span systems and days — and to be structured — auditable, exact, never losing a step. AZOA is the substrate built for both. This hosted site runs that substrate as a simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet.
          </p>
        </div>
      </header>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">01 — THE ACTION GRAPH</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">A durable state machine, drawn as a graph.</h2>
            <p>
              An action is a graph of steps — gates, transfers, swaps, grants, calls to the outside world. It parks when it needs to wait. It resumes on a signal or a timer. And before it takes any irreversible action, it reconciles against the source of truth: a network confirmation, a settlement callback, the world outside the graph.
            </p>
            <p>
              Steps carry their results forward as bindings, failed checks stop later steps, and every run survives restarts. Build actions visually — drag, drop, connect — and publish only when the graph validates. On this site those steps are simulated. They do not send a payout.
            </p>
            {/* Wide horizontal graph — desktop/tablet. */}
            <svg viewBox="0 0 720 140" className="az-arch-diagram az-arch-diagram--desktop">
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
            {/* Vertical graph — mobile, tuned for narrow width + legible labels. */}
            <svg viewBox="0 0 320 452" className="az-arch-diagram--mobile">
              <line x1="160" y1="40" x2="96" y2="140" stroke="#16120D" strokeWidth="2" />
              <line x1="160" y1="40" x2="224" y2="140" stroke="#16120D" strokeWidth="2" />
              <line x1="96" y1="140" x2="160" y2="236" stroke="#16120D" strokeWidth="2" />
              <line x1="224" y1="140" x2="160" y2="236" stroke="#C8501E" strokeWidth="2.5" />
              <line x1="160" y1="236" x2="96" y2="332" stroke="#16120D" strokeWidth="2" />
              <line x1="160" y1="236" x2="224" y2="332" stroke="#16120D" strokeWidth="2" />
              <line x1="96" y1="332" x2="160" y2="428" stroke="#16120D" strokeWidth="2" />
              <line x1="224" y1="332" x2="160" y2="428" stroke="#16120D" strokeWidth="2" />
              <circle cx="160" cy="40" r="11" fill="#16120D" />
              <circle cx="96" cy="140" r="11" fill="none" stroke="#16120D" strokeWidth="2.5" />
              <circle cx="224" cy="140" r="11" fill="#C8501E" />
              <circle cx="160" cy="236" r="11" fill="none" stroke="#16120D" strokeWidth="2.5" />
              <circle cx="96" cy="332" r="11" fill="none" stroke="#16120D" strokeWidth="2.5" />
              <circle cx="224" cy="332" r="11" fill="none" stroke="#16120D" strokeWidth="2.5" />
              <circle cx="160" cy="428" r="11" fill="#16120D" />
              <text x="182" y="45" fontFamily="IBM Plex Mono" fontSize="14" fill="#6f665a" textAnchor="start">START</text>
              <text x="246" y="145" fontFamily="IBM Plex Mono" fontSize="14" fill="#C8501E" textAnchor="start">ACTIVE</text>
              <text x="182" y="241" fontFamily="IBM Plex Mono" fontSize="14" fill="#6f665a" textAnchor="start">GATE</text>
              <text x="182" y="433" fontFamily="IBM Plex Mono" fontSize="14" fill="#6f665a" textAnchor="start">SETTLE</text>
            </svg>
          </div>
        </div>
      </section>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">02 — SETTLEMENT</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">Networks and fiat, the same shape.</h2>
            <p>
              AZOA treats a network and a fiat partner as two kinds of settlement behind one seam. Assets on a network move through adapters. Fiat is designed as one exact callback after a partner has already cleared funds in that partner&apos;s own checkout. That callback is not enabled on this site. There is no live fiat bridge.
            </p>
            <p>
              A workflow can name either shape. On this site the fiat shape does not run, real-value bridging is disabled, and identity checks for real money are not enabled yet.
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
              At the center is a self-sovereign user — an identity a person actually owns, proven by keys they hold, not an account a platform lends them. Applications act on a user&apos;s behalf only under a live, explicit, revocable grant of consent.
            </p>
            <p>
              That makes every step in every workflow attributable to a real subject, and makes delegated action safe: withdraw consent, and the app loses the permission to act for you. On this site that permission does not move real money.
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
              Everything a workflow can hold or move is a holon — a whole that can contain other wholes. A credit, a right, a bundle of both. Because value is modeled one way, an action built for one economy composes with records from another. A grant line, a material credit, and a payroll line are the same kind of record to the engine. Saving that record does not send a grant, issue a credit, or run payroll.
            </p>
          </div>
        </div>
      </section>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">05 — BUNDLES</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">From primitive to product.</h2>
            <p>
              An action graph bundle generates and deploys whole applications on top of the identity and action layers. A new marketplace, a co-op ledger, a community fund — scaffolded from the primitives instead of rebuilt from scratch. New networks, new exchanges, new settlement partners, and new step types all plug in through the same open interfaces. None of that is a live payout on this site. A planned consumer of that shape is <a href="https://ardanova.com">the ArdaNova workspace</a>, and its economy is still simulated.
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
              AZOA is deliberate about what software can guarantee and what a human must own. The safe path is automatic: a repeated step settles once, fail-closed verification, reconciliation before action. The ambiguous path is explicit: when a workflow is genuinely stuck, the engine flags it for a person — with full context — instead of silently resolving it. On this site those checks run against a simulation. They do not settle real funds.
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
