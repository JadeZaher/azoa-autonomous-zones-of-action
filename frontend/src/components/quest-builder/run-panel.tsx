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
import { isTerminal, type Result, type SdkError, type WorkflowExecutionState, type WorkflowNodeExecution, type WorkflowRunResult } from 'azoa-sdk'
import { DagFlow } from './dag-flow'

const POLL_INTERVAL_MS = 1500

interface RunPanelQuest {
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
  // Reconcile-before-retry park: engine probes chain truth before retrying a
  // chain-action node. Auto-resolves; no user action. (SDK union may lag; keyed by string.)
  AwaitingReconciliation: 'bg-violet-100 text-violet-800 dark:bg-violet-900/30 dark:text-violet-300',
}

function statusPill(status: string) {
  return <Badge className={STATUS_COLOR[status] ?? 'bg-muted text-muted-foreground'}>{status}</Badge>
}

/** Per-quest localStorage key holding the last-known active run id. */
const runKey = (questId: string) => `azoa.questRun.${questId}`

// listRuns(questId) + reconcileRun(runId) land in a sibling SDK bump. The on-disk
// built package types may lag, so we assert a narrow shape here (per task guidance);
// the panel degrades gracefully if a method is absent at runtime.
// - listRuns → GET quest/{id}/runs, run summaries shaped like getRunStatus.
// - reconcileRun → POST quest/runs/{runId}/reconcile, re-probes chain truth.
type WorkflowExtras = {
  listRuns?: (questId: string) => Promise<Result<WorkflowRunResult[], SdkError>>
  reconcileRun?: (runId: string) => Promise<Result<unknown, SdkError>>
}
const workflowExtras = azoa.workflow as unknown as typeof azoa.workflow & WorkflowExtras

/**
 * Durable step-based quest run panel: start a run, poll its execution state,
 * overlay live per-node status on the read-only DagFlow, and surface the
 * Advance/Signal controls for parked (Suspended/AwaitingSignal) runs.
 */
