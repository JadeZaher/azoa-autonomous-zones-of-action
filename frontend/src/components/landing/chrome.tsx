'use client'

import Link from 'next/link'
import { useAzoaAuth } from '@/lib/azoa-auth'

// Shared AZOA marketing chrome — styles, fonts, nav, footer — used by the
// landing (/) and Federation (/federation) routes. Imported from the Claude
// Design "Autonomous Zones of Action" project. See src/app/AGENTS.md §landing.

export const GITHUB = 'https://github.com/JadeZaher/azoa-autonomous-zones-of-action'

export const LANDING_STYLES = `
.azoa-landing{font-family:'Space Grotesk',sans-serif;color:#16120D;background:#F2EDE3;min-height:100vh;}
.azoa-landing *{box-sizing:border-box;}
.azoa-landing a{text-decoration:none;}
.azoa-landing ::selection{background:#C8501E;color:#F2EDE3;}
.azoa-landing .mono{font-family:'IBM Plex Mono',monospace;}

.az-nav{position:fixed;top:0;left:0;right:0;z-index:20;display:flex;align-items:center;justify-content:space-between;padding:0 32px;height:60px;border-bottom:1px solid #16120D;background:#F2EDE3;}
.az-brand{font-weight:700;font-size:18px;letter-spacing:0.08em;}
.az-nav-right{display:flex;align-items:center;gap:28px;font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.06em;}
.az-link{transition:color .15s;}
.az-link:hover{color:#C8501E;}
.az-link--active{color:#C8501E;}

.az-btn{display:inline-flex;align-items:center;font-family:'IBM Plex Mono',monospace;letter-spacing:0.08em;transition:background .15s,color .15s,border-color .15s;cursor:pointer;white-space:nowrap;text-decoration:none;}
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
.az-cta--plain{border-top:none;}
.az-cta-kicker{font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.16em;color:#C8501E;margin-bottom:24px;}
.az-cta h2{margin:0 auto;font-size:clamp(44px,6.5vw,104px);line-height:0.98;font-weight:700;letter-spacing:-0.03em;text-transform:uppercase;max-width:1100px;}
.az-cta p{margin:28px auto 0;font-size:18px;line-height:1.5;color:#3d362c;max-width:520px;text-wrap:pretty;}
.az-cta-row{display:flex;justify-content:center;gap:12px;margin-top:40px;flex-wrap:wrap;}

.az-footer{border-top:1px solid #16120D;padding:24px 32px;display:flex;justify-content:space-between;flex-wrap:wrap;gap:16px;font-family:'IBM Plex Mono',monospace;font-size:12px;letter-spacing:0.08em;color:#6f665a;}
.az-footer-links{display:flex;gap:28px;}
.az-flink{color:#6f665a;transition:color .15s;}
.az-flink:hover{color:#C8501E;}

/* Federation route */
.az-fed-header{padding:180px 32px 90px;border-bottom:1px solid #16120D;}
.az-fed-lede{margin:32px 0 0;font-size:clamp(17px,1.5vw,21px);line-height:1.5;max-width:640px;color:#3d362c;text-wrap:pretty;}
.az-fed-section{padding:100px 32px;border-bottom:1px solid #16120D;}
.az-fed-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:1px;background:#16120D;border:1px solid #16120D;}
.az-fed-cell{background:#F2EDE3;padding:48px 36px;display:flex;flex-direction:column;gap:16px;}
.az-fed-cell h3{margin:0;font-size:clamp(28px,3vw,44px);font-weight:700;letter-spacing:-0.02em;text-transform:uppercase;}
.az-fed-cell p{margin:0;font-size:16px;line-height:1.6;color:#3d362c;text-wrap:pretty;}
.az-fed-note{margin:56px auto 0;font-size:clamp(22px,2.6vw,36px);line-height:1.3;font-weight:500;letter-spacing:-0.015em;max-width:1000px;text-wrap:pretty;}

.az-ardanova{background:#16120D;color:#F2EDE3;padding:140px 32px;border-bottom:1px solid #16120D;}
.az-ardanova-eyebrow{font-family:'IBM Plex Mono',monospace;font-size:13px;letter-spacing:0.18em;color:#E08A5B;margin-bottom:24px;}
.az-ardanova-title{margin:0;font-size:clamp(56px,9vw,150px);line-height:0.92;font-weight:700;letter-spacing:-0.03em;text-transform:uppercase;}
.az-ardanova-lede{margin:36px 0 0;font-size:clamp(22px,2.6vw,36px);line-height:1.3;font-weight:500;letter-spacing:-0.015em;max-width:900px;text-wrap:pretty;}
.az-ardanova-items{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:40px 56px;margin-top:64px;max-width:1100px;}
.az-ardanova-item{display:flex;flex-direction:column;gap:10px;border-top:1px solid #3d362c;padding-top:20px;}
.az-ardanova-item .lbl{font-family:'IBM Plex Mono',monospace;font-size:12px;letter-spacing:0.12em;color:#E08A5B;}
.az-ardanova-item p{margin:0;font-size:16px;line-height:1.6;color:#b7ad9c;text-wrap:pretty;}

@media(max-width:720px){
  .az-nav{padding:0 18px;}
  .az-nav-right{gap:14px;}
  .az-hide-sm{display:none;}
  .az-hero-content{left:18px;right:18px;}
  .az-hero-badge{right:18px;}
  .az-shead,.az-trust-grid{grid-template-columns:1fr;}
  .az-domain{grid-template-columns:1fr;gap:14px;}
  #domains,.az-dark,.az-trust,#next,.az-cta,.az-fed-header,.az-fed-section,.az-ardanova{padding-left:18px;padding-right:18px;}
  .az-ticker{padding-left:18px;padding-right:18px;}
  .az-fed-header{padding-top:120px;}
}
`

