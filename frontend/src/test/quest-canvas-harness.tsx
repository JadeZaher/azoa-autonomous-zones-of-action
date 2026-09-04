import { Profiler, type ProfilerOnRenderCallback, type ReactElement } from 'react'
import { act, fireEvent, render, screen, type RenderResult } from '@testing-library/react'
import { QuestCanvas, type BuiltGraph, type NodeTemplateMeta } from '@/components/quest-builder/quest-canvas'

/**
 * Shared harness for mounting the quest builder in jsdom. Everything here drives
 * the component the way a user would (palette buttons, node clicks, real key
 * events) so a test can never assert on a state shape the UI does not expose.
 * See `src/components/quest-builder/AGENTS.md` §testing.
 */

/**
 * A render loop does not fail a test on its own — it pins a CPU and the runner
 * hangs until the worker is killed, which is exactly how C1 escaped review. Trip
 * a loud, immediate failure instead.
 */
const COMMIT_LIMIT = 200

export interface QuestCanvasHarness extends RenderResult {
  /** Commits of the whole `QuestCanvas` subtree since mount — the render-loop tripwire. */
  commitCount: () => number
  resetCommitCount: () => void
  /** Graphs handed to `onSubmit`, newest last. */
  submittedGraphs: BuiltGraph[]
}

export function renderQuestCanvas(
  options: { nodeTemplates?: NodeTemplateMeta[]; submitLabel?: string } = {},
): QuestCanvasHarness {
  const submittedGraphs: BuiltGraph[] = []
  let commits = 0
  let totalCommits = 0
  const countCommit: ProfilerOnRenderCallback = () => {
    commits += 1
    totalCommits += 1
    if (totalCommits > COMMIT_LIMIT) {
      throw new Error(
        `QuestCanvas re-rendered more than ${COMMIT_LIMIT} times — treat this as a render loop, not a slow test.`,
      )
    }
  }

  const utils = render(
    <Profiler id="quest-canvas" onRender={countCommit}>
      <QuestCanvas
        nodeTemplates={options.nodeTemplates ?? []}
        onSubmit={(graph) => submittedGraphs.push(graph)}
        submitLabel={options.submitLabel}
      />
    </Profiler>,
  )

  return {
    ...utils,
    commitCount: () => commits,
    resetCommitCount: () => {
      commits = 0
    },
    submittedGraphs,
  }
}

/** Mount an arbitrary tree with the same Profiler-based commit counter. */
export function renderWithCommitCount(element: ReactElement): RenderResult & { commitCount: () => number } {
  let commits = 0
  const utils = render(
    <Profiler id="probe" onRender={() => { commits += 1 }}>
      {element}
    </Profiler>,
  )
  return { ...utils, commitCount: () => commits }
}

// ── Reading the canvas ────────────────────────────────────────────────────────

/** Every node card React Flow currently has on the canvas. */
export function flowNodeElements(): HTMLElement[] {
  return Array.from(document.querySelectorAll<HTMLElement>('.react-flow__node'))
}

/** The node card whose label text matches, e.g. `flowNodeByLabel('Create Holon')`. */
export function flowNodeByLabel(label: string): HTMLElement {
  const match = flowNodeElements().find((el) => el.textContent?.includes(label))
  if (!match) {
    throw new Error(
      `No canvas node labelled "${label}". Present: ${flowNodeElements()
        .map((el) => el.textContent)
        .join(' | ')}`,
    )
  }
  return match
}

/** True when React Flow considers the node selected (its own `.selected` class). */
export function isNodeSelected(label: string): boolean {
  return flowNodeByLabel(label).classList.contains('selected')
}

/**
 * The `N nodes · M edges` readout in the inspector footer. This is the only place
 * the UI reports edge count, and it is read straight off graph state — so it stays
 * truthful even when jsdom's missing layout engine keeps edges out of the DOM.
 */
export function graphCounts(): { nodes: number; edges: number } {
  const spans = Array.from(document.querySelectorAll('span'))
  const readout = spans.find((el) => /^\s*\d+ nodes .* \d+ edges\s*$/.test(el.textContent ?? ''))
  if (!readout) throw new Error('Graph counts readout not found in the inspector footer.')
  const [nodes, edges] = (readout.textContent ?? '').match(/\d+/g)!.map(Number)
  return { nodes, edges }
}

// ── Driving the canvas ────────────────────────────────────────────────────────

/** Add a node by clicking its palette row, exactly as a user would. */
export function addNodeFromPalette(label: string): void {
  fireEvent.click(screen.getByRole('option', { name: new RegExp(`^${label}`) }))
}

/**
 * Click a node card. Routed through `fireEvent` rather than `user-event` on
 * purpose: jsdom never measures the card, so React Flow leaves it
 * `pointer-events: none` and user-event would (correctly) refuse to click it.
 */
export function clickFlowNode(label: string): void {
  fireEvent.click(flowNodeByLabel(label))
}

/**
 * Hold/release React Flow's multi-select modifier on `window`, where its own
 * `useKeyPress` listens. Exactly ONE modifier may be pressed: `isMatchingKey`
 * requires the pressed-key set to be the same SIZE as the watched combination,
 * so pressing Meta and Control together matches neither. jsdom reports a non-Mac
 * user agent, which makes React Flow watch `Control`.
 */
export function pressMultiSelectModifier(down: boolean): void {
  const event = down ? fireEvent.keyDown : fireEvent.keyUp
  event(window, { key: 'Control', code: 'ControlLeft' })
}

/**
 * React Flow's own delete path: Backspace on the document with a selection live.
 * Async because `deleteElements` awaits `onBeforeDelete` before it fires its edge
 * and node change batches — without flushing that microtask inside `act`, an
 * assertion races the delete and sees the pre-delete graph.
 */
export async function pressDeleteKey(): Promise<void> {
  fireEvent.keyDown(document, { key: 'Backspace', code: 'Backspace' })
  await act(async () => {})
  fireEvent.keyUp(document, { key: 'Backspace', code: 'Backspace' })
}

/** Open the Presets dropdown and load one — the cheapest way to get real edges on the canvas. */
export function loadPreset(name: string | RegExp): void {
  fireEvent.click(screen.getByRole('button', { name: /Presets/ }))
  fireEvent.click(screen.getByRole('menuitem', { name }))
}

/** Canvas shortcut (`useCanvasKeyboardShortcuts` listens on `window`). */
export function pressCanvasShortcut(key: string, modifiers: { shiftKey?: boolean } = {}): void {
  fireEvent.keyDown(window, { key, ctrlKey: true, ...modifiers })
}

export function toolbarButton(name: string): HTMLButtonElement {
  return screen.getByRole('button', { name }) as HTMLButtonElement
}

/**
 * Click a toolbar button. Always go through `fireEvent`, never `element.click()` —
 * the raw DOM method is not wrapped in `act()`, so React state updates it triggers
 * are still queued when the next assertion runs.
 */
export function clickToolbarButton(name: string): void {
  fireEvent.click(toolbarButton(name))
}
