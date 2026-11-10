'use client'

import Link from 'next/link'
import { GITHUB, LandingHead, LandingNav, LandingFooter } from '@/components/landing/chrome'
import { HeroDag } from '@/components/landing/hero-dag'

// AZOA marketing landing page — imported from the Claude Design project
// "AZOA — Autonomous Zones of Action" (Home.dc.html). Shared chrome (styles,
// nav, footer) lives in components/landing/chrome. See src/app/AGENTS.md §landing.

const DOMAINS = [
  {
    title: 'Financial workflows',
    body: 'Payouts, escrows, grants, and settlements, described as steps that can wait and resume. On this site they are simulated. Nothing here sends a payout.',
    icon: (
      <svg viewBox="0 0 48 48" width="48" height="48">
        <path d="M6 36 L20 20 L30 28 L42 10" fill="none" stroke="#16120D" strokeWidth="2" />
        <circle cx="42" cy="10" r="4" fill="#C8501E" />
      </svg>
    ),
  },
  {
    title: 'Circular economies',
    body: 'Materials, credits, and obligations that loop, tracked from first issue to final return in the model. This site does not settle them.',
    icon: (
      <svg viewBox="0 0 48 48" width="48" height="48">
        <circle cx="24" cy="24" r="16" fill="none" stroke="#16120D" strokeWidth="2" />
        <path d="M40 24 L44 19 M40 24 L35 20" fill="none" stroke="#C8501E" strokeWidth="2" />
      </svg>
    ),
  },
  {
    title: 'Operational processes',
    body: 'Approvals, handoffs, and logistics — ordinary steps recorded so a run can be audited instead of reconstructed afterward.',
    icon: (
      <svg viewBox="0 0 48 48" width="48" height="48">
        <rect x="6" y="20" width="10" height="10" fill="none" stroke="#16120D" strokeWidth="2" />
        <rect x="32" y="8" width="10" height="10" fill="none" stroke="#16120D" strokeWidth="2" />
        <rect x="32" y="32" width="10" height="10" fill="#C8501E" />
        <path d="M16 25 L32 13 M16 25 L32 37" stroke="#16120D" strokeWidth="2" />
      </svg>
    ),
  },
  {
    title: 'Grants and bounties',
    body: 'Applications and milestones, with a release that waits on a check. This site does not release funds or send a refund.',
    icon: (
      <svg viewBox="0 0 48 48" width="48" height="48">
        <path d="M24 6 L42 24 L24 42 L6 24 Z" fill="none" stroke="#16120D" strokeWidth="2" />
        <circle cx="24" cy="24" r="5" fill="#C8501E" />
      </svg>
    ),
  },
]

const PRIMITIVES = [
  { tag: 'IDENTITY', title: 'A user you actually own', body: 'Apps act for you only with consent you can see — and revoke — at any moment.' },
  { tag: 'ACTIONS', title: 'Durable graphs of action', body: 'They wait, resume, and reconcile against reality before acting. A retried or duplicated step is modelled to settle once, or not at all. This site does not move value.' },
  { tag: 'HOLONS', title: 'Value as nested wholes', body: 'Credits, rights, and records — anything the model can hold, split, and move. Holding one here is not a payout.' },
  { tag: 'RAILS', title: 'One settlement seam', body: 'A step can name a network or a fiat partner. Fiat is not enabled on this site. There is no live fiat bridge, and no bank is connected.' },
  { tag: 'BUNDLES', title: 'Whole apps, generated', body: 'Action graph bundles scaffold new products on top of identity and actions. Nobody rebuilds the plumbing.' },
]

const TRUST = [
  { label: 'EXACTLY ONCE', body: 'A retried or duplicated step settles once, or not at all. Never twice.' },
  { label: 'FAIL CLOSED', body: "Anything that can't be verified is refused — not waved through." },
  { label: 'RECONCILE, NEVER GUESS', body: 'The engine re-checks the real world before every irreversible move.' },
  { label: 'ESCALATE TO A HUMAN', body: 'Genuinely ambiguous decisions are surfaced to an operator — never faked.' },
  { label: 'SIMULATED HERE', body: 'On this public site those rules run against a simulation. Real-value bridging is disabled. Fiat settlement is not enabled yet, and identity checks for real money are not enabled yet.' },
]

