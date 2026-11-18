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
    kicker: 'BUILD WITH AZOA',
    title: 'Use one engine across every kind of economy.',
    primaryHref: '/architecture',
    primaryLabel: 'FULL ARCHITECTURE →',
    secondaryHref: '/workflows',
    secondaryLabel: 'ALL GUIDES →',
    pilotAction: 'open the workspace',
  },
  'workflows-index': {
    kicker: 'BUILD WITH AZOA',
    title: 'See how the five primitives fit together.',
    primaryHref: '/architecture',
    primaryLabel: 'FULL ARCHITECTURE →',
    secondaryHref: '/login',
    secondaryLabel: 'LOGIN →',
    pilotAction: 'try the workspace',
  },
  architecture: {
    kicker: 'EXPLORE FURTHER',
    title: 'See what people build with AZOA.',
    primaryHref: '/workflows',
    primaryLabel: 'EXPLORE USE CASES →',
    secondaryHref: '/login',
    secondaryLabel: 'LOGIN →',
    pilotAction: 'explore the workspace',
  },
  blog: {
    kicker: 'BUILD WITH AZOA',
    title: 'Run it, audit it, and own it end to end.',
    primaryHref: '/workflows',
    primaryLabel: 'EXPLORE USE CASES →',
    secondaryHref: '/architecture',
    secondaryLabel: 'ARCHITECTURE →',
    pilotAction: 'open the workspace',
  },
}

/** Shared call-to-action panel for AZOA public pages. */
export function WorkflowFunnelGate({ context = 'workflow' }: { context?: GateContext }) {
  const copy = GATE_COPY[context]
  return (
    <aside className="az-gate" aria-label="Next steps">
      <div className="az-kicker">{copy.kicker}</div>
      <h2 className="az-gate-title">{copy.title}</h2>
      <p className="az-gate-body">
        Self-host the open-source engine, {copy.pilotAction} through{' '}
        <Link href="/login">account sign-in</Link>, or see how shared creative work runs in the{' '}
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
