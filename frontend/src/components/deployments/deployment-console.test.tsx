import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { DeploymentConsole } from './deployment-console'

const state = vi.hoisted(() => ({ userId: 'first-user', wallets: [] }))
const api = vi.hoisted(() => ({ generateDeployment: vi.fn(), startDeployment: vi.fn(), cancelDeploymentRequest: vi.fn(), getDeploymentByRequest: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (result: { ok: boolean }) => result.ok }))
vi.mock('@/lib/azoa-context', () => ({ useAzoa: () => state }))
vi.mock('./deployment-operations', () => ({ DeploymentOperations: () => null }))
const artifact = { artifactId: 'artifact', artifactHash: 'hash', payloadHash: 'payload', formatVersion: '2', target: { kind: 'PlatformRuntime', chainType: 'Algorand', network: 'Testnet' } }
beforeEach(() => { localStorage.clear(); vi.clearAllMocks(); state.userId = 'first-user'; api.generateDeployment.mockResolvedValue({ ok: true, value: artifact }) })

it('retains the exact request after lost response and clears only after authoritative key cancellation', async () => {
  api.startDeployment.mockResolvedValue({ ok: false, error: { message: 'Response lost' } })
  api.cancelDeploymentRequest.mockResolvedValue({ ok: true, value: { operationId: 'original', state: 'Cancelled', ...artifact } })
  render(<DeploymentConsole bundleId="bundle" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Generate immutable artifact' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Activate runtime' }))
  await screen.findByRole('button', { name: 'Retry saved request' })
  const saved = JSON.parse(localStorage.getItem('azoa:deployment:first-user:bundle')!)
  fireEvent.click(screen.getByRole('button', { name: 'Retry saved request' }))
  await waitFor(() => expect(api.startDeployment).toHaveBeenCalledTimes(2))
  expect(api.startDeployment.mock.calls[1]).toEqual(api.startDeployment.mock.calls[0])
  fireEvent.click(screen.getByRole('button', { name: 'Cancel original request safely' }))
  await waitFor(() => expect(api.cancelDeploymentRequest).toHaveBeenCalledWith(saved.key, saved.request))
  await waitFor(() => expect(localStorage.getItem('azoa:deployment:first-user:bundle')).toBeNull())
});

it('does not publish a previous account artifact after an account switch', async () => {
  let finish!: (value: unknown) => void
  api.generateDeployment.mockReturnValueOnce(new Promise(resolve => { finish = resolve }))
  const view = render(<DeploymentConsole bundleId="bundle" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Generate immutable artifact' }))
  state.userId = 'next-user'; view.rerender(<DeploymentConsole bundleId="bundle" />)
  await act(async () => finish({ ok: true, value: artifact }))
  expect(screen.queryByRole('button', { name: 'Activate runtime' })).not.toBeInTheDocument()
  expect(api.startDeployment).not.toHaveBeenCalled()
});