export default function Home() {
  return (
    <div className="azoa-landing">
      <LandingHead />
      <LandingNav />

      <header className="az-hero">
        <HeroDag ink="#16120D" accent="#C8501E" speed={1} density={1} />
        <div className="az-hero-fade" />
        <div className="az-hero-content">
          <div className="az-eyebrow">AUTONOMOUS ZONES OF ACTION</div>
          <h1 className="az-h1">One engine.<br />Every kind of&nbsp;economy.</h1>
          <div className="az-hero-row">
            <p className="az-hero-lede">
              AZOA models financial workflows, circular economies, operational processes and grant programs as graphs of action. This public site is a simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet. AZOA is the workflow-engine layer of <a href="https://primusneo.com">PrimusNeo, the lab whose stack this engine belongs to</a>. It is not a consumer app, and this hosted site does not move money.
            </p>
            <div className="az-hero-cta">
              <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--dark az-btn--md">VIEW ON GITHUB ↗</a>
              <a href="#domains" className="az-btn az-btn--outline az-btn--md">HOW IT WORKS ↓</a>
            </div>
          </div>
        </div>
        <div className="az-hero-badge">
          <span className="az-dot" />
          SIMULATED — DEMO GRAPH
        </div>
      </header>

      <div className="az-ticker">
        <span>FINANCE</span><span>/</span><span>CIRCULARITY</span><span>/</span><span>OPERATIONS</span><span>/</span><span>GRANTS</span>
      </div>

      <section id="domains">
        <div className="az-shead az-wrap">
          <div className="az-kicker">01 — DOMAINS</div>
          <h2 className="az-h2">Four economies.<br />One grammar.</h2>
        </div>
        <div className="az-domains">
          {DOMAINS.map((d) => (
            <div className="az-domain" key={d.title}>
              {d.icon}
              <h3>{d.title}</h3>
              <p>{d.body}</p>
            </div>
          ))}
        </div>
        <div className="az-domains-note">
          <p>
            Each of these is a graph of steps over the same primitives. A milestone, a credit, and a payroll line can share that shape in the model. This site does not pay a supplier, run payroll, or pay yield. Real-value bridging is disabled, and fiat settlement is not enabled yet.
          </p>
        </div>
      </section>

      <section id="primitives" className="az-dark">
        <div className="az-wrap">
          <div className="az-shead">
            <div className="az-kicker">02 — PRIMITIVES</div>
            <h2 className="az-h2">The five moves<br />underneath it all.</h2>
          </div>
          <div className="az-prim-grid">
            {PRIMITIVES.map((p) => (
              <div className="az-prim" key={p.tag}>
                <div className="az-prim-tag">{p.tag}</div>
                <div className="az-prim-title">{p.title}</div>
                <p>{p.body}</p>
              </div>
            ))}
          </div>
          <div className="az-prim-foot">
            <p>SIMULATED · REAL-VALUE BRIDGING OFF · FIAT NOT ENABLED · SELF-HOSTED</p>
            <Link href="/architecture" className="az-btn az-btn--outline-inv az-btn--md">FULL ARCHITECTURE →</Link>
          </div>
        </div>
      </section>

      <section className="az-trust">
        <div className="az-trust-grid">
          <div className="az-kicker">03 — TRUST</div>
          <div>
            <h2>Honest by<br />construction.</h2>
            <div className="az-trust-items">
              {TRUST.map((t) => (
                <div className="az-trust-item" key={t.label}>
                  <div className="lbl">{t.label}</div>
                  <p>{t.body}</p>
                </div>
              ))}
            </div>
          </div>
        </div>
      </section>

      <section id="next">
        <div className="az-wrap">
          <div className="az-shead">
            <div className="az-kicker">04 — NEXT</div>
            <h2 className="az-h2">What's coming.</h2>
          </div>
          <div className="az-next-grid">
            <Link href="/federation" className="az-next-card az-next-card--outline">
              <div className="az-next-tag">ON ITS WAY</div>
              <div className="az-next-title">Federation</div>
              <p>A sketch of separate zones that keep their own records and could later exchange them. Federation is not running on this site.</p>
              <div className="az-next-more">READ THE VISION →</div>
            </Link>
            <div className="az-next-card az-next-card--dark">
              <div className="az-next-tag">PLANNED PILOT</div>
              <div className="az-next-title">Ardanova</div>
              <p>A planned pilot. Ardanova is a separate app, and its economy is still simulated. This site does not pay creators or bridge fiat for it.</p>
              <a className="az-next-more" href="https://ardanova.com">ArdaNova, a planned pilot whose economy is still simulated</a>
            </div>
          </div>
        </div>
      </section>

      <section className="az-cta">
        <div className="az-cta-kicker">APACHE-2.0 · OPEN SOURCE</div>
        <h2>Run it. Audit it.<br />Own it.</h2>
        <p>The source is Apache-2.0 and can be self-hosted. This public site is a simulation. Real-value bridging is disabled, and fiat settlement is not enabled yet.</p>
        <div className="az-cta-row">
          <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--dark az-btn--lg">VIEW ON GITHUB ↗</a>
          <Link href="/login" className="az-btn az-btn--outline az-btn--lg">LOGIN →</Link>
        </div>
      </section>

      <LandingFooter />
    </div>
  )
}
