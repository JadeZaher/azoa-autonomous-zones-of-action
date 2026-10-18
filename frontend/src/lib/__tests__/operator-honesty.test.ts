import { describe, expect, it } from 'vitest'
import type { WorkflowReconciliationResult } from 'azoa-sdk'
import { describePayoutResolution, describeSweep } from '../operator-honesty'

const run = (status: WorkflowReconciliationResult['status']): WorkflowReconciliationResult =>
  ({ runId: crypto.randomUUID(), status, reconciledConfirmed: 0, releasedFailedOnChain: 0, stillIndeterminate: 0 })

describe('describeSweep never reports success it did not prove', () => {
  it.each([
    ['zero scanned', { scanned: 0, results: [], errors: 0, hardStuck: 0 }, 'warn', /0 runs scanned/],
    ['errors win over resolved rows', { scanned: 3, results: [run('Running')], errors: 2, hardStuck: 0 }, 'bad', /2 errors/],
    ['some still parked', { scanned: 2, results: [run('Running'), run('AwaitingReconciliation')], errors: 0, hardStuck: 1 }, 'warn', /1 still waiting/],
    ['every scanned run resolved', { scanned: 2, results: [run('Running'), run('Running')], errors: 0, hardStuck: 0 }, 'ok', /all resolved/],
  ] as const)('%s', (_name, sweep, tone, title) => {
    const verdict = describeSweep({ ...sweep, results: [...sweep.results] })
    expect(verdict.tone).toBe(tone)
    expect(verdict.title).toMatch(title)
  })

  it.each([
    ['nothing left parked after the batch', 0, 'ok', /all resolved/],
    ['unscanned runs still parked after the batch', 4, 'warn', /4 runs still parked/],
  ] as const)('%s', (_name, parkedAfter, tone, title) => {
    const verdict = describeSweep({ scanned: 2, results: [run('Running'), run('Running')], errors: 0, hardStuck: 0 }, parkedAfter)
    expect(verdict.tone).toBe(tone)
    expect(verdict.title).toMatch(title)
  })
})

describe('describePayoutResolution reports the status the node returned', () => {
  it.each([
    ['Paid', 'ok', /Recorded as paid/],
    ['Returned', 'ok', /balance was returned/],
    ['BankPending', 'warn', /has not landed yet/],
    ['NeedsOperator', 'warn', /now NeedsOperator/],
  ] as const)('%s', (status, tone, title) => {
    const verdict = describePayoutResolution({ status, statusReason: null })
    expect(verdict.tone).toBe(tone)
    expect(verdict.title).toMatch(title)
  })
})
