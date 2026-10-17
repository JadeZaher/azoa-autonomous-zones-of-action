import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { WebhookEventsPanel } from './webhook-events'

const api = vi.hoisted(() => ({ listConsentWebhookEvents: vi.fn(), getConsentWebhookEvent: vi.fn(), retryConsentWebhookEvent: vi.fn(), listWebhookEvents: vi.fn(), getWebhookEvent: vi.fn(), retryWebhookEvent: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api } }))
vi.mock('@/lib/operator-client', () => ({ operatorRequest: vi.fn() }))
const id = '11111111-1111-1111-1111-111111111111'
const key = 'azoa:webhook-retry:actor:self'
const row = { eventId: id, eventType: 'completed', status: 'DeadLettered', version: 4, attemptCount: 8, cycleAttemptCount: 8, retryGeneration: 0, attemptInFlight: false, retryable: true }
beforeEach(() => { localStorage.clear(); vi.resetAllMocks(); api.listWebhookEvents.mockResolvedValue({ ok: true, value: [row] }) })

it('does not let an older initial read overwrite a newer refresh', async () => {
  let resolveInitial!: (value: unknown) => void
  api.listWebhookEvents.mockImplementationOnce(() => new Promise(resolve => { resolveInitial = resolve }))
    .mockResolvedValueOnce({ ok: true, value: [{ ...row, version: 5, status: 'Delivered', retryable: false }] })
  render(<WebhookEventsPanel userId="actor" />)
  fireEvent.click(screen.getByRole('button', { name: 'Refresh deliveries' }))
  await screen.findByText('completed: Delivered')
  await act(async () => { resolveInitial({ ok: true, value: [row] }) })
  expect(screen.getByText('completed: Delivered')).toBeTruthy()
  expect(screen.queryByRole('button', { name: 'Retry delivery' })).toBeNull()
})

it('persists before sending and recovers the same retry after response loss', async () => {
  api.retryWebhookEvent.mockImplementationOnce(async (eventId, command) => {
    expect(JSON.parse(localStorage.getItem(key)!)).toEqual({ eventId, command })
    return { ok: false, error: { message: 'Response lost' } }
  }).mockImplementationOnce(async (eventId, command) => ({ ok: true, value: { eventId, requestId: command.requestId, version: 5 } }))
  render(<WebhookEventsPanel userId="actor" />)
  fireEvent.change(screen.getByLabelText('Delivery retry reason'), { target: { value: 'Receiver repaired and verified.' } })
  fireEvent.click(await screen.findByRole('button', { name: 'Retry delivery' }))
  await screen.findByText('Response lost')
  fireEvent.click(screen.getByRole('button', { name: 'Recover saved retry' }))
  await waitFor(() => expect(localStorage.getItem(key)).toBeNull())
  expect(api.retryWebhookEvent.mock.calls[0]).toEqual(api.retryWebhookEvent.mock.calls[1])
})

it('keeps a saved command until an authenticated newer version is observed', async () => {
  const saved = { eventId: id, command: { requestId: id, expectedVersion: 4, reason: 'Receiver repaired and verified.' } }
  localStorage.setItem(key, JSON.stringify(saved))
  api.getWebhookEvent.mockResolvedValueOnce({ ok: true, value: row })
    .mockResolvedValueOnce({ ok: true, value: { ...row, version: 5, status: 'Pending' } })
  render(<WebhookEventsPanel userId="actor" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Reconcile newer event' }))
  await screen.findByText('The event has not advanced beyond this command. Keep it and retry the exact request.')
  expect(JSON.parse(localStorage.getItem(key)!)).toEqual(saved)
  fireEvent.click(screen.getByRole('button', { name: 'Reconcile newer event' }))
  await waitFor(() => expect(localStorage.getItem(key)).toBeNull())
  expect(JSON.parse(localStorage.getItem(`${key}:archive:${id}`)!).saved).toEqual(saved)
  expect(api.retryWebhookEvent).not.toHaveBeenCalled()
})

it('keeps consent retries separate from saved action commands', async () => {
  const action = { eventId: id, command: { requestId: id, expectedVersion: 4, reason: 'Action receiver repaired.' } }
  localStorage.setItem(key, JSON.stringify(action))
  const consentKey = 'azoa:consent-webhook-retry:actor:self'
  api.listConsentWebhookEvents.mockResolvedValue({ ok: true, value: [{ ...row, eventType: 'Revoked' }] })
  api.retryConsentWebhookEvent.mockImplementation(async (eventId, command) => {
    expect(JSON.parse(localStorage.getItem(consentKey)!)).toEqual({ eventId, command })
    return { ok: false, error: { message: 'Consent response lost' } }
  })
  render(<WebhookEventsPanel userId="actor" family="consent" />)
  fireEvent.change(screen.getByLabelText('Delivery retry reason'), { target: { value: 'Consent receiver restored.' } })
  fireEvent.click(await screen.findByRole('button', { name: 'Retry delivery' }))
  await screen.findByText('Consent response lost')
  fireEvent.click(screen.getByRole('button', { name: 'Recover saved retry' }))
  await waitFor(() => expect(api.retryConsentWebhookEvent).toHaveBeenCalledTimes(2))
  expect(api.retryConsentWebhookEvent.mock.calls[0]).toEqual(api.retryConsentWebhookEvent.mock.calls[1])
  expect(JSON.parse(localStorage.getItem(key)!)).toEqual(action)
  expect(api.retryWebhookEvent).not.toHaveBeenCalled()
  expect(api.listWebhookEvents).not.toHaveBeenCalled()
})

it('does not delete a newer saved command when an unmounted retry resolves', async () => {
  const pending = { eventId: id, command: { requestId: id, expectedVersion: 4, reason: 'Receiver repaired and verified.' } }
  localStorage.setItem(key, JSON.stringify(pending))
  let finish!: (result: unknown) => void
  api.retryWebhookEvent.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
  const oldPanel = render(<WebhookEventsPanel userId="actor" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Recover saved retry' }))
  await waitFor(() => expect(api.retryWebhookEvent).toHaveBeenCalledTimes(1))
  oldPanel.unmount()
  const newer = { eventId: id, command: { ...pending.command, requestId: '22222222-2222-2222-2222-222222222222', expectedVersion: 8 } }
  localStorage.setItem(key, JSON.stringify(newer))
  render(<WebhookEventsPanel userId="actor" />)
  await act(async () => { finish({ ok: true, value: { eventId: id, requestId: id, version: 5 } }) })
  expect(JSON.parse(localStorage.getItem(key)!)).toEqual(newer)
  expect(screen.getByRole('button', { name: 'Recover saved retry' })).toBeTruthy()
})
