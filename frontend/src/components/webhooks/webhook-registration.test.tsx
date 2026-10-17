import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { WebhookRegistrationPanel, OperatorWebhookRegistrationPanel } from './webhook-registration'

const api = vi.hoisted(() => ({ getWebhookRegistration: vi.fn(), updateWebhookRegistration: vi.fn(), withdrawWebhookRegistration: vi.fn() }))
const operator = vi.hoisted(() => vi.fn())
vi.mock('@/lib/operator-client', () => ({ operatorRequest: operator }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (result: { ok: boolean }) => result.ok }))
beforeEach(() => { localStorage.clear(); vi.resetAllMocks(); api.getWebhookRegistration.mockResolvedValue({ ok: true, value: null }) })

it('persists an exact command before sending and retries it after response loss', async () => {
  api.updateWebhookRegistration.mockImplementationOnce(async command => {
    expect(JSON.parse(localStorage.getItem('azoa:webhook-registration:actor')!)).toEqual(command)
    return { ok: false, error: { message: 'Lost response' } }
  }).mockImplementationOnce(async command => ({ ok: true, value: { replayed: true, newSecret: null,
    receipt: { ...command, tenantId: 'actor', version: 1, destinationRevision: 1, secretRevision: 1, recordedAt: '2026-01-01T00:00:00Z' } } }))
  render(<WebhookRegistrationPanel userId="actor" />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save registration' })).toBeEnabled())
  fireEvent.change(screen.getByLabelText('HTTPS destination'), { target: { value: 'https://receiver.example/hook' } })
  fireEvent.change(screen.getByLabelText('Registration change reason'), { target: { value: 'Configure our event receiver.' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save registration' }))
  await screen.findByText('Lost response')
  fireEvent.click(screen.getByRole('button', { name: 'Retry saved change' }))
  await waitFor(() => expect(api.updateWebhookRegistration).toHaveBeenCalledTimes(2))
  expect(api.updateWebhookRegistration.mock.calls[0][0]).toEqual(api.updateWebhookRegistration.mock.calls[1][0])
  await waitFor(() => expect(localStorage.getItem('azoa:webhook-registration:actor')).toBeNull())
  expect(screen.queryByLabelText('New signing secret')).not.toBeInTheDocument()
})

it('never persists the generated secret and clears it on account change', async () => {
  api.updateWebhookRegistration.mockImplementation(async command => ({ ok: true, value: { replayed: false, newSecret: 'one-time-sensitive-value',
    receipt: { ...command, tenantId: 'actor', version: 1, destinationRevision: 1, secretRevision: 1, recordedAt: '2026-01-01T00:00:00Z' } } }))
  const view = render(<WebhookRegistrationPanel userId="actor" />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save registration' })).toBeEnabled())
  fireEvent.change(screen.getByLabelText('HTTPS destination'), { target: { value: 'https://receiver.example/hook' } })
  fireEvent.change(screen.getByLabelText('Registration change reason'), { target: { value: 'Configure our event receiver.' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save registration' }))
  expect(await screen.findByLabelText('New signing secret')).toHaveValue('one-time-sensitive-value')
  expect(JSON.stringify(localStorage)).not.toContain('one-time-sensitive-value')
  view.rerender(<WebhookRegistrationPanel userId="another-actor" />)
  expect(screen.queryByLabelText('New signing secret')).not.toBeInTheDocument()
})

it('keeps an uncertain command when the current version has not advanced', async () => {
  const command = { requestId: 'saved', registrationId: 'reg', expectedVersion: 0, url: 'https://receiver.example', isActive: true, rotateSecret: true, reason: 'Rotate our signing secret.' }
  localStorage.setItem('azoa:webhook-registration:actor', JSON.stringify(command))
  render(<WebhookRegistrationPanel userId="actor" />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Retry saved change' })).toBeEnabled())
  fireEvent.click(screen.getByRole('button', { name: 'Check current version' }))
  await screen.findByText(/still unresolved/)
  expect(JSON.parse(localStorage.getItem('azoa:webhook-registration:actor')!)).toEqual(command)
  expect(api.updateWebhookRegistration).not.toHaveBeenCalled()
})

it('withdraws a rejected command before unlocking another edit', async () => {
  const command = { requestId: 'saved', registrationId: 'reg', expectedVersion: 0, url: 'https://127.0.0.1', isActive: true, rotateSecret: true, reason: 'Configure receiver endpoint.' }
  localStorage.setItem('azoa:webhook-registration:actor', JSON.stringify(command))
  api.withdrawWebhookRegistration.mockResolvedValueOnce({ ok: false, error: { message: 'Withdrawal response lost' } })
    .mockResolvedValueOnce({ ok: true, value: { requestId: 'saved', alreadyApplied: false, receipt: null } })
  render(<WebhookRegistrationPanel userId="actor" />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Retry saved change' })).toBeEnabled())
  fireEvent.click(screen.getByRole('button', { name: 'Withdraw saved change' }))
  await screen.findByText('Withdrawal response lost')
  expect(localStorage.getItem('azoa:webhook-registration:actor')).not.toBeNull()
  expect(screen.getByLabelText('HTTPS destination')).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Withdraw saved change' }))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save registration' })).toBeEnabled())
  expect(api.withdrawWebhookRegistration.mock.calls[0][0]).toEqual(command)
  expect(api.withdrawWebhookRegistration.mock.calls[1][0]).toEqual(command)
  expect(localStorage.getItem('azoa:webhook-registration:actor')).toBeNull()
  expect(screen.getByLabelText('HTTPS destination')).toBeEnabled()
  expect(api.updateWebhookRegistration).not.toHaveBeenCalled()
})

it('operator editing uses tenant-scoped proxy commands and clears secrets when target changes', async () => {
  const tenant = '11111111-1111-1111-1111-111111111111'
  operator.mockImplementation(async (_path, init) => init?.method === 'PUT'
    ? { replayed: false, newSecret: 'operator-one-time-secret', receipt: { ...init.body, tenantId: tenant,
      version: 1, destinationRevision: 1, secretRevision: 1, recordedAt: '2026-01-01T00:00:00Z' } } : null)
  const view = render(<OperatorWebhookRegistrationPanel tenantId={tenant} />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save registration' })).toBeEnabled())
  fireEvent.change(screen.getByLabelText('HTTPS destination'), { target: { value: 'https://receiver.example/hook' } })
  fireEvent.change(screen.getByLabelText('Registration change reason'), { target: { value: 'Configure tenant event receiver.' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save registration' }))
  expect(await screen.findByLabelText('New signing secret')).toHaveValue('operator-one-time-secret')
  expect(operator).toHaveBeenCalledWith(`tenants/${tenant}/webhook-registration`, expect.objectContaining({ method: 'PUT' }))
  expect(api.updateWebhookRegistration).not.toHaveBeenCalled()
  expect(JSON.stringify(localStorage)).not.toContain('operator-one-time-secret')
  view.rerender(<OperatorWebhookRegistrationPanel tenantId="22222222-2222-2222-2222-222222222222" />)
  expect(screen.queryByLabelText('New signing secret')).not.toBeInTheDocument()
  await waitFor(() => expect(operator).toHaveBeenCalledWith('tenants/22222222-2222-2222-2222-222222222222/webhook-registration'))
})
