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
import { isTerminal, type WorkflowExecutionState, type WorkflowNodeExecution, type WorkflowReconciliationResult, type WorkflowRunResult } from 'azoa-sdk'
import { DagFlow } from './dag-flow'
import { getOutputShapeMirror } from './node-output-schema'

const POLL_INTERVAL_MS = 1500
// Bounded: after this many *consecutive* poll failures, stop polling and
// surface it visibly rather than retrying forever or freezing silently.
const MAX_CONSECUTIVE_POLL_FAILURES = 4

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
  // chain-action node. Resolved by a manual reconcileRun re-probe or the
  // backend's own sweep — never auto-retried blindly.
  AwaitingReconciliation: 'bg-violet-100 text-violet-800 dark:bg-violet-900/30 dark:text-violet-300',
}

function statusPill(status: string) {
  return <Badge className={STATUS_COLOR[status] ?? 'bg-muted text-muted-foreground'}>{status}</Badge>
}

/** Per-quest localStorage key holding the last-known active run id. */
const runKey = (questId: string) => `azoa.questRun.${questId}`

/**
 * Durable step-based quest run panel: start a run, poll its execution state,
 * overlay live per-node status on the read-only DagFlow, and surface the
 * Advance/Signal controls for parked (Suspended/AwaitingSignal) runs.
 */
