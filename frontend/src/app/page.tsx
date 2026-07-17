'use client'

import Link from 'next/link'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { HeroDag } from '@/components/landing/hero-dag'

// AZOA marketing landing page — imported from the Claude Design project
// "AZOA — Autonomous Zones of Action" (Home.dc.html). The x-dc custom
// elements and style-hover attributes are converted to React + a scoped
// stylesheet. See src/app/AGENTS.md §landing for the conversion notes.

const GITHUB = 'https://github.com/JadeZaher/azoa-autonomous-zones-of-action'

const STYLES = `
.azoa-landing{font-family:'Space Grotesk',sans-serif;color:#16120D;background:#F2EDE3;min-height:100vh;}
.azoa-landing *{box-sizing:border-box;}
.azoa-landing a{color:inherit;text-decoration:none;}
.azoa-landing ::selection{background:#C8501E;color:#F2EDE3;}
.azoa-landing .mono{font-family:'IBM Plex Mono',monospace;}

.az-nav{position:fixed;top:0;left:0;right:0;z-index:20;display:flex;align-items:center;justify-content:space-between;padding:0 32px;height:60px;border-bottom:1px solid #16120D;background:#F2EDE3;}
.az-brand{font-weight:700;font-size:18px;letter-spacing:0.08em;}
.az-nav-right{display:flex;align-items:center;gap:28px;font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.06em;}
.az-link{transition:color .15s;}
.az-link:hover{color:#C8501E;}

.az-btn{display:inline-flex;align-items:center;font-family:'IBM Plex Mono',monospace;letter-spacing:0.08em;transition:background .15s,color .15s,border-color .15s;cursor:pointer;white-space:nowrap;}
.az-btn--sm{font-size:13px;padding:8px 16px;}
.az-btn--md{font-size:13px;padding:14px 24px;}
.az-btn--lg{font-size:14px;padding:16px 32px;}
.az-btn--dark{border:1px solid #16120D;background:#16120D;color:#F2EDE3;}
.az-btn--dark:hover{background:#C8501E;border-color:#C8501E;color:#F2EDE3;}
.az-btn--outline{border:1px solid #16120D;color:#16120D;background:transparent;}
.az-btn--outline:hover{background:#16120D;color:#F2EDE3;}
.az-btn--outline-inv{border:1px solid #F2EDE3;color:#F2EDE3;background:transparent;}
.az-btn--outline-inv:hover{background:#F2EDE3;color:#16120D;}

.az-hero{position:relative;height:100vh;min-height:680px;overflow:hidden;}
.az-hero-fade{position:absolute;inset:0;background:linear-gradient(to top,rgba(242,237,227,0.92) 0%,rgba(242,237,227,0.25) 45%,rgba(242,237,227,0) 70%);pointer-events:none;}
.az-hero-content{position:absolute;left:32px;right:32px;bottom:56px;pointer-events:none;}
.az-eyebrow{font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.18em;color:#C8501E;margin-bottom:20px;}
.az-h1{margin:0;font-size:clamp(56px,8.5vw,140px);line-height:0.94;font-weight:700;letter-spacing:-0.03em;text-transform:uppercase;max-width:1200px;}
.az-hero-row{display:flex;flex-wrap:wrap;align-items:flex-end;justify-content:space-between;gap:24px;margin-top:32px;}
.az-hero-lede{margin:0;font-size:clamp(17px,1.5vw,21px);line-height:1.45;max-width:560px;color:#3d362c;text-wrap:pretty;}
.az-hero-cta{display:flex;gap:12px;pointer-events:auto;flex-wrap:wrap;}
.az-hero-badge{position:absolute;top:76px;right:32px;font-family:'IBM Plex Mono',monospace;font-size:12px;letter-spacing:0.1em;color:#6f665a;display:flex;align-items:center;gap:8px;}
.az-dot{width:8px;height:8px;border-radius:50%;background:#C8501E;display:inline-block;}

.az-ticker{border-top:1px solid #16120D;border-bottom:1px solid #16120D;padding:14px 32px;font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.14em;color:#6f665a;display:flex;justify-content:space-between;flex-wrap:wrap;gap:12px;}

.az-wrap{max-width:1400px;margin:0 auto;}
.az-shead{display:grid;grid-template-columns:minmax(180px,1fr) 3fr;gap:32px;}
.az-kicker{font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.14em;color:#C8501E;}
.az-h2{margin:0;font-size:clamp(36px,4.5vw,68px);line-height:1.02;font-weight:700;letter-spacing:-0.025em;text-transform:uppercase;}

#domains{padding:110px 32px 0;}
.az-domains{max-width:1400px;margin:48px auto 0;border-top:1px solid #16120D;}
.az-domain{display:grid;grid-template-columns:64px minmax(220px,1fr) 2fr;gap:32px;align-items:center;padding:36px 0;border-bottom:1px solid #d6ccba;}
.az-domain:last-child{border-bottom:1px solid #16120D;}
.az-domain h3{margin:0;font-size:clamp(24px,2.4vw,34px);font-weight:700;letter-spacing:-0.02em;}
.az-domain p{margin:0;font-size:17px;line-height:1.55;color:#3d362c;text-wrap:pretty;}
.az-domains-note{max-width:1400px;margin:0 auto;padding:56px 0 110px;}
.az-domains-note p{margin:0;font-size:clamp(22px,2.6vw,36px);line-height:1.3;font-weight:500;letter-spacing:-0.015em;max-width:1000px;text-wrap:pretty;}
.az-accent{color:#C8501E;}

.az-dark{border-top:1px solid #16120D;background:#16120D;color:#F2EDE3;padding:110px 32px;}
.az-dark .az-kicker{color:#E08A5B;}
.az-prim-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:1px;background:#3d362c;border:1px solid #3d362c;margin-top:64px;}
.az-prim{background:#16120D;padding:32px 24px;display:flex;flex-direction:column;gap:14px;min-height:220px;}
.az-prim-tag{font-family:'IBM Plex Mono',monospace;font-size:12px;color:#E08A5B;letter-spacing:0.12em;}
.az-prim-title{font-size:22px;font-weight:700;letter-spacing:-0.01em;}
.az-prim p{margin:0;font-size:15px;line-height:1.55;color:#b7ad9c;text-wrap:pretty;}
.az-prim-foot{margin-top:40px;display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:16px;}
.az-prim-foot p{margin:0;font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.08em;color:#b7ad9c;}

.az-trust{padding:110px 32px;border-bottom:1px solid #16120D;}
.az-trust-grid{max-width:1400px;margin:0 auto;display:grid;grid-template-columns:minmax(180px,1fr) 3fr;gap:32px;}
.az-trust h2{margin:0 0 40px;font-size:clamp(36px,4.5vw,68px);line-height:1.02;font-weight:700;letter-spacing:-0.025em;text-transform:uppercase;}
.az-trust-items{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:40px 56px;max-width:1000px;}
.az-trust-item{display:flex;flex-direction:column;gap:10px;}
.az-trust-item .lbl{font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.1em;color:#C8501E;}
.az-trust-item p{margin:0;font-size:16px;line-height:1.55;color:#3d362c;text-wrap:pretty;}

#next{padding:110px 32px;}
.az-next-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:24px;margin-top:56px;}
.az-next-card{border:1px solid #16120D;padding:48px 36px;display:flex;flex-direction:column;gap:18px;min-height:300px;transition:background .15s,color .15s,border-color .15s;}
.az-next-card--outline:hover{background:#e9e2d3;}
.az-next-card--dark{background:#16120D;color:#F2EDE3;}
.az-next-card--dark:hover{background:#C8501E;border-color:#C8501E;color:#F2EDE3;}
.az-next-tag{font-family:'IBM Plex Mono',monospace;font-size:12px;letter-spacing:0.14em;color:#C8501E;}
.az-next-card--dark .az-next-tag{color:#E08A5B;}
.az-next-title{font-size:clamp(32px,3.4vw,52px);font-weight:700;letter-spacing:-0.02em;text-transform:uppercase;line-height:1;}
.az-next-card p{margin:0;font-size:17px;line-height:1.55;color:#3d362c;max-width:480px;text-wrap:pretty;}
.az-next-card--dark p{color:#F2EDE3;opacity:0.8;}
.az-next-more{margin-top:auto;font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.08em;}

.az-cta{border-top:1px solid #16120D;padding:130px 32px;text-align:center;}
.az-cta-kicker{font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.16em;color:#C8501E;margin-bottom:24px;}
.az-cta h2{margin:0 auto;font-size:clamp(44px,6.5vw,104px);line-height:0.98;font-weight:700;letter-spacing:-0.03em;text-transform:uppercase;max-width:1100px;}
.az-cta p{margin:28px auto 0;font-size:18px;line-height:1.5;color:#3d362c;max-width:520px;text-wrap:pretty;}
.az-cta-row{display:flex;justify-content:center;gap:12px;margin-top:40px;flex-wrap:wrap;}

.az-footer{border-top:1px solid #16120D;padding:24px 32px;display:flex;justify-content:space-between;flex-wrap:wrap;gap:16px;font-family:'IBM Plex Mono',monospace;font-size:12px;letter-spacing:0.08em;color:#6f665a;}
.az-footer-links{display:flex;gap:28px;}
.az-flink{color:#6f665a;transition:color .15s;}
.az-flink:hover{color:#C8501E;}

@media(max-width:720px){
  .az-nav{padding:0 18px;}
  .az-nav-right{gap:14px;}
  .az-hide-sm{display:none;}
  .az-hero-content{left:18px;right:18px;}
  .az-hero-badge{right:18px;}
  .az-shead,.az-trust-grid{grid-template-columns:1fr;}
  .az-domain{grid-template-columns:1fr;gap:14px;}
  #domains,.az-dark,.az-trust,#next,.az-cta{padding-left:18px;padding-right:18px;}
  .az-ticker{padding-left:18px;padding-right:18px;}
}
`