export function RunPanel({ quest }: { questId: string; quest: RunPanelQuest }) {
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

  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  // Tracks the currently-shown quest id. attachRun captures quest.id at call time
  // and bails its setRun/removeItem continuation if the quest switched while the
  // status fetch was in flight — otherwise a resolved run could land on the wrong
  // quest (this component instance is reused across quests, no upstream `key`).
  const questIdRef = useRef(quest.id)

  const clearPoll = useCallback(() => {
    if (pollRef.current) {
      clearInterval(pollRef.current)
      pollRef.current = null
    }
  }, [])

  const pollOnce = useCallback(async (runId: string) => {
    const res = await azoa.workflow.getExecutionState(runId)
    if (isOk(res)) {
      setExecState(res.value)
      if (isTerminal(res.value.status)) clearPoll()
    } else {
      setError(res.error.message)
      clearPoll()
    }
  }, [clearPoll])

  // (Re)start polling only when the run ID changes — NOT on every status change,
  // else each poll (which updates status) would tear down and rebuild the interval.
  // pollOnce/clearPoll are stable useCallbacks, so excluding them is safe here.
  useEffect(() => {
    if (!run || isTerminal(run.status)) return
    void pollOnce(run.id)
    pollRef.current = setInterval(() => void pollOnce(run.id), POLL_INTERVAL_MS)
    return clearPoll
    // eslint-disable-next-line react-hooks/exhaustive-deps -- intentionally keyed on run.id only
  }, [run?.id, pollOnce, clearPoll])

  useEffect(() => clearPoll, [clearPoll])

  // Holds the quest id whose stored key has been hydrated (mount-attach ran). The
  // persist effect only writes once this matches the current quest — this prevents
  // (a) clobbering the stored key before mount-attach reads it, and (b) writing a
  // stale run under a newly-switched quest's key before its own mount-attach resets.
  const hydratedForRef = useRef<string | null>(null)

  // Persist the active run id per quest so a refresh / tab-switch can re-attach.
  // Terminal runs clear the key (nothing to resume); the history list covers them.
  useEffect(() => {
    if (typeof window === 'undefined') return
    if (hydratedForRef.current !== quest.id) return // not yet hydrated for THIS quest
    if (run && !isTerminal(run.status)) {
      window.localStorage.setItem(runKey(quest.id), run.id)
    } else {
      window.localStorage.removeItem(runKey(quest.id))
    }
  }, [run, quest.id])

  const loadHistory = useCallback(async () => {
    if (typeof workflowExtras.listRuns !== 'function') return
    const res = await workflowExtras.listRuns(quest.id)
    if (isOk(res)) {
      // Newest first — startedAt ISO descending.
      setHistory([...res.value].sort((a, b) => b.startedAt.localeCompare(a.startedAt)))
    }
  }, [quest.id])

  // Re-attach a persisted run on mount: fetch its current status; clear the key if
  // the run is terminal or the fetch 404s (stale/deleted). Also load run history.
  const attachRun = useCallback(async (runId: string) => {
    const forQuestId = quest.id
    setAttaching(true)
    setError(null)
    try {
      const res = await azoa.workflow.getRunStatus(runId)
      // Bail if the quest switched while the fetch was in flight — the resolved
      // run belongs to a quest that's no longer shown.
      if (questIdRef.current !== forQuestId) return
      if (isOk(res)) {
        setRun(res.value)
        setExecState(null)
        setSelectedNodeId(null)
        if (isTerminal(res.value.status) && typeof window !== 'undefined') {
          window.localStorage.removeItem(runKey(forQuestId))
        }
      } else if (typeof window !== 'undefined') {
        // 404 / gone — drop the stale key silently.
        window.localStorage.removeItem(runKey(forQuestId))
      }
    } finally {
      if (questIdRef.current === forQuestId) setAttaching(false)
    }
  }, [quest.id])

  // Mount / quest switch: reset transient run state (this component instance is
  // reused across quests — no `key` prop upstream), then re-attach the persisted
  // run for THIS quest (if any) and hydrate the history list.
  useEffect(() => {
    if (typeof window === 'undefined') return
    questIdRef.current = quest.id // current quest generation — attachRun guards on this
    setRun(null)
    setExecState(null)
    setSelectedNodeId(null)
    setHistory([])
    const stored = window.localStorage.getItem(runKey(quest.id))
    hydratedForRef.current = quest.id // allow the persist effect to write for this quest
    if (stored) void attachRun(stored)
    void loadHistory()
    // Keyed on quest.id only — attachRun/loadHistory are stable useCallbacks.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [quest.id])

  const handleStart = async () => {
    setStarting(true)
    setError(null)
    setExecState(null)
    setSelectedNodeId(null)
    try {
      const res = await azoa.workflow.startWorkflow(quest.id)
      if (isOk(res)) {
        setRun(res.value)
        void loadHistory()
      } else {
        setError(res.error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to start run')
    } finally {
      setStarting(false)
    }
  }

  const handleAdvance = async (fromNodeId: string) => {
    if (!run || !fromNodeId) return
    setAdvancing(true)
    setError(null)
    try {
      const res = await azoa.workflow.advance(run.id, fromNodeId, {
        idempotencyKey: crypto.randomUUID(),
      })
      if (isOk(res)) {
        setRun(res.value)
        setAdvanceFromNodeId('')
        await pollOnce(res.value.id)
      } else {
        setError(res.error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Advance failed')
    } finally {
      setAdvancing(false)
    }
  }

  const handleSignal = async () => {
    if (!run || !gateId.trim()) return
    setSignaling(true)
    setError(null)
    try {
      const res = await azoa.workflow.signal(run.id, gateId.trim(), signalPayload.trim() || null, {
        idempotencyKey: crypto.randomUUID(),
      })
      if (isOk(res)) {
        setRun(res.value)
        setGateId('')
        setSignalPayload('')
        await pollOnce(res.value.id)
      } else {
        setError(res.error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Signal failed')
    } finally {
      setSignaling(false)
    }
  }

  const handleReconcile = async () => {
    if (!run || typeof workflowExtras.reconcileRun !== 'function') return
    setReconciling(true)
    setError(null)
    try {
      const res = await workflowExtras.reconcileRun(run.id)
      if (!isOk(res)) {
        setError(res.error.message)
      }
      // Refresh run + execution state regardless — reconcile advances lifecycle.
      const s = await azoa.workflow.getRunStatus(run.id)
      if (isOk(s)) setRun(s.value)
      await pollOnce(run.id)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Reconcile failed')
    } finally {
      setReconciling(false)
    }
  }

  if (quest.status !== 'Active') {
    return (
      <p className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">
        This quest must be <strong>Active</strong> (published) before it can be run. Publish it from the{' '}
        <strong>Actions</strong> tab first.
      </p>
    )
  }

  // Merge live per-node execution state onto the quest's static nodes, keyed by
  // real node id (UUID), for the DagFlow overlay.
  const execByNodeId = new Map<string, WorkflowNodeExecution>()
  for (const ne of execState?.nodeExecutions ?? []) execByNodeId.set(ne.nodeId, ne)
  const liveNodes = quest.nodes.map((n) => {
    const live = execByNodeId.get(n.id)
    return live
      ? { ...n, state: live.state, output: live.output, error: live.error }
      : n
  })

  const selectedExec = selectedNodeId ? execByNodeId.get(selectedNodeId) : undefined
  const selectedNode = selectedNodeId ? quest.nodes.find((n) => n.id === selectedNodeId) : undefined

  // Advance resumes FROM a completed node into its successor. The natural default
  // is the most-recently-succeeded node (highest executionOrder among Succeeded),
  // so the operator never hand-types a UUID. Candidates = all Succeeded nodes.
  const advanceCandidates = quest.nodes
    .filter((n) => execByNodeId.get(n.id)?.state === 'Succeeded')
    .sort((a, b) => b.executionOrder - a.executionOrder)
  const defaultAdvanceFrom = advanceCandidates[0]?.id ?? ''
  const effectiveAdvanceFrom = advanceFromNodeId || defaultAdvanceFrom

  return (
    <div className="flex flex-col gap-3">
      {/* Run header */}
      <div className="flex flex-wrap items-center gap-3 rounded-md border bg-card p-3">
        <Button size="sm" disabled={starting || (run !== null && !isTerminal(run.status))} onClick={handleStart}>
          {starting ? 'Starting...' : run && !isTerminal(run.status) ? 'Run in progress...' : 'Start Run'}
        </Button>

        {run && (
          <>
            {statusPill(run.status)}
            <span className="font-mono text-xs text-muted-foreground">run {run.id}</span>
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

      {error && <ErrorBanner message={error} />}

      {/* Run history — shown when no run is attached. Click a row to re-attach. */}
      {!run && !attaching && history.length > 0 && (
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
          {/* Live DAG overlay — click a node to inspect its output/error below */}
          <div onClick={(e) => {
            const target = (e.target as HTMLElement).closest('[data-id]')
            const nodeId = target?.getAttribute('data-id')
            if (nodeId) setSelectedNodeId(nodeId)
          }}>
            <DagFlow nodes={liveNodes} edges={quest.edges} />
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
                  disabled={advancing || !effectiveAdvanceFrom}
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
                <Button size="sm" disabled={signaling || !gateId.trim()} onClick={handleSignal}>
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

          {/* AwaitingReconciliation: engine is probing chain truth before retrying a
              chain-action node (no double-mint). Auto-resolves; no user action, no
              re-probe control exposed by the SDK. Compared as string — union may lag. */}
          {(run.status as string) === 'AwaitingReconciliation' && (
            <div className="flex flex-col gap-2 rounded-md border border-violet-300 bg-violet-50 p-3 dark:border-violet-900/50 dark:bg-violet-900/10">
              <p className="text-xs text-violet-800 dark:text-violet-300">
                Run awaiting chain reconciliation — the engine is verifying on-chain truth
                before retrying a chain action (guards against double-execution). This
                resolves automatically once the chain state is probed; no action needed.
              </p>
              {typeof workflowExtras.reconcileRun === 'function' && (
                <div>
                  <Button size="sm" variant="outline" disabled={reconciling} onClick={handleReconcile}>
                    {reconciling ? 'Probing…' : 'Re-probe chain'}
                  </Button>
                </div>
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
              <Separator className="mb-2" />
              {selectedExec?.error && <p className="mb-2 text-xs text-red-600 dark:text-red-400">{selectedExec.error}</p>}
              {selectedExec?.output ? (
                <JsonViewer data={tryParseJson(selectedExec.output)} />
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

function tryParseJson(raw: string): unknown {
  try {
    return JSON.parse(raw)
  } catch {
    return raw
  }
}
