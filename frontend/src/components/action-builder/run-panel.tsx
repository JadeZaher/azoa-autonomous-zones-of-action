'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ErrorBanner } from '@/components/shared/error-banner'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoa } from '@/lib/azoa-context'
import { isTerminal, type Result, type WorkflowExecutionState,
 type WorkflowNodeExecution, type WorkflowReconciliationResult, type WorkflowRunResult, type ActionEconomicManifest, type StartWorkflowOptions } from 'azoa-sdk'
import { DagFlow } from './dag-flow'
import { getOutputShapeMirror } from './node-output-schema'
import { NftWorkflowCosts } from './nft-workflow-costs'

const POLL_INTERVAL_MS = 1500
const MAX_CONSECUTIVE_POLL_FAILURES = 4

interface RunPanelAction {
  id: string
  status: string
  nodes: Array<{ id: string; name: string; nodeType: string; state: string; executionOrder: number; isEntry: boolean; isTerminal: boolean; output?: string; error?: string }>
  edges: Array<{ id: string; sourceNodeId: string; targetNodeId: string; edgeType: string; condition?: string }>
}

const STATUS_COLOR: Record<string, string> = {
  Pending: 'bg-muted text-muted-foreground',
  Running: 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-300',
  Succeeded: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300',
  Failed: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300',
  Forked: 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-300',
  Cancelled: 'bg-red-50 text-red-700 dark:bg-red-900/20 dark:text-red-400',
  Suspended: 'bg-orange-100 text-orange-800 dark:bg-orange-900/30 dark:text-orange-300',
  AwaitingSignal: 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300',
  AwaitingTimer: 'bg-cyan-100 text-cyan-800 dark:bg-cyan-900/30 dark:text-cyan-300',
  AwaitingReconciliation: 'bg-violet-100 text-violet-800 dark:bg-violet-900/30 dark:text-violet-300',
}

function statusPill(status: string) {
  return <Badge className={STATUS_COLOR[status] ?? 'bg-muted text-muted-foreground'}>{status}</Badge>
}

const runKey = (scope: string) => `azoa.actionRun.v2.${scope}`
const startKey = (scope: string) => `azoa.actionStart.v1.${scope}`
type StoredStart = Pick<StartWorkflowOptions, 'idempotencyKey' | 'acknowledgeEconomicEffects' | 'expectedPublishedHash'>

export function RunPanel({ action }: { action: RunPanelAction }) {
  const { userId } = useAzoa()
  if (!userId) return <p>Sign in to run this action.</p>
  const scope = `${userId}:${action.id}`
  return <ScopedRunPanel key={scope} action={action} scope={scope} />
}