export function LandingHead() {
  return (
    <>
      <style dangerouslySetInnerHTML={{ __html: LANDING_STYLES }} />
      {/* eslint-disable-next-line @next/next/no-page-custom-font */}
      <link rel="preconnect" href="https://fonts.googleapis.com" />
      {/* eslint-disable-next-line @next/next/no-page-custom-font */}
      <link
        href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@400;500;700&family=IBM+Plex+Mono:wght@400;500&display=swap"
        rel="stylesheet"
      />
    </>
  )
}

export function LandingNav({ active }: { active?: 'architecture' | 'federation' }) {
  const { isAuthenticated, loading } = useAzoaAuth()
  const entered = !loading && isAuthenticated
  const enterHref = entered ? '/overview' : '/login'
  const enterLabel = entered ? 'DASHBOARD →' : 'LOGIN →'

  return (
    <nav className="az-nav">
      <Link href="/" className="az-brand">AZOA</Link>
      <div className="az-nav-right">
        <Link href="/#primitives" className={`az-link az-hide-sm${active === 'architecture' ? ' az-link--active' : ''}`}>ARCHITECTURE</Link>
        <Link href="/federation" className={`az-link az-hide-sm${active === 'federation' ? ' az-link--active' : ''}`}>FEDERATION</Link>
        <Link href="/federation#ardanova" className="az-link az-hide-sm">ARDANOVA</Link>
        <Link href={enterHref} className="az-btn az-btn--outline az-btn--sm">{enterLabel}</Link>
        <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-btn az-btn--dark az-btn--sm">GITHUB ↗</a>
      </div>
    </nav>
  )
}

export function LandingFooter() {
  return (
    <footer className="az-footer">
      <span>AZOA — AUTONOMOUS ZONES OF ACTION</span>
      <div className="az-footer-links">
        <Link href="/" className="az-flink">HOME</Link>
        <Link href="/federation" className="az-flink">FEDERATION</Link>
        <a href={GITHUB} target="_blank" rel="noopener noreferrer" className="az-flink">GITHUB ↗</a>
      </div>
    </footer>
  )
}