const DOMAINS = [
  {
    title: 'Financial workflows',
    body: 'Payouts, escrows, grants, settlements. Processes that branch on conditions, wait for the real world, and never lose a step or spend one twice.',
    icon: (
      <svg viewBox="0 0 48 48" width="48" height="48">
        <path d="M6 36 L20 20 L30 28 L42 10" fill="none" stroke="#16120D" strokeWidth="2" />
        <circle cx="42" cy="10" r="4" fill="#C8501E" />
      </svg>
    ),
  },
  {
    title: 'Circular economies',
    body: 'Materials, credits, and obligations that loop. Tracked from first issue to final return — so what goes around genuinely comes around.',
    icon: (
      <svg viewBox="0 0 48 48" width="48" height="48">
        <circle cx="24" cy="24" r="16" fill="none" stroke="#16120D" strokeWidth="2" />
        <path d="M40 24 L44 19 M40 24 L35 20" fill="none" stroke="#C8501E" strokeWidth="2" />
      </svg>
    ),
  },
  {
    title: 'Operational processes',
    body: 'Approvals, procurement, logistics — the everyday choreography of an organization, made durable and auditable instead of ad-hoc.',
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
    title: 'Quests in games',
    body: 'Player journeys with real stakes — earn, trade, unlock. The same engine that moves money moves play, without a separate stack.',
    icon: (
      <svg viewBox="0 0 48 48" width="48" height="48">
        <path d="M24 6 L42 24 L24 42 L6 24 Z" fill="none" stroke="#16120D" strokeWidth="2" />
        <circle cx="24" cy="24" r="5" fill="#C8501E" />
      </svg>
    ),
  },
]

