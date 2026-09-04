import { fireEvent, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { addNodeFromPalette, flowNodeByLabel, renderQuestCanvas } from '@/test/quest-canvas-harness'

/**
 * C6 regression: the badge used to open its popover on `onMouseEnter` and close it on
 * `onMouseLeave`. The popover is PORTALLED and positioned with a `sideOffset` gap, so
 * moving the pointer toward it left the trigger first and unmounted the popup — its
 * text could never be hovered, scrolled or selected. Worse, hover left `open === true`,
 * so the trigger's own click toggle could then only ever CLOSE it. It is click-only now.
 * See `../AGENTS.md` §warning-badge.
 */

/** A graph whose second node the pre-check has a finding for (unmarked root, no incoming edge). */
function renderCanvasWithAFlaggedNode(): void {
  renderQuestCanvas()
  addNodeFromPalette('Create Holon')
  addNodeFromPalette('Get Holon')
}

const warningBadge = () => screen.getAllByLabelText(/pre-check finding/)[0]

/**
 * The portalled popup while it is OPEN, else null. Scoped to `[data-slot=popover-content]`
 * so the footer warning list never matches, and gated on `[data-open]` because Base UI
 * keeps a closing popup mounted for its exit animation.
 */
const popoverContent = () =>
  document.querySelector<HTMLElement>('[data-slot="popover-content"][data-open]')

describe('node warning badge', () => {
  it('shows a badge on a node the pre-check has findings for', () => {
    renderCanvasWithAFlaggedNode()

    expect(flowNodeByLabel('Get Holon').querySelector('[aria-label*="pre-check"]')).not.toBeNull()
  })

  it('opens the findings popover on click', () => {
    renderCanvasWithAFlaggedNode()

    fireEvent.click(warningBadge())

    const popup = popoverContent()
    expect(popup).not.toBeNull()
    expect(within(popup!).getAllByRole('listitem').length).toBeGreaterThan(0)
  })

  it('keeps the popover open while the pointer moves off the trigger toward it', () => {
    renderCanvasWithAFlaggedNode()
    const badge = warningBadge()
    fireEvent.click(badge)
    const finding = within(popoverContent()!).getAllByRole('listitem')[0]

    // Crossing the portal's `sideOffset` gap: leave the trigger, arrive at the popup.
    fireEvent.mouseLeave(badge)
    fireEvent.mouseEnter(finding)

    expect(popoverContent()).not.toBeNull()
    expect(finding).toBeInTheDocument()
  })

  it('closes again on a second click, so the badge is a plain toggle', () => {
    renderCanvasWithAFlaggedNode()

    fireEvent.click(warningBadge())
    expect(popoverContent()).not.toBeNull()
    fireEvent.click(warningBadge())

    expect(popoverContent()).toBeNull()
  })

  it('does not open on hover alone', () => {
    renderCanvasWithAFlaggedNode()

    fireEvent.mouseEnter(warningBadge())

    expect(popoverContent()).toBeNull()
  })

  it('renders no native tooltip that would double up with the popover', () => {
    renderCanvasWithAFlaggedNode()

    expect(warningBadge()).not.toHaveAttribute('title')
  })
})
