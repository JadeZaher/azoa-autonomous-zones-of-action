'use client'

import Link from 'next/link'
import { GITHUB, LandingHead, LandingNav, LandingFooter } from '@/components/landing/chrome'
import { HeroDag } from '@/components/landing/hero-dag'

const DOMAINS = [
  {
    n: '01',
    name: 'Payroll & Schedules',
    desc: 'Timeouts happen mid-batch when downstream partners stall. An action graph gives every payee transfer a durable idempotency key and a paused state you can resume safely.',
  },
  {
    n: '02',
    name: 'Supplier Settlement',
    desc: 'Bind deliverables, inspection gates, and split approvals into one version-pinned workflow so a partial completion triggers explicit compensating actions rather than manual spreadsheet cleanup.',
  },
  {
    n: '03',
    name: 'Grants & Milestones',
    desc: 'Park runs cheaply in AwaitingSignal or AwaitingTimer until reviewers publish evidence and cohorts pass quorum, keeping the entire approval audit slice attached to the run.',
  },
  {
    n: '04',
    name: 'Escrow Reserves',
    desc: 'Separate authorization from settlement using two-phase holds, bounded retries, and fail-closed version hashes so concurrent edits never execute under stale terms.',
  },
]

const PRIMITIVES = [
  {
    tag: 'WAIT / RESUME',
    name: 'Suspendable runs',
    desc: 'Park long-lived workflows in Suspended, AwaitingSignal, or AwaitingTimer for hours or weeks, then resume safely across server restarts.',
  },
  {
    tag: 'FAIL / CLOSED',
    name: 'Fail-closed guards',
    desc: 'Missing disclosures, revoked consent, or stale published version hashes abort execution with an explicit refusal rather than logging a quiet warning.',
  },
  {
    tag: 'POLICY / PRIVACY',
    name: 'Consent toggles',
    desc: 'Record who granted consent, for which purpose, and let withdrawal take effect immediately for downstream steps.',
  },
  {
    tag: 'SAGA / RECOVERY',
    name: 'Compensating actions',
    desc: 'When step four of a multi-party workflow fails, invoke declared compensating steps in reverse order instead of ad-hoc repair scripts.',
  },
  {
    tag: 'RETRY / KEY',
    name: 'Idempotency keys',
    desc: 'Every run start, advance, and signal carries an explicit caller key so dropped HTTP responses converge to the original result.',
  },
  {
    tag: 'SWEEP / STATE',
    name: 'Reconciliation loop',
    desc: 'Ambiguous external timeouts transition to AwaitingReconciliation so sweeps inspect actual destination truth before deciding whether to retry or fail.',
  },
]

const TRUST = [
  {
    n: '01 / SETTLE ONCE',
    desc: 'Save the first recorded outcome by idempotency key and re-probe timed-out destinations before resending, turning network silence into a settled yes or no.',
  },
  {
    n: '02 / FAIL CLOSED',
    desc: 'Every transition checks current authority, consent state, and published action hash. Anything unreadable stops at the gate.',
  },
  {
    n: '03 / RECONCILE FIRST',
    desc: 'Parked runs do not spin on blind timer retries. Operator sweeps and targeted probes confirm external state before advancing.',
  },
  {
    n: '04 / ESCALATE AMBIGUITY',
    desc: 'When a provider returns an indeterminate state, hold the run for human review instead of guessing.',
  },
]

