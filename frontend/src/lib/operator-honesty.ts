import type { WorkflowReconciliationSweep } from 'azoa-sdk'

/** Tone of an operator outcome: only `ok` may be shown as success. See `src/app/(dashboard)/AGENTS.md` §operator-boards. */
export type OutcomeTone = 'ok' | 'warn' | 'bad'

export interface OutcomeVerdict {
  tone: OutcomeTone
  title: string
  detail: string
}

const plural = (count: number, noun: string) => `${count} ${noun}${count === 1 ? '' : 's'}`

/**
 * What one reconcile sweep established, never claiming a success it did not prove. `parkedAfter` is the
 * number of runs still parked when the board was re-read: the sweep scans a bounded batch, so runs beyond it stay parked.
 */
export function describeSweep(sweep: WorkflowReconciliationSweep, parkedAfter?: number): OutcomeVerdict {
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
  if (parkedAfter !== undefined && parkedAfter > 0) {
    return {
      tone: 'warn',
      title: `Re-probe finished — ${sweep.scanned} scanned and resolved, ${plural(parkedAfter, 'run')} still parked`,
      detail: 'One sweep checks a bounded batch. The runs still parked were not scanned in this pass; sweep again to reach them.',
    }
  }
  return {
    tone: 'ok',
    title: `Re-probe finished — ${sweep.scanned} scanned, all resolved`,
    detail: 'Every scanned run had a definite settlement outcome and has resumed.',
  }
}

/** Toast for an audited cash-out resolution: the status the node returned, never the button's intent. */
export function describePayoutResolution(view: { status: string; statusReason?: string | null }): OutcomeVerdict {
  const reason = view.statusReason ? ` ${view.statusReason}` : ''
  switch (view.status) {
    case 'Paid':
      return { tone: 'ok', title: 'Recorded as paid', detail: `The bank confirmed the payout.${reason}` }
    case 'Returned':
      return { tone: 'ok', title: 'The balance was returned', detail: `The units are back in the person's account.${reason}` }
    case 'BankPending':
      return { tone: 'warn', title: 'Funded, but the bank payout has not landed yet (BankPending)', detail: `The bank-payout lifecycle owns it from here; it is not paid yet.${reason}` }
    default:
      return { tone: 'warn', title: `The cash-out is now ${view.status}`, detail: reason.trim() || 'Check the row before acting again.' }
  }
}