export function RunPanel({ quest }: { quest: RunPanelQuest }) {
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

  // True once polling has given up after MAX_CONSECUTIVE_POLL_FAILURES in a
  // row — surfaced as a visible "stopped" banner (with a manual retry) rather
  // than letting the run view silently freeze at its last known state.
  const [pollStalled, setPollStalled] = useState(false)
  // Bumped by the manual retry action to force the polling effect below to
  // re-run for the SAME run id (run.id alone doesn't change on a manual retry).
  const [pollGeneration, setPollGeneration] = useState(0)

  // Mirrors the last quest.id this component actually rendered transient run
  // state for. This component instance is reused across quests (no upstream
  // `key`), so a quest switch must reset run/exec/history state synchronously —
  // done here, during render, per React's "adjusting state when a prop changes"
  // pattern, rather than from inside an effect (which would apply one paint late
  // and trips react-hooks/set-state-in-effect). The actual side effects — the
  // localStorage read and the attach/history network calls — stay in the effect
  // below, which now only performs work, never resets state directly.
  //
  // Every piece of transient, per-run-or-per-quest UI state must be reset here,
  // not just the "obvious" run/exec state: advanceFromNodeId, gateId,
  // signalPayload, error and pollStalled all previously survived a quest
  // switch, so a value picked (or an error raised) against quest A could be
  // silently applied to / shown for quest B once the switch landed.
  const [renderedQuestId, setRenderedQuestId] = useState(quest.id)
  if (quest.id !== renderedQuestId) {
    setRenderedQuestId(quest.id)
    setRun(null)
    setExecState(null)
    setSelectedNodeId(null)
    setHistory([])
    setReconcileResult(null)
    setAdvanceFromNodeId('')
    setGateId('')
    setSignalPayload('')
    setError(null)
    setPollStalled(false)
  }

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

  // Consecutive poll-failure count and the number of upcoming fixed-cadence
  // ticks to skip (a lightweight, bounded backoff) — refs because they drive
  // scheduling decisions inside the interval tick, not rendering.
  const pollFailureCountRef = useRef(0)
  const ticksToSkipRef = useRef(0)

  const pollOnce = useCallback(async (runId: string) => {
    const res = await azoa.workflow.getExecutionState(runId)
    if (isOk(res)) {
      pollFailureCountRef.current = 0
      ticksToSkipRef.current = 0
      setPollStalled(false)
      setExecState(res.value)
      if (isTerminal(res.value.status)) clearPoll()
    } else {
      pollFailureCountRef.current += 1
      if (pollFailureCountRef.current >= MAX_CONSECUTIVE_POLL_FAILURES) {
        // Repeated failures — stop rather than silently freezing at the last
        // known state. The banner below lets the operator resume manually.
        setError(res.error.message)
        setPollStalled(true)
        clearPoll()
      } else {
        // Transient failure: tolerate it and back off before the next actual
        // attempt (skip the next N fixed-cadence ticks) instead of either
        // hammering the endpoint or giving up outright.
        ticksToSkipRef.current = pollFailureCountRef.current
      }
    }
  }, [clearPoll])

  // (Re)start polling only when the run ID changes (or a manual retry bumps
  // pollGeneration) — NOT on every status change, else each poll (which
  // updates status) would tear down and rebuild the interval. pollOnce/clearPoll
  // are stable useCallbacks, so excluding them is safe here. The first poll
  // fires via a zero-delay timeout rather than a direct call, so the effect
  // body never invokes anything that sets state synchronously on its own turn —
  // the same subscription shape as the interval tick below, just a one-shot.
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

  // Manual resume after polling has stalled: clear the failure/backoff state
  // and bump pollGeneration so the effect above re-subscribes for this run.
  const handleResumePolling = useCallback(() => {
    setError(null)
    setPollStalled(false)
    setPollGeneration((g) => g + 1)
  }, [])

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

  // Guarded the same way attachRun is: capture the quest this fetch was issued
  // for, and bail post-await if the quest has since switched. Without this, a
  // fast A→B quest switch can let quest A's listRuns response land after B's
  // and overwrite B's history with A's runs (the render-phase reset above
  // clears `history` synchronously, but that doesn't cancel A's in-flight
  // fetch or stop its resolution from winning the race).
  const loadHistory = useCallback(async () => {
    const forQuestId = quest.id
    const res = await azoa.workflow.listRuns(forQuestId)
    if (questIdRef.current !== forQuestId) return
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
        // Defense in depth: reject a run whose questId doesn't match the quest
        // currently shown. This is what actually stops a contaminated history
        // row (or a stale/tampered localStorage entry) from writing the wrong
        // run id into THIS quest's localStorage key — the questIdRef check
        // above only guards the in-flight-fetch race, not a mismatched result.
        if (res.value.questId !== forQuestId) {
          if (typeof window !== 'undefined') window.localStorage.removeItem(runKey(forQuestId))
          return
        }
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

  // Mount / quest switch: re-attach the persisted run for THIS quest (if any) and
  // hydrate the history list. Transient state itself is reset synchronously during
  // render (the renderedQuestId check above); the ref bookkeeping here is
  // synchronous too (refs, not state), but the two network calls are deferred
  // through a zero-delay timeout — same subscription shape as the poll effect
  // above — so nothing that sets state is invoked directly from the effect body.
  useEffect(() => {
    if (typeof window === 'undefined') return
    questIdRef.current = quest.id // current quest generation — attachRun guards on this
    hydratedForRef.current = quest.id // allow the persist effect to write for this quest
    const stored = window.localStorage.getItem(runKey(quest.id))
    const kickoff = setTimeout(() => {
      if (stored) void attachRun(stored)
      void loadHistory()
    }, 0)
    return () => clearTimeout(kickoff)
    // Keyed on quest.id only — attachRun/loadHistory are stable useCallbacks.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [quest.id])

  const handleStart = async () => {
    setStarting(true)
    setError(null)
    setExecState(null)
    setSelectedNodeId(null)
    setReconcileResult(null)
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
    // Defense in depth: reject a resume-from node id that isn't actually a
    // Succeeded node on the CURRENT quest/run. advanceFromNodeId is reset on
    // quest switch above, but this also guards a run swap (e.g. re-attaching
    // a different run of the same quest) that leaves a picked id pointing at
    // a node that no longer applies — so a stale/unmatched id can never be
    // posted, not just a cross-quest one.
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
    if (!run) return
    setReconciling(true)
    setError(null)
    setReconcileResult(null)
    try {
      const res = await azoa.workflow.reconcileRun(run.id)
      if (isOk(res)) {
        setReconcileResult(res.value)
      } else {
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

      {error && <ErrorBanner message={error} />}

      {pollStalled && run && !isTerminal(run.status) && (
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-amber-300 bg-amber-50 p-2 text-xs text-amber-800 dark:border-amber-900/50 dark:bg-amber-900/10 dark:text-amber-300">
          <span>Live updates stopped after repeated errors — the run may still be progressing on the server.</span>
          <Button size="sm" variant="outline" onClick={handleResumePolling}>
            Retry
          </Button>
        </div>
      )}

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
          {/* Live DAG overlay — click a node to inspect its output/error below.
              React Flow sets `data-id` on node wrappers AND on handles
              (`${rfId}-${nodeId}-${handleId}-${type}`) and edges, so the
              nearest `data-id` ancestor isn't necessarily a real node id —
              e.g. clicking a node's connection handle would otherwise set
              selectedNodeId to a composite string matching no node, silently
              closing the inspector below. Only accept it if it's an actual
              id from this quest's node list. */}
          <div onClick={(e) => {
            const target = (e.target as HTMLElement).closest('[data-id]')
            const nodeId = target?.getAttribute('data-id')
            if (nodeId && quest.nodes.some((n) => n.id === nodeId)) setSelectedNodeId(nodeId)
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
                <Button size="sm" variant="outline" disabled={reconciling} onClick={handleReconcile}>
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

/** One-line caption describing a node type's expected output shape, per the client-side schema mirror. */
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
