import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import ActionRecoveryPage from './page'

const request = vi.hoisted(() => vi.fn())
vi.mock('@/lib/operator-client', () => ({ operatorRequest: request }))
const row = { executionId: 'execution', runId: 'run', nodeId: 'node', version: 4, state: 'Running',
  recoveryState: 'NeedsOperator', domain: 'local', operationId: 'original-operation', intentHash: 'a'.repeat(64), canRetry: true }

beforeEach(() => {
  request.mockReset()
  request.mockImplementation(async (path: string) => path.includes('?') ? [row] : path.endsWith('/history') ? [] : row)
})

it('retains the exact retry body after an unacknowledged response', async () => {
  let attempts = 0
  request.mockImplementation(async (path: string) => {
    if (path.endsWith('/retry')) {
      attempts++
      if (attempts === 1) throw new Error('Response was lost')
      return { auditId: 'audit', executionId: row.executionId, expectedVersion: 4, requestedAt: 'now' }
    }
    return path.includes('?') ? [row] : path.endsWith('/history') ? [] : row
  })
  render(<ActionRecoveryPage />)
  fireEvent.click(await screen.findByRole('button', { name: 'Inspect saved operation' }))
  fireEvent.change(await screen.findByLabelText('Investigation and reason for retry'), { target: { value: 'Reviewed and restored the original dependency.' } })
  fireEvent.click(screen.getByRole('button', { name: 'Request recovery' }))
  await screen.findByText('Response was lost')
  fireEvent.click(screen.getByRole('button', { name: 'Retry same recovery request' }))
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('audit'))
  const calls = request.mock.calls.filter(call => call[0].endsWith('/retry'))
  expect(calls).toHaveLength(2)
  expect(calls[1][1].body).toEqual(calls[0][1].body)
  expect(calls[0][1].body).toMatchObject({ expectedVersion: 4, intentHash: row.intentHash })
  expect(calls[0][1].body).not.toHaveProperty('operationId')
})

it('shows legacy quarantine without offering an unsupported reset', async () => {
  request.mockImplementation(async (path: string) => path.includes('?') ? [row]
    : path.endsWith('/history') ? [] : { ...row, canRetry: false, intentHash: null, domain: null })
  render(<ActionRecoveryPage />)
  fireEvent.click(await screen.findByRole('button', { name: 'Inspect saved operation' }))
  await screen.findByText(/Its effects remain quarantined/)
  expect(screen.queryByRole('button', { name: 'Request recovery' })).not.toBeInTheDocument()
  expect(request.mock.calls.some(call => call[0].endsWith('/retry'))).toBe(false)
})
