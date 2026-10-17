import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { WorkflowExecutionState, WorkflowNodeExecution, WorkflowRunResult } from 'azoa-sdk'

/**
 * Stubs the sibling `DagFlow` (owned by another agent, mid-edit elsewhere in
 * this pass) with a minimal DOM shape that reproduces the one thing these
 * tests need from real React Flow: `data-id` appears on BOTH a node wrapper
 * (`data-id="<nodeId>"`) and something nested inside it that is NOT a node
 * (a stand-in for a connection handle, whose real `data-id` is a composite
 * `${rfId}-${nodeId}-${handleId}-${type}` string matching no node). Mounting
 * real React Flow here would couple these tests to a file outside this
 * component's ownership.
 */
vi.mock('../../dag-flow', () => ({
  DagFlow: ({ nodes }: { nodes: Array<{ id: string }> }) => (
    <div data-testid="dag-flow-stub">
      {nodes.map((n) => (
        <div key={n.id} data-id={n.id} data-testid={`node-${n.id}`}>
          node
          <button type="button" data-id={`rf-1-${n.id}-source-source`} data-testid={`handle-${n.id}`}>
            handle
          </button>
        </div>
      ))}
    </div>
  ),
}))

const { workflowMock, auth } = vi.hoisted(() => ({
  auth: { userId: 'user-1' as string | null },
  workflowMock: {
    startWorkflow: vi.fn(),
    previewRun: vi.fn(),
    getRunStatus: vi.fn(),
    getExecutionState: vi.fn(),
    listRuns: vi.fn(),
    advance: vi.fn(),
    signal: vi.fn(),
    reconcileRun: vi.fn(),
  },
}))

vi.mock('@/lib/azoa-context', () => ({ useAzoa: () => auth }))

vi.mock('@/lib/azoa', () => ({
  azoa: { workflow: workflowMock },
  isOk: (r: { ok: boolean }) => r.ok === true,
}))

import { RunPanel } from '../../run-panel'

interface TestNode {
  id: string
  name: string
  nodeType: string
  state: string
  executionOrder: number
  isEntry: boolean
  isTerminal: boolean
}

function node(id: string, name: string, executionOrder: number): TestNode {
  return { id, name, nodeType: 'Holon.Create', state: 'Pending', executionOrder, isEntry: executionOrder === 0, isTerminal: false }
}

function makeAction(id: string, nodes: TestNode[] = [node(`${id}-n1`, 'Node 1', 0)]) {
  return { id, status: 'Active', nodes, edges: [] }
}

function makeRun(overrides: Partial<WorkflowRunResult> & { id: string; actionId: string }): WorkflowRunResult {
  return {
    userId: 'user-1',
    status: 'Running',
    startedAt: '2026-01-01T00:00:00.000Z',
    ...overrides,
  } as WorkflowRunResult
}

function makeExecState(
  runId: string,
  actionId: string,
  nodeStates: Array<[nodeId: string, state: WorkflowNodeExecution['state']]>,
): WorkflowExecutionState {
  const nodeExecutions: WorkflowNodeExecution[] = nodeStates.map(([nodeId, state]) => ({
    id: `exec-${nodeId}`,
    runId,
    nodeId,
    state,
    startedAt: '2026-01-01T00:00:00.000Z',
  }))
  return {
    runId,
    actionId,
    status: 'Suspended',
    startedAt: '2026-01-01T00:00:00.000Z',
    totalNodes: nodeExecutions.length,
    completedNodes: nodeExecutions.filter((n) => n.state === 'Succeeded').length,
    failedNodes: nodeExecutions.filter((n) => n.state === 'Failed').length,
    pendingNodes: nodeExecutions.filter((n) => n.state === 'Pending').length,
    nodeExecutions,
  }
}

async function flush() {
  await act(async () => {
    await Promise.resolve()
    await Promise.resolve()
  })
}

beforeEach(() => {
  Object.values(workflowMock).forEach((mock) => mock.mockReset())
  window.localStorage.clear()
  auth.userId = 'user-1'
  workflowMock.previewRun.mockImplementation(async (actionId: string) => ({ ok: true, value: { actionId, publishedVersionHash: 'revision-1', hasEconomicNodes: false, entries: [] } }))
  workflowMock.listRuns.mockResolvedValue({ ok: true, value: [] })
  workflowMock.getRunStatus.mockResolvedValue({ ok: false, error: { message: 'not found' } })
  workflowMock.getExecutionState.mockResolvedValue({ ok: true, value: makeExecState('run', 'action', []) })
})

afterEach(() => {
  window.localStorage.clear()
})

