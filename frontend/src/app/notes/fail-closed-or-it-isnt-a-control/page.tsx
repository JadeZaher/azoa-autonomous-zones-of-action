import type { Metadata } from 'next'
import { LegalDocument } from '@/components/landing/legal'

export const metadata: Metadata = {
  title: "Fail closed, or it isn't a control — AZOA",
  description:
    'A control that fails open is a decoration. AZOA is built so that anything it cannot verify is refused, not waved through. This public site is a simulation, and that sentence is part of the control, not a disclaimer under it.',
}

const SOURCES = [
  { label: 'AZOA homepage', href: 'https://azoa.ardanova.com' },
  { label: 'AZOA architecture', href: 'https://azoa.ardanova.com/architecture' },
  { label: 'AZOA terms', href: 'https://azoa.ardanova.com/terms' },
  { label: 'AZOA API health', href: 'https://azoa-api-production.up.railway.app/health' },
  { label: 'PrimusNeo', href: 'https://primusneo.com' },
  { label: 'ArdaNova', href: 'https://ardanova.com' },
]

export default function FailClosedPage() {
  return (
    <LegalDocument
      eyebrow="9 NOVEMBER 2026"
      title="Fail closed, or it isn't a control"
      lede="A control that fails open is a decoration. AZOA is built so that anything it cannot verify is refused, not waved through. This public site is a simulation, and that sentence is part of the control, not a disclaimer under it."
    >
      <section className="az-arch-section">
        <div className="az-arch-grid">
          <div className="az-kicker">NOTE</div>
          <div className="az-arch-body">
            <p>
              Most systems would rather succeed than be right. When a check cannot be completed — a document is missing, a confirmation has not arrived, a partner has not answered — the convenient path is to let the step through and to log a warning for someone who will not read it. The work continues. The report stays green. What you have, at that point, is not a control. It is a decoration that knows a prayer.
            </p>
            <p>
              Fail closed is the opposite habit, and it is already on the AZOA homepage as a construction rule: anything that cannot be verified is refused, not waved through. The refusal is the result. It is not a temporary embarrassment on the way to a later yes. A graph of action that cannot park, and cannot say no, is just a to-do list with nicer typography.
            </p>
            <p>
              AZOA is an open-source engine for that kind of graph, offered as a layer in the <a href="https://primusneo.com">PrimusNeo</a> stack: identity a person actually holds, consent that can be seen and revoked, steps that wait and resume, a single seam that can name a network or a fiat partner. It is not a consumer brand, and this hosted site is not a bank. Fiat is designed, in the architecture, as a thin callback after a partner has already cleared funds in that partner&apos;s own checkout. That callback is not enabled here. There is no live fiat bridge. Identity checks for moving real money are not enabled. The health check and the terms agree, which is the least a pre-launch engine can do.
            </p>
            <p>
              The honesty is itself a fail-closed move. A page that describes payroll, grants, and supplier payments, and does not say they are simulated, has already failed open — not in the ledger, but in the reader&apos;s head. Someone will send funds, or expect them. The terms page is already blunt: do not send money to this site, and do not expect a payment from it. That sentence should be treated as part of the product, the way a refused step is part of the graph.
            </p>
            <p>
              Federation, on the public site, is a sketch of separate zones that could later exchange records. It is not running. <a href="https://ardanova.com">ArdaNova</a> is a planned pilot, a separate workspace whose economy is still simulated. This site does not pay creators or bridge fiat for it. Those are not delays to be softened into &quot;coming soon&quot; with a brighter chip. They are the current shape of the system. A control that has to be described as live in order to be taken seriously is already asking to be believed, which is the one thing a fail-closed engine should not need.
            </p>
            <p>
              What you can do today is narrower. You can read the architecture. You can look at a demo graph that is labelled a demo graph. You can clone the Apache-2.0 source and run it yourself. You can refuse to treat a simulation as a settlement. If that feels like too little for a page that still says &quot;one engine, every kind of economy,&quot; the jacket is doing its job only if the next sentence is still in view: this public site is a simulation. Real-value bridging is disabled. Fiat settlement is not enabled yet.
            </p>
            <p>
              A warning in a log is not a control. A sentence in a footer is not a control either, unless the rest of the site is willing to stop at it. AZOA&apos;s job, when the dangerous path is eventually opened, will be to stop. Until then, the honest version of the engine is the one that already does.
            </p>
            <h2 className="az-arch-h2">Sources</h2>
            <ul>
              {SOURCES.map((source) => (
                <li key={source.href}><a href={source.href}>{source.label}</a></li>
              ))}
            </ul>
          </div>
        </div>
      </section>
    </LegalDocument>
  )
}