const PRIMITIVES = [
  { tag: 'IDENTITY', title: 'An avatar you actually own', body: 'Apps act for you only with consent you can see — and revoke — at any moment.' },
  { tag: 'QUESTS', title: 'Durable graphs of action', body: 'They wait, resume, and reconcile against reality before acting. They never double-spend.' },
  { tag: 'HOLONS', title: 'Value as nested wholes', body: 'Tokens, credits, rights, things — anything that can be held, split, and moved.' },
  { tag: 'RAILS', title: 'Chains and fiat, one seam', body: "A step doesn't care where it settles. Blockchains and banks sit behind the same door." },
  { tag: 'STAR', title: 'Whole apps, generated', body: 'New products are scaffolded on top of identity and quests. Nobody rebuilds the plumbing.' },
]

const TRUST = [
  { label: 'EXACTLY ONCE', body: 'A retried or duplicated step settles once, or not at all. Never twice.' },
  { label: 'FAIL CLOSED', body: "Anything that can't be verified is refused — not waved through." },
  { label: 'RECONCILE, NEVER GUESS', body: 'The engine re-checks the real world before every irreversible move.' },
  { label: 'ESCALATE TO A HUMAN', body: 'Genuinely ambiguous decisions are surfaced to an operator — never faked.' },
]

