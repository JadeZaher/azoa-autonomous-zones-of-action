'use client'

import Link from 'next/link'
import { GITHUB, LandingHead, LandingNav, LandingFooter } from '@/components/landing/chrome'
import { WorkflowFunnelGate } from '@/components/landing/workflow-gate'

export default function Architecture() {
  return (
    <div className="azoa-landing">
      <LandingHead />
      <LandingNav active="architecture" />

      <header className="az-fed-header">
        <div className="az-wrap">
          <div className="az-eyebrow">ARCHITECTURE</div>
          <h1 className="az-h1">A workflow the engine<br />can verify.</h1>
          <p className="az-fed-lede">
            AZOA separates workflow orchestration from downstream adapters: durable graphs handle retries, consent, version pinning, and pause-and-resume state while clearing partners stay swappable behind narrow interfaces.
          </p>
        </div>
      </header>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">01 — THE ACTION GRAPH</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">Steps that wait, resume, and prove their state.</h2>
            <p>A payment API call can tell you something succeeded. It cannot explain why an approval paused for three days, who authorized the release, or which compensating step runs if step four drops mid-flight.</p>
            <p>AZOA compiles workflows into directed acyclic action graphs with explicit lifecycle positions: <code>Pending</code>, <code>Running</code>, non-terminal waits (<code>Suspended</code>, <code>AwaitingSignal</code>, <code>AwaitingTimer</code>, <code>AwaitingReconciliation</code>), and terminal states (<code>Succeeded</code>, <code>Failed</code>, <code>Forked</code>, <code>Cancelled</code>).</p>
            <svg className="az-arch-diagram az-arch-diagram--desktop" viewBox="0 0 720 120" fill="none" aria-label="Action graph: Consent to Hold to Gate to Settle">
              <defs>
                <marker id="az-arr" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto">
                  <path d="M 0 1 L 10 5 L 0 9 z" fill="#A63D15" />
                </marker>
              </defs>
              <line x1="128" y1="60" x2="188" y2="60" stroke="#A63D15" strokeWidth="1.5" markerEnd="url(#az-arr)" />
              <line x1="308" y1="60" x2="368" y2="60" stroke="#A63D15" strokeWidth="1.5" markerEnd="url(#az-arr)" />
              <line x1="488" y1="60" x2="548" y2="60" stroke="#A63D15" strokeWidth="1.5" markerEnd="url(#az-arr)" />
              <rect x="8" y="34" width="120" height="52" stroke="#16120D" fill="#F2EDE3" />
              <text x="68" y="65" textAnchor="middle" fill="#16120D" fontFamily="IBM Plex Mono, monospace" fontSize="12">CONSENT</text>
              <rect x="188" y="34" width="120" height="52" stroke="#16120D" fill="#F2EDE3" />
              <text x="248" y="65" textAnchor="middle" fill="#16120D" fontFamily="IBM Plex Mono, monospace" fontSize="12">HOLD</text>
              <rect x="368" y="34" width="120" height="52" stroke="#A63D15" fill="#F2EDE3" />
              <text x="428" y="65" textAnchor="middle" fill="#A63D15" fontFamily="IBM Plex Mono, monospace" fontSize="12">GATE</text>
              <rect x="548" y="34" width="120" height="52" stroke="#16120D" fill="#16120D" />
              <text x="608" y="65" textAnchor="middle" fill="#F2EDE3" fontFamily="IBM Plex Mono, monospace" fontSize="12">SETTLE</text>
            </svg>
            <svg className="az-arch-diagram az-arch-diagram--mobile" viewBox="0 0 280 340" fill="none" aria-label="Action graph: Consent to Hold to Gate to Settle">
              <defs>
                <marker id="az-arr-m" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto">
                  <path d="M 0 1 L 10 5 L 0 9 z" fill="#A63D15" />
                </marker>
              </defs>
              <line x1="140" y1="60" x2="140" y2="96" stroke="#A63D15" strokeWidth="1.5" markerEnd="url(#az-arr-m)" />
              <line x1="140" y1="148" x2="140" y2="184" stroke="#A63D15" strokeWidth="1.5" markerEnd="url(#az-arr-m)" />
              <line x1="140" y1="236" x2="140" y2="272" stroke="#A63D15" strokeWidth="1.5" markerEnd="url(#az-arr-m)" />
              <rect x="40" y="8" width="200" height="52" stroke="#16120D" fill="#F2EDE3" />
              <text x="140" y="39" textAnchor="middle" fill="#16120D" fontFamily="IBM Plex Mono, monospace" fontSize="13">CONSENT</text>
              <rect x="40" y="96" width="200" height="52" stroke="#16120D" fill="#F2EDE3" />
              <text x="140" y="127" textAnchor="middle" fill="#16120D" fontFamily="IBM Plex Mono, monospace" fontSize="13">HOLD</text>
              <rect x="40" y="184" width="200" height="52" stroke="#A63D15" fill="#F2EDE3" />
              <text x="140" y="215" textAnchor="middle" fill="#A63D15" fontFamily="IBM Plex Mono, monospace" fontSize="13">GATE</text>
              <rect x="40" y="272" width="200" height="52" stroke="#16120D" fill="#16120D" />
              <text x="140" y="303" textAnchor="middle" fill="#F2EDE3" fontFamily="IBM Plex Mono, monospace" fontSize="13">SETTLE</text>
            </svg>
          </div>
        </div>
      </section>

      <section className="az-arch-section az-arch-section--dark">
        <div className="az-arch-grid">
          <div className="az-kicker">02 — FOUR SAFETY PROPERTIES</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">Stop cleanly when the wire goes silent.</h2>
            <p><strong style={{ color: '#F2EDE3' }}>Version-pinned start.</strong> Before a caller approves a run, <code>WorkflowClient.previewRun</code> returns the action&apos;s published hash and disclosure manifest. Passing <code>expectedPublishedHash</code> rejects the start if an operator mutates the graph concurrently.</p>
            <p><strong style={{ color: '#F2EDE3' }}>Explicit idempotency keys.</strong> Every run initiation, step advance, and signal carries an <code>Idempotency-Key</code> header so lost HTTP responses return the initial execution state rather than re-running side effects.</p>
            <p><strong style={{ color: '#F2EDE3' }}>Indeterminate freeze.</strong> Ambiguous external responses never trigger blind timer retries. The run parks in <code>AwaitingReconciliation</code> until an operator or worker sweep checks real state at the destination.</p>
            <p><strong style={{ color: '#F2EDE3' }}>Fail-closed controls.</strong> Missing disclosures, unverified authorities, or withdrawn consent halt the graph immediately instead of logging a quiet warning and moving forward.</p>
          </div>
        </div>
      </section>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">03 — IDENTITY &amp; CONSENT</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">Scoped credentials with one-step revocation.</h2>
            <p>Delegated multi-party workflows issue short-lived, scope-bounded credentials tied to explicit consent records. Any participant can withdraw future consent cleanly without corrupting historical audit logs of work already executed.</p>
          </div>
        </div>
      </section>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">04 — SWAPPABLE ADAPTERS</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">One seam between orchestration and settlement.</h2>
            <p>Your action graphs describe obligations, gates, timers, and compensation once. Underneath, thin adapters bind those steps to payment gateways, accounting ledgers, or external clearing partners. Self-hosted deployments swap or enable live adapters explicitly in configuration; the hosted demo runs with live bridging disabled.</p>
          </div>
        </div>
      </section>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">05 — ARDANOVA PILOT</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">Workspaces built on AZOA graphs.</h2>
            <p><a href="https://ardanova.com">ArdaNova</a> is a companion collaboration platform piloting AZOA workflows for milestone reviews, contributor permissions, and simulated project economies — showing how domain apps stay focused on teams while AZOA guarantees state integrity underneath.</p>
          </div>
        </div>
      </section>

      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">06 — OPERATOR CONSOLE</div>
          <div className="az-arch-body">
            <h2 className="az-arch-h2">Built for inspection and audit sweeps.</h2>
            <p>Sign in to trace action templates, run graphs, and reconciliation sweeps inside the console, or inspect the API controllers, SurrealDB schemas, and TypeScript SDK directly on GitHub.</p>
            <WorkflowFunnelGate context="architecture" />
          </div>
        </div>
      </section>

      <section className="az-cta az-cta--plain">
        <div className="az-cta-kicker">APACHE-2.0</div>
        <h2>Audit the source.</h2>
        <p>Every endpoint, schema, and workflow primitive described above lives in the open repository.</p>
        <div className="az-cta-row">
          <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--dark az-btn--lg">VIEW ON GITHUB ↗</a>
          <Link href="/workflows" className="az-btn az-btn--outline az-btn--lg">WORKFLOW PATTERNS →</Link>
        </div>
      </section>

      <LandingFooter />
    </div>
  )
}
