'use client'

import type { ReactNode } from 'react'
import Link from 'next/link'
import { GITHUB, LandingHead, LandingNav, LandingFooter } from '@/components/landing/chrome'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'

const PILLARS = [
  {
    title: 'Freedom',
    body: 'Each workspace zone preserves its own business rules and audit logs. No single central hub can silently rewrite historical agreements or unilaterally override zone policies.',
  },
  {
    title: 'Privacy',
    body: 'Workflow payloads executed inside a zone remain inside that zone by default. Only explicit consent-gated assertions or verifiable receipts cross zone boundaries.',
  },
  {
    title: 'Agency',
    body: 'Participants keep portable identities and verifiable credentials across workspaces — leave a zone, join another, or operate across several concurrently.',
  },
]

const ARDANOVA: { label: string; body: ReactNode }[] = [
  {
    label: 'COMPANION WORKSPACE',
    body: (
      <>
        <a href="https://ardanova.com">ArdaNova</a> is a separate collaboration platform piloting AZOA action graphs for shared projects, role permissions, and milestone tracking.
      </>
    ),
  },
  {
    label: 'STATUS',
    body: 'Cross-zone federation and ArdaNova live settlement remain under active engineering development alongside the core single-zone runtime.',
  },
  {
    label: 'SIMULATED ECONOMY',
    body: 'ArdaNova pilot ledgers currently run simulated balances; hosted AZOA pages do not move customer funds or execute fiat payouts.',
  },
]

export default function Federation() {
  return (
    <div className="azoa-landing">
      <LandingHead />
      <LandingNav active="federation" />

      <header className="az-fed-header">
        <div className="az-wrap">
          <div className="az-eyebrow">FEDERATION · ARCHITECTURE SKETCH</div>
          <h1 className="az-h1">Zones that govern<br />themselves.</h1>
          <p className="az-fed-lede">
            AZOA&apos;s federation model lets autonomous organizations run isolated action graphs and exchange only consent-scoped proofs across zone boundaries. Single-zone workflows are active in the repository today; multi-zone federation is queued on the roadmap.
          </p>
        </div>
      </header>

      <section className="az-fed-section">
        <div className="az-wrap">
          <div className="az-fed-grid">
            {PILLARS.map((p) => (
              <div className="az-fed-cell" key={p.title}>
                <h3>{p.title}</h3>
                <p>{p.body}</p>
              </div>
            ))}
          </div>
          <p className="az-fed-note">
            Self-sovereign logs locally, explicit verifiable handoffs across boundaries.
          </p>
        </div>
      </section>

      <section id="ardanova" className="az-ardanova">
        <div className="az-wrap">
          <div className="az-ardanova-eyebrow">COMPANION PILOT</div>
          <h2 className="az-ardanova-title">ArdaNova</h2>
          <p className="az-ardanova-lede">Where shared workspaces meet AZOA action graphs.</p>
          <div className="az-ardanova-items">
            {ARDANOVA.map((a) => (
              <div className="az-ardanova-item" key={a.label}>
                <div className="lbl">{a.label}</div>
                <p>{a.body}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      <section className="az-cta az-cta--plain">
        <h2>Explore the architecture today.</h2>
        <p>Inspect single-zone execution patterns now or audit the Apache-2.0 source directly.</p>
        <div className="az-cta-row">
          <Link href="/workflows" className="az-btn az-btn--dark az-btn--lg">WORKFLOW PATTERNS →</Link>
          <Link href="/architecture" className="az-btn az-btn--outline az-btn--lg">THE ARCHITECTURE →</Link>
          <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--outline az-btn--lg">GITHUB ↗</a>
        </div>
        <div className="az-wrap" style={{ textAlign: 'left', marginTop: 48 }}>
          <WorkflowFunnelGate context="architecture" />
        </div>
      </section>

      <LandingFooter />
    </div>
  )
}