export default function Home() {
  const { isAuthenticated, loading } = useAzoaAuth()
  const entered = !loading && isAuthenticated
  const enterHref = entered ? '/overview' : '/login'
  const enterLabel = entered ? 'DASHBOARD →' : 'LOGIN →'

  return (
    <div className="azoa-landing">
      <style dangerouslySetInnerHTML={{ __html: STYLES }} />
      {/* eslint-disable-next-line @next/next/no-page-custom-font */}
      <link rel="preconnect" href="https://fonts.googleapis.com" />
      {/* eslint-disable-next-line @next/next/no-page-custom-font */}
      <link
        href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@400;500;700&family=IBM+Plex+Mono:wght@400;500&display=swap"
        rel="stylesheet"
      />

      <nav className="az-nav">
        <Link href="/" className="az-brand">AZOA</Link>
        <div className="az-nav-right">
          <a href="#primitives" className="az-link az-hide-sm">ARCHITECTURE</a>
          <a href="#next" className="az-link az-hide-sm">FEDERATION</a>
          <a href="#next" className="az-link az-hide-sm">ARDANOVA</a>
          <Link href={enterHref} className="az-btn az-btn--outline az-btn--sm">{enterLabel}</Link>
          <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--dark az-btn--sm">GITHUB ↗</a>
        </div>
      </nav>

      <header className="az-hero">
        <HeroDag ink="#16120D" accent="#C8501E" speed={1} density={1} />
        <div className="az-hero-fade" />
        <div className="az-hero-content">
          <div className="az-eyebrow">AUTONOMOUS ZONES OF ACTION</div>
          <h1 className="az-h1">One engine.<br />Every kind of&nbsp;economy.</h1>
          <div className="az-hero-row">
            <p className="az-hero-lede">
              AZOA models financial workflows, circular economies, operational processes — even quests in games — as living graphs of action. Built from the same primitives. Interoperable by default.
            </p>
            <div className="az-hero-cta">
              <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--dark az-btn--md">VIEW ON GITHUB ↗</a>
              <a href="#domains" className="az-btn az-btn--outline az-btn--md">HOW IT WORKS ↓</a>
            </div>
          </div>
        </div>
        <div className="az-hero-badge">
          <span className="az-dot" />
          LIVE — AGENTS EXECUTING A QUEST GRAPH
        </div>
      </header>

      <div className="az-ticker">
        <span>FINANCE</span><span>/</span><span>CIRCULARITY</span><span>/</span><span>OPERATIONS</span><span>/</span><span>GOVERNANCE</span><span>/</span><span>PLAY</span>
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
            Each of these is a graph of steps over the same primitives. Which means <span className="az-accent">a game quest can pay a supplier</span>. A circular credit can settle a payroll. Anything can talk to anything.
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
            <p>MULTICHAIN · MULTI-WALLET · FIAT-BRIDGED · SELF-HOSTED</p>
            <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--outline-inv az-btn--md">FULL ARCHITECTURE →</a>
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
            <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-next-card az-next-card--outline">
              <div className="az-next-tag">ON ITS WAY</div>
              <div className="az-next-title">Federation</div>
              <p>Zones that govern themselves — and still talk to each other. Distributed governance means freedom, privacy, and agency.</p>
              <div className="az-next-more">READ THE VISION →</div>
            </a>
            <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-next-card az-next-card--dark">
              <div className="az-next-tag">COMING SOON — FIRST PILOT</div>
              <div className="az-next-title">Ardanova</div>
              <p>The new creator economy. A fair deal for the digital age — the first zone built on AZOA.</p>
              <div className="az-next-more">MEET THE PILOT →</div>
            </a>
          </div>
        </div>
      </section>

      <section className="az-cta">
        <div className="az-cta-kicker">APACHE-2.0 · OPEN SOURCE</div>
        <h2>Run it. Audit it.<br />Own it.</h2>
        <p>AZOA is meant to be self-hosted and extended. The engine is yours, end to end.</p>
        <div className="az-cta-row">
          <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--dark az-btn--lg">VIEW ON GITHUB ↗</a>
          <Link href={enterHref} className="az-btn az-btn--outline az-btn--lg">{enterLabel}</Link>
        </div>
      </section>

      <footer className="az-footer">
        <span>AZOA — AUTONOMOUS ZONES OF ACTION</span>
        <div className="az-footer-links">
          <a href="#primitives" className="az-flink">ARCHITECTURE</a>
          <a href="#next" className="az-flink">FEDERATION</a>
          <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-flink">GITHUB ↗</a>
        </div>
      </footer>
    </div>
  )
}
