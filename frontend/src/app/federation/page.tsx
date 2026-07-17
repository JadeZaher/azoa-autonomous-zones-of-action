'use client'

import Link from 'next/link'
import { GITHUB, LandingHead, LandingNav, LandingFooter } from '@/components/landing/chrome'

// AZOA Federation page — imported from the Claude Design project
// "AZOA — Autonomous Zones of Action" (Federation.dc.html). Shares the landing
// chrome; the "Architecture" links point at the home /#primitives section since
// a dedicated Architecture route is not yet implemented.

const PILLARS = [
  { title: 'Freedom', body: 'Every zone runs its own node, its own rules, its own economy. No central operator to appease, no platform that can change the deal underneath you.' },
  { title: 'Privacy', body: 'What happens inside a zone stays inside it. Only what a zone chooses to share crosses the boundary — under consent that its members control.' },
  { title: 'Agency', body: 'Your avatar and your value travel with you. Leave a zone, join another, or belong to ten at once — your identity is yours, not theirs.' },
]

const ARDANOVA = [
  { label: 'CREATORS OWN THE UPSIDE', body: "Work, audience, and earnings that belong to the people who make them — not to a platform's terms of service." },
  { label: 'BUILT ON AZOA', body: 'The first zone to run on the engine: self-sovereign identity, durable workflows, and value that settles across chains and fiat alike.' },
  { label: 'PROOF, NOT PROMISE', body: 'Ardanova is the working demonstration that a fairer economy can run on open, self-hosted infrastructure.' },
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
            An autonomous zone of action is exactly that — autonomous. Federation lets zones stay sovereign and still transact, so no one has to choose between independence and connection.
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
            Distributed governance is not a feature. It's the point: <span className="az-accent">communities that own their economies</span>, and an internet of zones that trade as equals.
          </p>
        </div>
      </section>

      <section id="ardanova" className="az-ardanova">
        <div className="az-wrap">
          <div className="az-ardanova-eyebrow">FIRST PILOT — COMING SOON</div>
          <h2 className="az-ardanova-title">Ardanova</h2>
          <p className="az-ardanova-lede">The new creator economy. A fair deal for the digital age.</p>
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
        <p>The engine is open source and ready to self-host today. Federation lands on top of what you build now.</p>
        <div className="az-cta-row">
          <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--dark az-btn--lg">VIEW ON GITHUB ↗</a>
          <Link href="/#primitives" className="az-btn az-btn--outline az-btn--lg">THE ARCHITECTURE →</Link>
        </div>
      </section>

      <LandingFooter />
    </div>
  )
}