describe('RunPanel — C4 cross-action run bleed', () => {
  it('does not let a late action-A history response overwrite action-B history after a fast switch', async () => {
    const actionA = makeAction('action-a')
    const actionB = makeAction('action-b')

    let resolveA: (value: { ok: true; value: WorkflowRunResult[] }) => void = () => {}
    const pendingA = new Promise<{ ok: true; value: WorkflowRunResult[] }>((resolve) => {
      resolveA = resolve
    })

    workflowMock.listRuns.mockImplementation(async (actionId: string) => {
      if (actionId === 'action-a') return pendingA
      return { ok: true, value: [makeRun({ id: 'bbbbbbbb-0000-0000-0000-000000000001', actionId: 'action-b' })] }
    })

    const { rerender } = render(<RunPanel action={actionA} />)
    // Let action A's mount-effect kickoff fire — listRuns('action-a') is now in
    // flight and deliberately left unresolved.
    await flush()

    rerender(<RunPanel action={actionB} />)
    await waitFor(() => expect(screen.getByText(/Previous runs \(1\)/)).toBeInTheDocument())
    expect(screen.getByText(/bbbbbbbb/)).toBeInTheDocument()

    // NOW resolve action A's stale response — the generation guard must reject it.
    await act(async () => {
      resolveA({ ok: true, value: [makeRun({ id: 'aaaaaaaa-0000-0000-0000-000000000001', actionId: 'action-a' })] })
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(screen.getByText(/Previous runs \(1\)/)).toBeInTheDocument()
    expect(screen.getByText(/bbbbbbbb/)).toBeInTheDocument()
    expect(screen.queryByText(/aaaaaaaa/)).not.toBeInTheDocument()
  })

  it('rejects a persisted run whose actionId does not match the current action and clears the stale key', async () => {
    const action = makeAction('action-a')
    window.localStorage.setItem('azoa.actionRun.v2.user-1:action-a', 'stale-run-id')

    workflowMock.getRunStatus.mockImplementation(async (runId: string) => {
      if (runId === 'stale-run-id') {
        return { ok: true, value: makeRun({ id: 'stale-run-id', actionId: 'action-OTHER', status: 'Suspended' }) }
      }
      return { ok: false, error: { message: 'not found' } }
    })

    render(<RunPanel action={action} />)

    await waitFor(() => expect(workflowMock.getRunStatus).toHaveBeenCalledWith('stale-run-id'))
    await flush()

    // Never attached: "Start Run" is still the primary action.
    expect(screen.getByRole('button', { name: 'Start Run' })).toBeInTheDocument()
    expect(screen.queryByText(/stale-run-id/)).not.toBeInTheDocument()
    expect(window.localStorage.getItem('azoa.actionRun.v2.user-1:action-a')).toBeNull()
  })
})

describe('RunPanel — C5 stale advance-target survives an action switch', () => {
  it('resets a picked advanceFromNodeId (and a stale error) on action switch, so Advance posts the NEW action run/node', async () => {
    const actionA = makeAction('action-a', [node('a-n1', 'NodeA1', 0), node('a-n2', 'NodeA2', 1)])
    const actionB = makeAction('action-b', [node('b-n1', 'NodeB1', 0)])

    window.localStorage.setItem('azoa.actionRun.v2.user-1:action-a', 'run-a')
    window.localStorage.setItem('azoa.actionRun.v2.user-1:action-b', 'run-b')

    workflowMock.getRunStatus.mockImplementation(async (runId: string) => {
      if (runId === 'run-a') return { ok: true, value: makeRun({ id: 'run-a', actionId: 'action-a', status: 'Suspended' }) }
      if (runId === 'run-b') return { ok: true, value: makeRun({ id: 'run-b', actionId: 'action-b', status: 'Suspended' }) }
      return { ok: false, error: { message: 'not found' } }
    })
    workflowMock.getExecutionState.mockImplementation(async (runId: string) => {
      if (runId === 'run-a') {
        return { ok: true, value: makeExecState('run-a', 'action-a', [['a-n1', 'Succeeded'], ['a-n2', 'Succeeded']]) }
      }
      return { ok: true, value: makeExecState('run-b', 'action-b', [['b-n1', 'Succeeded']]) }
    })
    // Force an error banner on action A before switching, to also assert it clears.
    workflowMock.advance.mockResolvedValueOnce({ ok: false, error: { message: 'boom on action A' } })

    const { rerender } = render(<RunPanel action={actionA} />)

    const selectA = await screen.findByRole('combobox')
    // Default is the highest-executionOrder Succeeded node (a-n2); explicitly
    // pick the OTHER candidate so a real, non-default selection is in state.
    await waitFor(() => expect((selectA as HTMLSelectElement).value).toBe('a-n2'))
    fireEvent.change(selectA, { target: { value: 'a-n1' } })
    expect((screen.getByRole('combobox') as HTMLSelectElement).value).toBe('a-n1')

    fireEvent.click(screen.getByRole('button', { name: 'Advance' }))
    await waitFor(() => expect(screen.getByText('boom on action A')).toBeInTheDocument())

    rerender(<RunPanel action={actionB} />)

    // Stale error must not survive the switch.
    expect(screen.queryByText('boom on action A')).not.toBeInTheDocument()

    const selectB = await screen.findByRole('combobox')
    await waitFor(() => expect((selectB as HTMLSelectElement).value).toBe('b-n1'))

    workflowMock.advance.mockResolvedValueOnce({ ok: true, value: makeRun({ id: 'run-b', actionId: 'action-b', status: 'Running' }) })
    fireEvent.click(screen.getByRole('button', { name: 'Advance' }))

    await waitFor(() => expect(workflowMock.advance).toHaveBeenLastCalledWith('run-b', 'b-n1', expect.anything()))
    // The stale action-A node id must never have been posted against action B's run.
    expect(workflowMock.advance).not.toHaveBeenCalledWith('run-b', 'a-n1', expect.anything())
  })
})

describe('RunPanel — C10 poll failure handling', () => {
  it('tolerates a single transient failure and keeps polling without stalling', async () => {
    vi.useFakeTimers()
    try {
      const action = makeAction('action-a', [node('a-n1', 'NodeA1', 0)])
      workflowMock.startWorkflow.mockResolvedValue({ ok: true, value: makeRun({ id: 'run-a', actionId: 'action-a', status: 'Running' }) })

      let calls = 0
      workflowMock.getExecutionState.mockImplementation(async () => {
        calls += 1
        if (calls === 1) return { ok: false, error: { message: 'transient 502' } }
        // Terminal on success so the interval self-clears — makes the call count exact.
        return { ok: true, value: { ...makeExecState('run-a', 'action-a', [['a-n1', 'Succeeded']]), status: 'Succeeded' } }
      })

      render(<RunPanel action={action} />)
      await act(async () => { await vi.advanceTimersByTimeAsync(0) })
      fireEvent.click(screen.getByRole('button', { name: 'Start Run' }))
      await act(async () => {
        await Promise.resolve()
        await Promise.resolve()
      })

      // Initial zero-delay poll (fails) + one skipped tick + the retry (succeeds, terminal).
      await act(async () => {
        await vi.advanceTimersByTimeAsync(6000)
      })

      expect(calls).toBe(2)
      expect(screen.queryByText(/Live updates stopped/)).not.toBeInTheDocument()
      expect(screen.getByText(/1\/1 completed/)).toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })

  it('stops polling and shows a visible banner after repeated consecutive failures, and Retry resumes it', async () => {
    vi.useFakeTimers()
    try {
      const action = makeAction('action-a', [node('a-n1', 'NodeA1', 0)])
      workflowMock.startWorkflow.mockResolvedValue({ ok: true, value: makeRun({ id: 'run-a', actionId: 'action-a', status: 'Running' }) })
      workflowMock.getExecutionState.mockResolvedValue({ ok: false, error: { message: 'still down' } })

      render(<RunPanel action={action} />)
      await act(async () => { await vi.advanceTimersByTimeAsync(0) })
      fireEvent.click(screen.getByRole('button', { name: 'Start Run' }))
      await act(async () => {
        await Promise.resolve()
        await Promise.resolve()
      })

      await act(async () => {
        await vi.advanceTimersByTimeAsync(30000)
      })

      expect(workflowMock.getExecutionState).toHaveBeenCalledTimes(4)
      expect(screen.getByText(/Live updates stopped/)).toBeInTheDocument()

      // Confirm it really stopped — more elapsed time triggers no further calls.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(30000)
      })
      expect(workflowMock.getExecutionState).toHaveBeenCalledTimes(4)

      workflowMock.getExecutionState.mockResolvedValue({
        ok: true,
        value: { ...makeExecState('run-a', 'action-a', [['a-n1', 'Succeeded']]), status: 'Succeeded' },
      })
      fireEvent.click(screen.getByRole('button', { name: 'Retry' }))

      await act(async () => {
        await vi.advanceTimersByTimeAsync(1000)
      })

      expect(workflowMock.getExecutionState).toHaveBeenCalledTimes(5)
      expect(screen.queryByText(/Live updates stopped/)).not.toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('RunPanel — C12 data-id click matching', () => {
  it('selects a node on a real node click, and ignores a click on a handle-shaped data-id', async () => {
    const action = makeAction('action-a', [node('a-n1', 'NodeA1', 0)])
    window.localStorage.setItem('azoa.actionRun.v2.user-1:action-a', 'run-a')
    workflowMock.getRunStatus.mockResolvedValue({ ok: true, value: makeRun({ id: 'run-a', actionId: 'action-a', status: 'Running' }) })
    workflowMock.getExecutionState.mockResolvedValue({
      ok: true,
      value: makeExecState('run-a', 'action-a', [['a-n1', 'Succeeded']]),
    })

    render(<RunPanel action={action} />)
    await screen.findByTestId('dag-flow-stub')
    await flush()

    fireEvent.click(screen.getByTestId('handle-a-n1'))
    expect(screen.queryByText('NodeA1', { selector: 'p' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByTestId('node-a-n1'))
    expect(screen.getByText('NodeA1', { selector: 'p' })).toBeInTheDocument()
  })
})


describe('RunPanel durable start admission', () => {
  it('persists before sending and replays the exact consent and hash after a lost response and remount', async () => {
    workflowMock.previewRun.mockResolvedValue({ ok: true, value: { actionId: 'action-a', publishedVersionHash: 'revision-1',
      hasEconomicNodes: true, entries: [{ nodeId: 'fee', nodeName: 'Transfer', nodeType: 'Transfer', amount: '12', destination: 'recipient' }] } })
    workflowMock.startWorkflow.mockImplementationOnce(async (_actionId: string, request: unknown) => {
      expect(JSON.parse(localStorage.getItem('azoa.actionStart.v1.user-1:action-a')!)).toEqual(request)
      throw new Error('Response lost')
    }).mockResolvedValueOnce({ ok: true, value: makeRun({ id: 'original-run', actionId: 'action-a' }) })
    const first = render(<RunPanel action={makeAction('action-a')} />)
    await screen.findByText(/Transfer: Transfer/)
    expect(screen.getByRole('button', { name: 'Start Run' })).toBeDisabled()
    fireEvent.click(screen.getByRole('checkbox'))
    fireEvent.click(screen.getByRole('button', { name: 'Start Run' }))
    await screen.findByText('Response lost')
    const original = workflowMock.startWorkflow.mock.calls[0][1]
    expect(original).toMatchObject({ acknowledgeEconomicEffects: true, expectedPublishedHash: 'revision-1' })
    first.unmount()
    workflowMock.previewRun.mockResolvedValue({ ok: true, value: { actionId: 'action-a', publishedVersionHash: 'revision-2', hasEconomicNodes: false, entries: [] } })
    render(<RunPanel action={makeAction('action-a')} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Retry original start' }))
    await waitFor(() => expect(workflowMock.startWorkflow).toHaveBeenCalledTimes(2))
    expect(workflowMock.startWorkflow.mock.calls[1]).toEqual(['action-a', original])
    await waitFor(() => expect(localStorage.getItem('azoa.actionStart.v1.user-1:action-a')).toBeNull())
    expect(localStorage.getItem('azoa.actionRun.v2.user-1:action-a')).toBe('original-run')
  })

  it('does not attach a late start to a different account or reuse its saved request', async () => {
    let finish!: (value: unknown) => void
    workflowMock.startWorkflow.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve }))
    const view = render(<RunPanel action={makeAction('action-a')} />)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Start Run' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Start Run' }))
    await waitFor(() => expect(workflowMock.startWorkflow).toHaveBeenCalledTimes(1))
    auth.userId = 'user-2'
    view.rerender(<RunPanel action={makeAction('action-a')} />)
    await act(async () => { finish({ ok: true, value: makeRun({ id: 'late-account-one', actionId: 'action-a' }) }) })
    expect(screen.queryByText(/late-account-one/)).not.toBeInTheDocument()
    expect(localStorage.getItem('azoa.actionStart.v1.user-1:action-a')).not.toBeNull()
    expect(localStorage.getItem('azoa.actionStart.v1.user-2:action-a')).toBeNull()
    await waitFor(() => expect(screen.getByRole('button', { name: 'Start Run' })).toBeEnabled())
    expect(screen.queryByRole('button', { name: 'Retry original start' })).not.toBeInTheDocument()
  })

  it('retains the saved run on transient attachment failure and blocks a replacement start', async () => {
    localStorage.setItem('azoa.actionRun.v2.user-1:action-a', 'existing-run')
    workflowMock.getRunStatus.mockResolvedValueOnce({ ok: false, error: { message: 'temporarily unavailable' } })
      .mockResolvedValueOnce({ ok: true, value: makeRun({ id: 'existing-run', actionId: 'action-a' }) })
    render(<RunPanel action={makeAction('action-a')} />)
    await screen.findByText('temporarily unavailable')
    expect(localStorage.getItem('azoa.actionRun.v2.user-1:action-a')).toBe('existing-run')
    expect(screen.getByRole('button', { name: 'Start Run' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Retry restoring saved run' }))
    await screen.findByText('run existing-run')
    expect(workflowMock.startWorkflow).not.toHaveBeenCalled()
  })

  it('refuses network admission when durable request storage fails', async () => {
    render(<RunPanel action={makeAction('action-a')} />)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Start Run' })).toBeEnabled())
    const storage = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage unavailable') })
    try {
      fireEvent.click(screen.getByRole('button', { name: 'Start Run' }))
      await screen.findByText('Storage unavailable')
      expect(workflowMock.startWorkflow).not.toHaveBeenCalled()
    } finally { storage.mockRestore() }
  })
})


it('discards an old same-action poll after selecting another run', async () => {
  let finishOld!: (value: unknown) => void
  workflowMock.listRuns.mockResolvedValue({ ok: true, value: [
    makeRun({ id: 'aaaaaaaa-old', actionId: 'action-a' }), makeRun({ id: 'bbbbbbbb-new', actionId: 'action-a' }),
  ] })
  workflowMock.getRunStatus.mockImplementation(async (runId: string) => ({ ok: true, value: makeRun({ id: runId, actionId: 'action-a' }) }))
  workflowMock.getExecutionState.mockImplementation((runId: string) => runId === 'aaaaaaaa-old'
    ? new Promise((resolve) => { finishOld = resolve })
    : Promise.resolve({ ok: true, value: makeExecState(runId, 'action-a', [['action-a-n1', 'Pending']]) }))
  render(<RunPanel action={makeAction('action-a')} />)
  fireEvent.click(await screen.findByRole('button', { name: /aaaaaaaa/ }))
  await waitFor(() => expect(workflowMock.getExecutionState).toHaveBeenCalledWith('aaaaaaaa-old'))
  fireEvent.click(screen.getByRole('button', { name: /bbbbbbbb/ }))
  await screen.findByText('run bbbbbbbb-new')
  await waitFor(() => expect(workflowMock.getExecutionState).toHaveBeenCalledWith('bbbbbbbb-new'))
  await act(async () => { finishOld({ ok: true, value: { ...makeExecState('aaaaaaaa-old', 'action-a', [['action-a-n1', 'Succeeded']]), status: 'Succeeded' } }) })
  expect(screen.getByText('run bbbbbbbb-new')).toBeInTheDocument()
  expect(screen.getByText(/0\/1 completed/)).toBeInTheDocument()
  expect(localStorage.getItem('azoa.actionRun.v2.user-1:action-a')).toBe('bbbbbbbb-new')
})

it('cannot advance the previous run while a new history selection is loading', async () => {
  let finishNew!: (value: unknown) => void
  workflowMock.listRuns.mockResolvedValue({ ok: true, value: [
    makeRun({ id: 'aaaaaaaa-old', actionId: 'action-a' }), makeRun({ id: 'bbbbbbbb-new', actionId: 'action-a' }),
  ] })
  workflowMock.getRunStatus.mockImplementation((runId: string) => runId === 'bbbbbbbb-new'
    ? new Promise((resolve) => { finishNew = resolve })
    : Promise.resolve({ ok: true, value: makeRun({ id: runId, actionId: 'action-a', status: 'Suspended' }) }))
  workflowMock.getExecutionState.mockResolvedValue({ ok: true, value: makeExecState('aaaaaaaa-old', 'action-a', [['action-a-n1', 'Succeeded']]) })
  render(<RunPanel action={makeAction('action-a')} />)
  fireEvent.click(await screen.findByRole('button', { name: /aaaaaaaa/ }))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Advance' })).toBeEnabled())
  fireEvent.click(screen.getByRole('button', { name: /bbbbbbbb/ }))
  expect(screen.getByRole('button', { name: 'Advance' })).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Advance' }))
  expect(workflowMock.advance).not.toHaveBeenCalled()
  await act(async () => { finishNew({ ok: true, value: makeRun({ id: 'bbbbbbbb-new', actionId: 'action-a', status: 'Succeeded' }) }) })
})