function ScopedRunPanel({ action, scope }: { action: RunPanelAction; scope: string }) {
  const [pendingStart, setPendingStart] = useState<StoredStart | null>(null)
  const [unresolvedRunId, setUnresolvedRunId] = useState<string | null>(null)
  const [hydrated, setHydrated] = useState(false)
  const [preview, setPreview] = useState<ActionEconomicManifest | null>(null)
  const [acknowledged, setAcknowledged] = useState(false)
  const alive = useRef(false)
  const startBusy = useRef(false)
  const selection = useRef({ generation: 0, runId: '' })
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  const [run, setRun] = useState<WorkflowRunResult | null>(null)
  const [execState, setExecState] = useState<WorkflowExecutionState | null>(null)
  const [starting, setStarting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null)
  const [history, setHistory] = useState<WorkflowRunResult[]>([])
  const [attaching, setAttaching] = useState(false)

  const [advanceFromNodeId, setAdvanceFromNodeId] = useState('')
  const [advancing, setAdvancing] = useState(false)

  const [gateId, setGateId] = useState('')
  const [signalPayload, setSignalPayload] = useState('')
  const [signaling, setSignaling] = useState(false)
  const [reconciling, setReconciling] = useState(false)
  const [reconcileResult, setReconcileResult] = useState<WorkflowReconciliationResult | null>(null)

  const [pollStalled, setPollStalled] = useState(false)
  const [pollGeneration, setPollGeneration] = useState(0)

  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const actionIdRef = useRef(action.id)

  const clearPoll = useCallback(() => {
    if (pollRef.current) {
      clearInterval(pollRef.current)
      pollRef.current = null
    }
  }, [])

  const pollFailureCountRef = useRef(0)
  const ticksToSkipRef = useRef(0)

  const pollOnce = useCallback(async (runId: string) => {
    const expected = selection.current
    if (expected.runId !== runId) return
    const res: Result<WorkflowExecutionState, { message: string }> = await azoa.workflow.getExecutionState(runId).catch((e: unknown) => ({
      ok: false as const, error: { message: e instanceof Error ? e.message : 'Unable to poll run' },
    }))
    if (!alive.current || selection.current !== expected) return
    if (isOk(res) && (res.value.runId !== runId || res.value.actionId !== action.id)) {
      setError('Execution response does not match the selected run.')
      clearPoll()
      return
    }
    if (isOk(res)) {
      pollFailureCountRef.current = 0
      ticksToSkipRef.current = 0
      setPollStalled(false)
      setExecState(res.value)
      setRun((current) => current?.id === runId ? { ...current, status: res.value.status } : current)
      if (isTerminal(res.value.status)) clearPoll()
    } else {
      pollFailureCountRef.current += 1
      if (pollFailureCountRef.current >= MAX_CONSECUTIVE_POLL_FAILURES) {
        setError(res.error.message)
        setPollStalled(true)
        clearPoll()
      } else {
        ticksToSkipRef.current = pollFailureCountRef.current
      }
    }
  }, [clearPoll, action.id])

  useEffect(() => {
    if (!run || isTerminal(run.status)) return
    const runId = run.id
    pollFailureCountRef.current = 0
    ticksToSkipRef.current = 0
    const tick = () => {
      if (ticksToSkipRef.current > 0) {
        ticksToSkipRef.current -= 1
        return
      }
      void pollOnce(runId)
    }
    const initialPoll = setTimeout(tick, 0)
    pollRef.current = setInterval(tick, POLL_INTERVAL_MS)
    return () => {
      clearTimeout(initialPoll)
      clearPoll()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- intentionally keyed on run.id (+ manual-retry generation) only
  }, [run?.id, pollGeneration, pollOnce, clearPoll])

  const handleResumePolling = useCallback(() => {
    setError(null)
    setPollStalled(false)
    setPollGeneration((g) => g + 1)
  }, [])

  useEffect(() => clearPoll, [clearPoll])

  const hydratedForRef = useRef<string | null>(null)

  useEffect(() => {
    if (!run || hydratedForRef.current !== action.id) return
    try {
      if (isTerminal(run.status)) window.localStorage.removeItem(runKey(scope))
      else window.localStorage.setItem(runKey(scope), run.id)
    } catch {
      const report = setTimeout(() => {
        if (alive.current) setError('Run remains selected, but browser storage could not save it. Keep its run ID for recovery.')
      }, 0)
      return () => clearTimeout(report)
    }
  }, [run, action.id, scope])

  const loadHistory = useCallback(async () => {
    try {
      const res = await azoa.workflow.listRuns(action.id)
      if (!alive.current) return
      if (isOk(res)) setHistory([...res.value].sort((a, b) => b.startedAt.localeCompare(a.startedAt)))
    } catch (e) {
      if (alive.current) setError(e instanceof Error ? e.message : 'Unable to load run history')
    }
  }, [action.id])

  const attachRun = useCallback(async (runId: string) => {
    const forActionId = action.id
    const expected = { generation: selection.current.generation + 1, runId }
    selection.current = expected
    clearPoll()
    setStarting(false)
    setAdvancing(false)
    setSignaling(false)
    setReconciling(false)
    setAdvanceFromNodeId('')
    setGateId('')
    setSignalPayload('')
    setReconcileResult(null)
    setAttaching(true)
    setError(null)
    try {
      const res = await azoa.workflow.getRunStatus(runId)
      if (!alive.current || selection.current !== expected || actionIdRef.current !== forActionId) return
      if (isOk(res)) {
        if (res.value.actionId !== forActionId || res.value.id !== runId) {
          window.localStorage.removeItem(runKey(scope))
          setUnresolvedRunId(null)
          setError('Saved run belongs to a different action.')
          return
        }
        setUnresolvedRunId(null)
        setRun(res.value)
        setExecState(null)
        setSelectedNodeId(null)
        if (isTerminal(res.value.status) && typeof window !== 'undefined') {
          window.localStorage.removeItem(runKey(scope))
        }
      } else {
        setUnresolvedRunId(runId)
        setError(res.error.message)
      }
    } catch (e) {
      if (alive.current && selection.current === expected) {
        setUnresolvedRunId(runId)
        setError(e instanceof Error ? e.message : 'Unable to restore run')
      }
    } finally {
      if (alive.current && selection.current === expected && actionIdRef.current === forActionId) setAttaching(false)
    }
  }, [action.id, scope, clearPoll])

  useEffect(() => {
    if (typeof window === 'undefined') return
    actionIdRef.current = action.id // current action generation — attachRun guards on this
    hydratedForRef.current = action.id // allow the persist effect to write for this action
    const kickoff = setTimeout(() => {
      let stored: string | null = null
      try {
        stored = window.localStorage.getItem(runKey(scope))
        setUnresolvedRunId(stored)
        const raw = window.localStorage.getItem(startKey(scope))
        if (raw) {
          const saved = JSON.parse(raw) as StoredStart
          if (typeof saved.idempotencyKey !== 'string' || !saved.idempotencyKey.trim()
            || typeof saved.acknowledgeEconomicEffects !== 'boolean'
            || typeof saved.expectedPublishedHash !== 'string' || !saved.expectedPublishedHash)
            throw new Error('Saved start request is invalid. Keep it for recovery; no new request was sent.')
          setPendingStart({ idempotencyKey: saved.idempotencyKey,
            acknowledgeEconomicEffects: saved.acknowledgeEconomicEffects, expectedPublishedHash: saved.expectedPublishedHash })
        }
        setHydrated(true)
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Unable to read saved start request')
      }
      void azoa.workflow.previewRun(action.id).then((result) => {
        if (!alive.current) return
        if (isOk(result) && result.value.actionId === action.id) setPreview(result.value)
        else setError(isOk(result) ? 'Preview belongs to a different action.' : result.error.message)
      }).catch((e: unknown) => {
        if (alive.current) setError(e instanceof Error ? e.message : 'Unable to load run preview')
      })
      if (stored) void attachRun(stored)
      void loadHistory()
    }, 0)
    return () => clearTimeout(kickoff)
  }, [action.id, scope, attachRun, loadHistory])

  const handleStart = async () => {
    if (startBusy.current || !hydrated || unresolvedRunId) return
    startBusy.current = true
    const expected = selection.current
    setStarting(true)
    setError(null)
    try {
      let request = pendingStart
      if (!request) {
        if (!preview?.publishedVersionHash || (preview.hasEconomicNodes && !acknowledged))
          throw new Error('Review the published action and acknowledge its economic effects before starting.')
        request = { idempotencyKey: crypto.randomUUID(), acknowledgeEconomicEffects: acknowledged,
          expectedPublishedHash: preview.publishedVersionHash }
        window.localStorage.setItem(startKey(scope), JSON.stringify(request))
        setPendingStart(request)
      }
      const res = await azoa.workflow.startWorkflow(action.id, request)
      if (!alive.current || selection.current !== expected) return
      if (isOk(res)) {
        if (res.value.actionId !== action.id) throw new Error('Start response belongs to a different action.')
        window.localStorage.setItem(runKey(scope), res.value.id)
        window.localStorage.removeItem(startKey(scope))
        setPendingStart(null)
        setStarting(false)
        selection.current = { generation: expected.generation + 1, runId: res.value.id }
        setUnresolvedRunId(null)
        setRun(res.value)
        setExecState(null)
        setSelectedNodeId(null)
        setReconcileResult(null)
        void loadHistory()
      } else setError(res.error.message)
    } catch (e) {
      if (alive.current && selection.current === expected) setError(e instanceof Error ? e.message : 'Failed to start run')
    } finally {
      startBusy.current = false
      if (alive.current && selection.current === expected) setStarting(false)
    }
  }

  const handleAdvance = async (fromNodeId: string) => {
    const expected = selection.current
    if (!run || !fromNodeId || attaching || expected.runId !== run.id) return
    if (!advanceCandidates.some((n) => n.id === fromNodeId)) {
      setError('The selected resume node no longer applies to this run — pick again.')
      return
    }
    setAdvancing(true)
    setError(null)
    try {
      const res = await azoa.workflow.advance(run.id, fromNodeId, {
        idempotencyKey: crypto.randomUUID(),
      })
      if (!alive.current || selection.current !== expected) return
      if (isOk(res)) {
        if (res.value.id !== expected.runId || res.value.actionId !== action.id) throw new Error('Response does not match the selected run.')
        setRun(res.value)
        setAdvanceFromNodeId('')
        await pollOnce(res.value.id)
      } else {
        setError(res.error.message)
      }
    } catch (e) {
      if (alive.current && selection.current === expected) setError(e instanceof Error ? e.message : 'Advance failed')
    } finally {
      if (alive.current && selection.current === expected) setAdvancing(false)
    }
  }

  const handleSignal = async () => {
    const expected = selection.current
    if (!run || !gateId.trim() || attaching || expected.runId !== run.id) return
    setSignaling(true)
    setError(null)
    try {
      const res = await azoa.workflow.signal(run.id, gateId.trim(), signalPayload.trim() || null, {
        idempotencyKey: crypto.randomUUID(),
      })
      if (!alive.current || selection.current !== expected) return
      if (isOk(res)) {
        if (res.value.id !== expected.runId || res.value.actionId !== action.id) throw new Error('Response does not match the selected run.')
        setRun(res.value)
        setGateId('')
        setSignalPayload('')
        await pollOnce(res.value.id)
      } else {
        setError(res.error.message)
      }
    } catch (e) {
      if (alive.current && selection.current === expected) setError(e instanceof Error ? e.message : 'Signal failed')
    } finally {
      if (alive.current && selection.current === expected) setSignaling(false)
    }
  }

  const handleReconcile = async () => {
    const expected = selection.current
    if (!run || attaching || expected.runId !== run.id) return
    setReconciling(true)
    setError(null)
    setReconcileResult(null)
    try {
      const res = await azoa.workflow.reconcileRun(run.id)
      if (!alive.current || selection.current !== expected) return
      if (isOk(res)) {
        setReconcileResult(res.value)
      } else {
        setError(res.error.message)
      }
      // Refresh run + execution state regardless — reconcile advances lifecycle.
      const s = await azoa.workflow.getRunStatus(run.id)
      if (!alive.current || selection.current !== expected) return
      if (isOk(s)) {
        if (s.value.id !== expected.runId || s.value.actionId !== action.id) throw new Error('Response does not match the selected run.')
        setRun(s.value)
      }
      await pollOnce(run.id)
    } catch (e) {
      if (alive.current && selection.current === expected) setError(e instanceof Error ? e.message : 'Reconcile failed')
    } finally {
      if (alive.current && selection.current === expected) setReconciling(false)
    }
  }

  if (action.status !== 'Active') {
    return (
      <p className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">
        This action must be <strong>Active</strong> (published) before it can be run. Publish it from the{' '}
        <strong>Actions</strong> tab first.
      </p>
    )
  }

  const execByNodeId = new Map<string, WorkflowNodeExecution>()
  for (const ne of execState?.nodeExecutions ?? []) execByNodeId.set(ne.nodeId, ne)
  const liveNodes = action.nodes.map((n) => {
    const live = execByNodeId.get(n.id)
    return live
      ? { ...n, state: live.state, output: live.output, error: live.error }
      : n
  })

  const selectedExec = selectedNodeId ? execByNodeId.get(selectedNodeId) : undefined
  const selectedNode = selectedNodeId ? action.nodes.find((n) => n.id === selectedNodeId) : undefined

  const advanceCandidates = action.nodes
    .filter((n) => execByNodeId.get(n.id)?.state === 'Succeeded')
    .sort((a, b) => b.executionOrder - a.executionOrder)
  const defaultAdvanceFrom = advanceCandidates[0]?.id ?? ''
  const effectiveAdvanceFrom = advanceFromNodeId || defaultAdvanceFrom

  return (
    <div className="flex flex-col gap-3">
      {/* Run header */}
      <div className="flex flex-wrap items-center gap-3 rounded-md border bg-card p-3">
        <Button size="sm" disabled={!hydrated || starting || attaching || !!unresolvedRunId || (!pendingStart && (!preview?.publishedVersionHash || (preview.hasEconomicNodes && !acknowledged))) || (run !== null && !isTerminal(run.status))} onClick={handleStart}>
          {starting ? 'Starting...' : run && !isTerminal(run.status) ? 'Run in progress...' : pendingStart ? 'Retry original start' : 'Start Run'}
        </Button>

        {run && (
          <>
            {statusPill(run.status)}
            <span className="font-mono text-xs text-muted-foreground">run {run.id}</span>
            {run.status === 'Failed' && run.failReason && (
              <span className="text-xs text-red-600 dark:text-red-400">{run.failReason}</span>
            )}
          </>
        )}

        {execState && (
          <span className="text-xs text-muted-foreground">
            {execState.completedNodes}/{execState.totalNodes} completed
            {execState.failedNodes > 0 && (
              <span className="ml-1 text-red-600 dark:text-red-400">· {execState.failedNodes} failed</span>
            )}
            {execState.pendingNodes > 0 && <span className="ml-1">· {execState.pendingNodes} pending</span>}
          </span>
        )}
      </div>

      {unresolvedRunId && !attaching && (
        <Button size="sm" variant="outline" onClick={() => void attachRun(unresolvedRunId)}>Retry restoring saved run</Button>
      )}
      {pendingStart ? (
        <p className="text-sm">A saved start request is awaiting confirmation. Retry sends its original consent and published version.</p>
      ) : preview && (
        <div className="space-y-2 rounded-md border p-3 text-sm">
          <p>Published version <code className="break-all">{preview.publishedVersionHash ?? 'unavailable'}</code></p>
          {preview.entries.map((entry) => (
            <p key={entry.nodeId}>{entry.nodeName}: {entry.nodeType}{entry.amount ? ` - ${entry.amount}` : ''}{entry.destination ? ` to ${entry.destination}` : ''}</p>
          ))}
          {preview.hasEconomicNodes && (
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={acknowledged} onChange={(event) => setAcknowledged(event.target.checked)} />
              I authorize the economic effects shown for this published version.
            </label>
          )}
        </div>
      )}
      {error && <ErrorBanner message={error} />}

      {pollStalled && run && !isTerminal(run.status) && (
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-amber-300 bg-amber-50 p-2 text-xs text-amber-800 dark:border-amber-900/50 dark:bg-amber-900/10 dark:text-amber-300">
          <span>Live updates stopped after repeated errors — the run may still be progressing on the server.</span>
          <Button size="sm" variant="outline" onClick={handleResumePolling}>
            Retry
          </Button>
        </div>
      )}

      {/* Select a run to inspect its durable outcome. */}
      {history.length > 0 && (
        <div className="rounded-md border">
          <div className="border-b px-3 py-2 text-xs font-medium text-muted-foreground">
            Previous runs ({history.length})
          </div>
          <ul className="divide-y">
            {history.map((h) => (
              <li key={h.id}>
                <button
                  type="button"
                  onClick={() => void attachRun(h.id)}
                  className="flex w-full items-center gap-2 px-3 py-2 text-left text-xs transition-colors hover:bg-accent/50"
                >
                  {statusPill(h.status)}
                  <span className="text-muted-foreground">{formatStarted(h.startedAt)}</span>
                  <span className="ml-auto font-mono text-[10px] text-muted-foreground">{h.id.slice(0, 8)}…</span>
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}

      {run && (
        <>
          {/* Live DAG overlay — click a node to inspect its output/error below.
              React Flow sets `data-id` on node wrappers AND on handles
              (`${rfId}-${nodeId}-${handleId}-${type}`) and edges, so the
              nearest `data-id` ancestor isn't necessarily a real node id —
              e.g. clicking a node's connection handle would otherwise set
              selectedNodeId to a composite string matching no node, silently
              closing the inspector below. Only accept it if it's an actual
              id from this action's node list. */}
          <div onClick={(e) => {
            const target = (e.target as HTMLElement).closest('[data-id]')
            const nodeId = target?.getAttribute('data-id')
            if (nodeId && action.nodes.some((n) => n.id === nodeId)) setSelectedNodeId(nodeId)
          }}>
            <DagFlow nodes={liveNodes} edges={action.edges} />
          </div>

          {/* Advance control — Suspended (manual-advance park) */}
          {run.status === 'Suspended' && (
            <div className="flex flex-col gap-2 rounded-md border border-orange-300 bg-orange-50 p-3 dark:border-orange-900/50 dark:bg-orange-900/10">
              <p className="text-xs font-medium text-orange-800 dark:text-orange-300">
                Run suspended — resume from the completed node to advance into its successor(s).
              </p>
              <div className="flex flex-wrap items-end gap-2">
                <div className="flex flex-1 flex-col gap-1 min-w-[200px]">
                  <Label className="text-xs">Resume from completed node</Label>
                  <select
                    value={effectiveAdvanceFrom}
                    onChange={(e) => setAdvanceFromNodeId(e.target.value)}
                    className="h-9 rounded-md border border-input bg-background px-2 text-sm"
                  >
                    {advanceCandidates.length === 0 && <option value="">No completed nodes yet</option>}
                    {advanceCandidates.map((n) => (
                      <option key={n.id} value={n.id}>
                        {n.name} ({n.nodeType})
                      </option>
                    ))}
                  </select>
                </div>
                <Button
                  size="sm"
                  disabled={attaching || advancing || !effectiveAdvanceFrom}
                  onClick={() => handleAdvance(effectiveAdvanceFrom)}
                >
                  {advancing ? 'Advancing...' : 'Advance'}
                </Button>
              </div>
            </div>
          )}

          {/* Signal control — AwaitingSignal (parked at a GATE node) */}
          {run.status === 'AwaitingSignal' && (
            <div className="flex flex-col gap-2 rounded-md border border-blue-300 bg-blue-50 p-3 dark:border-blue-900/50 dark:bg-blue-900/10">
              <p className="text-xs font-medium text-blue-800 dark:text-blue-300">
                Run parked at a gate — deliver a signal to resume.
              </p>
              <div className="flex flex-wrap items-end gap-2">
                <div className="flex flex-col gap-1">
                  <Label className="text-xs">Gate Id</Label>
                  <Input value={gateId} onChange={(e) => setGateId(e.target.value)} placeholder="gate-id" className="w-40" />
                </div>
                <div className="flex flex-1 flex-col gap-1 min-w-[200px]">
                  <Label className="text-xs">Payload (optional)</Label>
                  <Input value={signalPayload} onChange={(e) => setSignalPayload(e.target.value)} placeholder="optional payload" />
                </div>
                <Button size="sm" disabled={attaching || signaling || !gateId.trim()} onClick={handleSignal}>
                  {signaling ? 'Signaling...' : 'Signal'}
                </Button>
              </div>
            </div>
          )}

          {run.status === 'AwaitingTimer' && (
            <p className="rounded-md border border-cyan-300 bg-cyan-50 p-3 text-xs text-cyan-800 dark:border-cyan-900/50 dark:bg-cyan-900/10 dark:text-cyan-300">
              Run parked awaiting a timer — it will resume automatically once due.
            </p>
          )}

          {/* AwaitingReconciliation: a chain-action node's on-chain confirmation was
              indeterminate. Resolved by a manual re-probe (reconcileRun) or the
              backend's own sweep — never auto-retried blindly. */}
          {run.status === 'AwaitingReconciliation' && (
            <div className="flex flex-col gap-2 rounded-md border border-violet-300 bg-violet-50 p-3 dark:border-violet-900/50 dark:bg-violet-900/10">
              <p className="text-xs text-violet-800 dark:text-violet-300">
                Run awaiting chain reconciliation — re-probe checks on-chain truth: a
                confirmed transaction resolves without re-minting, a failed one is
                released to retry, and an indeterminate one stays parked for the next sweep.
              </p>
              <div>
                <Button size="sm" variant="outline" disabled={attaching || reconciling} onClick={handleReconcile}>
                  {reconciling ? 'Probing…' : 'Re-probe chain'}
                </Button>
              </div>
              {reconcileResult && (
                <dl className="grid grid-cols-3 gap-2 text-[11px] text-violet-800 dark:text-violet-300">
                  <div>
                    <dt className="text-violet-600 dark:text-violet-400">Confirmed</dt>
                    <dd className="font-mono">{reconcileResult.reconciledConfirmed}</dd>
                  </div>
                  <div>
                    <dt className="text-violet-600 dark:text-violet-400">Released to retry</dt>
                    <dd className="font-mono">{reconcileResult.releasedFailedOnChain}</dd>
                  </div>
                  <div>
                    <dt className="text-violet-600 dark:text-violet-400">Still indeterminate</dt>
                    <dd className="font-mono">{reconcileResult.stillIndeterminate}</dd>
                  </div>
                </dl>
              )}
            </div>
          )}

          {/* Per-node output inspector */}
          {selectedNode && (
            <div className="rounded-md border p-3">
              <div className="mb-2 flex items-center justify-between">
                <p className="text-sm font-medium">{selectedNode.name}</p>
                {selectedExec && statusPill(selectedExec.state)}
              </div>
              <p className="mb-2 font-mono text-[10px] text-muted-foreground">
                {outputShapeCaption(selectedNode.nodeType)}
              </p>
              <Separator className="mb-2" />
              {selectedExec && selectedExec.state !== 'Pending' && ['NftMint', 'Grant'].includes(selectedNode.nodeType) &&
                <NftWorkflowCosts key={`${scope}:${selectedExec.id}`} scope={scope} executionId={selectedExec.id} />}
              {selectedExec?.error && (
                <p className="mb-2 break-words text-xs text-red-600 dark:text-red-400">{selectedExec.error}</p>
              )}
              {selectedExec?.output ? (
                <div className="max-h-64 overflow-auto break-all text-xs">
                  <JsonViewer data={tryParseJson(selectedExec.output)} />
                </div>
              ) : (
                <p className="text-xs text-muted-foreground">No output recorded yet for this node.</p>
              )}
            </div>
          )}
        </>
      )}
    </div>
  )
}

function formatStarted(iso: string): string {
  const d = new Date(iso)
  return isNaN(d.getTime()) ? iso : d.toLocaleString()
}

function outputShapeCaption(nodeType: string): string {
  const shape = getOutputShapeMirror(nodeType)
  if (shape.open) return 'Free-form output'
  return `Expected fields: ${Object.keys(shape.fields).join(', ')}`
}

function tryParseJson(raw: string): unknown {
  try {
    return JSON.parse(raw)
  } catch {
    return raw
  }
}
