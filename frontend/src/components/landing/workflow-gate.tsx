import Link from 'next/link'
import { GITHUB } from '@/components/landing/chrome'

export type GateContext = 'workflow' | 'workflows-index' | 'architecture' | 'blog'

const GATE_COPY: Record<
  GateContext,
  {
    kicker: string
    title: string
    primaryHref: string
    primaryLabel: string
    secondaryHref: string
    secondaryLabel: string
    pilotAction: string
  }
> = {
  workflow: {
    kicker: 'NEXT IN THE STACK',
    title: 'Put the action graph in your own stack.',
    primaryHref: '/architecture',
    primaryLabel: 'READ ARCHITECTURE →',
    secondaryHref: '/workflows',
    secondaryLabel: 'ALL WORKFLOWS →',
    pilotAction: 'inspect the console',
  },
  'workflows-index': {
    kicker: 'BUILD WITH AZOA',
    title: 'Trace how the engine executes each guarantee.',
    primaryHref: '/architecture',
    primaryLabel: 'READ ARCHITECTURE →',
    secondaryHref: '/login',
    secondaryLabel: 'OPEN CONSOLE →',
    pilotAction: 'explore the interactive sandbox',
  },
  architecture: {
    kicker: 'PRACTICAL PATTERNS',
    title: 'Read the workflow field notes or clone the source.',
    primaryHref: '/workflows',
    primaryLabel: 'WORKFLOW PATTERNS →',
    secondaryHref: '/login',
    secondaryLabel: 'OPEN CONSOLE →',
    pilotAction: 'walk the live workflow patterns',
  },
  blog: {
    kicker: 'FROM ESSAYS TO CODE',
    title: 'Explore how AZOA enforces every guard in practice.',
    primaryHref: '/workflows',
    primaryLabel: 'WORKFLOW PATTERNS →',
    secondaryHref: '/architecture',
    secondaryLabel: 'ARCHITECTURE →',
    pilotAction: 'open the interactive console',
  },
}

/** Shared top-of-funnel call-to-action panel for AZOA public engineering pages. */
export function WorkflowFunnelGate({ context = 'workflow' }: { context?: GateContext }) {
  const copy = GATE_COPY[context]
  return (
    <aside className="az-gate" aria-label="Next steps">
      <div className="az-kicker">{copy.kicker}</div>
      <h2 className="az-gate-title">{copy.title}</h2>
      <p className="az-gate-body">
        Clone the Apache-2.0 engine and SDK, {copy.pilotAction} via{' '}
        <Link href="/login">workspace sign-in</Link>, or see multi-party operations modelled in the{' '}
        <a href="https://ardanova.com" target="_blank" rel="noopener noreferrer">
          ArdaNova
        </a>{' '}
        pilot.
      </p>
      <div className="az-gate-actions">
        <Link href={copy.primaryHref} className="az-btn az-btn--dark az-btn--sm">
          {copy.primaryLabel}
        </Link>
        <Link href={copy.secondaryHref} className="az-btn az-btn--outline az-btn--sm">
          {copy.secondaryLabel}
        </Link>
        <a
          href={GITHUB}
          target="_blank"
          rel="noopener noreferrer"
          className="az-btn az-btn--outline az-btn--sm"
        >
          GITHUB ↗
        </a>
      </div>
    </aside>
  )
}
