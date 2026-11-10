'use client'

import type { ReactNode } from 'react'
import Link from 'next/link'
import { GITHUB, LandingHead, LandingNav, LandingFooter } from '@/components/landing/chrome'

// AZOA Federation page — imported from the Claude Design project
// "AZOA — Autonomous Zones of Action" (Federation.dc.html). Shares the landing
// chrome; the "Architecture" links point at the /architecture route.

const PILLARS = [
  { title: 'Freedom', body: 'The plan is that every zone keeps its own rules and its own records. No central operator rewriting the deal. That plan is not running on this site.' },
  { title: 'Privacy', body: 'What happens inside a zone stays inside it. Only what a zone chooses to share crosses the boundary — under consent that its members control.' },
  { title: 'Agency', body: 'Your account and your records travel with you. Leave a zone, join another, or belong to several at once — the identity is yours.' },
]

const ARDANOVA: { label: string; body: ReactNode }[] = [
  { label: 'SEPARATE APP', body: <><a href="https://ardanova.com">Ardanova</a> is a different product. Its own API reports a simulated economy. This site does not pay creators, run payroll, or bridge fiat for it.</> },
  { label: 'PLANNED PILOT', body: 'A first pilot means it is planned. It is a sketch of a later zone, and it does not settle from this site.' },
  { label: 'SIMULATED', body: 'Until real-value bridging is enabled, treat every earnings or payout line about Ardanova as a sketch. There is no yield here.' },
]

export default function Federation() {
  return (
    <div className="azoa-landing">
      <LandingHead />
      <LandingNav active="federation" />

      <header className="az-fed-header">
        <div className="az-wrap">
          <div className="az-eyebrow">FEDERATION — ON ITS WAY</div>
          <h1 className="az-h1">Zones that govern<br />themselves.</h1>
          <p className="az-fed-lede">
            Federation is a plan for zones that keep their own rules and still exchange records with each other. It is not running on this site. Real-value bridging is disabled, and fiat settlement is not enabled yet.
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
            The aim is zones that keep their own records and deal with each other directly. That exchange is not live, and nothing on this page is a payout.
          </p>
        </div>
      </section>

      <section id="ardanova" className="az-ardanova">
        <div className="az-wrap">
          <div className="az-ardanova-eyebrow">PLANNED PILOT</div>
          <h2 className="az-ardanova-title">Ardanova</h2>
          <p className="az-ardanova-lede">A planned pilot. Its economy is simulated. Nobody is paid from this page.</p>
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
        <h2>Build your zone.</h2>
        <p>The source is open and can be self-hosted. Federation is later work. This public site stays a simulation until real-value bridging is enabled.</p>
        <div className="az-cta-row">
          <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--dark az-btn--lg">VIEW ON GITHUB ↗</a>
          <Link href="/architecture" className="az-btn az-btn--outline az-btn--lg">THE ARCHITECTURE →</Link>
        </div>
      </section>

      <LandingFooter />
    </div>
  )
}
