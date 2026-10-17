import type { WorkflowReconciliationSweep } from 'azoa-sdk'

/** Tone of an operator outcome: only `ok` may be shown as success. See `src/lib/AGENTS.md` §operator-honesty. */
export type OutcomeTone = 'ok' | 'warn' | 'bad'

export interface OutcomeVerdict {
  tone: OutcomeTone
  title: string
  detail: string
}

const plural = (count: number, noun: string) => `${count} ${noun}${count === 1 ? '' : 's'}`

/** What one reconcile sweep established, never claiming a success it did not prove. */
export function describeSweep(sweep: WorkflowReconciliationSweep): OutcomeVerdict {
  const stillParked = sweep.results.filter(r => r.status === 'AwaitingReconciliation').length
  if (sweep.errors > 0) {
    return {
      tone: 'bad',
      title: `Re-probe hit ${plural(sweep.errors, 'error')} — ${plural(sweep.scanned, 'run')} scanned`,
      detail: 'Runs that errored stay parked untouched. Check the node logs before sweeping again; nothing was re-sent.',
    }
  }
  if (sweep.scanned === 0) {
    return {
      tone: 'warn',
      title: 'Nothing was waiting — 0 runs scanned',
      detail: 'The sweep found no parked run to check. That is not a confirmation of anything.',
    }
  }
  if (stillParked > 0) {
    return {
      tone: 'warn',
      title: `Re-probe finished — ${sweep.scanned} scanned, ${stillParked} still waiting`,
      detail: 'The settlement outcome of the waiting runs could not be established, so nothing was re-sent.',
    }
  }
  return {
    tone: 'ok',
    title: `Re-probe finished — ${sweep.scanned} scanned, all resolved`,
    detail: 'Every scanned run had a definite settlement outcome and has resumed.',
  }
}