export default function Home() {
  return (
    <div className="azoa-landing">
      <LandingHead />
      <LandingNav />

      <section className="az-hero">
        <HeroDag ink="#16120D" accent="#A63D15" />
        <div className="az-hero-fade" />
        <div className="az-hero-badge">
          <span className="az-dot" />
          <span>APACHE-2.0 · ACTION GRAPH ENGINE</span>
        </div>
        <div className="az-hero-content">
          <div className="az-eyebrow">AUTONOMOUS ZONES OF ACTION</div>
          <h1 className="az-h1">
            Durable
            <br />
            workflows.
            <br />
            Fail closed.
          </h1>
          <div className="az-hero-row">
            <p className="az-hero-lede">
              Write multi-step workflows that pause across human sign-offs, carry idempotency keys across retries, reconcile timed-out calls against live state, and refuse to proceed when consent or authority is missing.
            </p>
            <div className="az-hero-cta">
              <Link href="/workflows" className="az-btn az-btn--dark az-btn--md">
                WORKFLOW PATTERNS →
              </Link>
              <Link href="/architecture" className="az-btn az-btn--outline az-btn--md">
                ARCHITECTURE →
              </Link>
            </div>
          </div>
        </div>
      </section>

      <div className="az-ticker">
        <span>4 EXECUTION GUARANTEES</span>
        <span>EXPLICIT IDEMPOTENCY KEYS</span>
        <span>RECONCILE BEFORE RETRY</span>
        <span>HOSTED DEMO · SIMULATED PAYLOADS</span>
      </div>

      <section id="domains">
        <div className="az-wrap">
          <div className="az-shead">
            <div className="az-kicker">01 — WORKLOADS</div>
            <h2 className="az-h2">
              Explicit state machines
              <br />
              for risky coordination.
            </h2>
          </div>

          <div className="az-domains">
            {DOMAINS.map((d) => (
              <div className="az-domain" key={d.n}>
                <div className="mono">{d.n}</div>
                <h3>{d.name}</h3>
                <p>{d.desc}</p>
              </div>
            ))}
          </div>

          <div className="az-domains-note">
            <p>
              Built inside the <a href="https://primusneo.com" className="az-accent">PrimusNeo</a> engineering stack and tested against real operational workloads in the <a href="https://ardanova.com" className="az-accent">ArdaNova</a> workspace pilot.
            </p>
          </div>
        </div>
      </section>

      <section id="primitives" className="az-dark">
        <div className="az-wrap">
          <div className="az-shead">
            <div className="az-kicker">02 — PRIMITIVES</div>
            <h2 className="az-h2">Six workflow guards built into the run loop.</h2>
          </div>

          <div className="az-prim-grid">
            {PRIMITIVES.map((p) => (
              <div className="az-prim" key={p.name}>
                <div className="az-prim-tag">{p.tag}</div>
                <div className="az-prim-title">{p.name}</div>
                <p>{p.desc}</p>
              </div>
            ))}
          </div>

          <div className="az-prim-foot">
            <p>DESIGNED IN THE OPEN · TESTED AGAINST FAILURE MODES · ZERO SILENT PASSES</p>
            <Link href="/workflows" className="az-btn az-btn--outline-inv az-btn--md">
              READ WORKFLOW PATTERNS →
            </Link>
          </div>
        </div>
      </section>

      <section className="az-trust">
        <div className="az-trust-grid">
          <div className="az-kicker">03 — GUARANTEES</div>
          <div>
            <h2>
              Silence is never
              <br />
              treated as yes.
            </h2>
            <div className="az-trust-items">
              {TRUST.map((t) => (
                <div className="az-trust-item" key={t.n}>
                  <div className="lbl">{t.n}</div>
                  <p>{t.desc}</p>
                </div>
              ))}
            </div>
          </div>
        </div>
      </section>

      <section id="next">
        <div className="az-wrap">
          <div className="az-shead">
            <div className="az-kicker">04 — EXPLORE FURTHER</div>
            <h2 className="az-h2">Patterns, architecture, and live pilots.</h2>
          </div>

          <div className="az-next-grid">
            <Link href="/workflows" className="az-next-card az-next-card--outline">
              <div className="az-next-tag">FIELD NOTES &amp; PATTERNS</div>
              <div className="az-next-title">Workflows</div>
              <p>Sixteen engineering essays on idempotency, reconciliation sweeps, consent records, least privilege, and saga compensation.</p>
              <span className="az-next-more">BROWSE PATTERNS →</span>
            </Link>
            <Link href="/federation" className="az-next-card az-next-card--outline">
              <div className="az-next-tag">ZONE BOUNDARIES</div>
              <div className="az-next-title">Federation</div>
              <p>How independent workspaces preserve local records, share verifiable proofs across boundaries, and keep user portability intact.</p>
              <span className="az-next-more">EXPLORE FEDERATION →</span>
            </Link>
            <a href="https://ardanova.com" target="_blank" rel="noopener noreferrer" className="az-next-card az-next-card--dark">
              <div className="az-next-tag">WORKSPACE PILOT</div>
              <div className="az-next-title">ArdaNova</div>
              <p>A collaborative operations workspace piloting AZOA action graphs for contributor payouts, escrow workflows, and verifiable permissions.</p>
              <span className="az-next-more">VISIT ARDANOVA ↗</span>
            </a>
          </div>
        </div>
      </section>

      <section className="az-cta">
        <div className="az-cta-kicker">SELF-HOST OR INSPECT</div>
        <h2>Read the source.</h2>
        <p>Clone the Apache-2.0 repository and TypeScript SDK, or sign in to explore the interactive console with simulated payloads.</p>
        <div className="az-cta-row">
          <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--dark az-btn--lg">
            VIEW ON GITHUB ↗
          </a>
          <Link href="/architecture" className="az-btn az-btn--outline az-btn--lg">
            ARCHITECTURE →
          </Link>
          <Link href="/login" className="az-btn az-btn--outline az-btn--lg">
            OPEN CONSOLE →
          </Link>
        </div>
      </section>

      <LandingFooter />
    </div>
  )
}
