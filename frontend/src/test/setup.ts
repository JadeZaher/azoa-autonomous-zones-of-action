import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

/**
 * Global jsdom setup for every test file. jsdom implements neither the layout
 * engine nor the observer APIs React Flow measures nodes with, so a bare jsdom
 * environment cannot mount `<ReactFlow>` at all — everything below closes that
 * gap. See `src/components/quest-builder/AGENTS.md` §testing.
 */

/** jsdom ships no ResizeObserver; React Flow constructs one per canvas to measure node cards. */
class NoopResizeObserver implements ResizeObserver {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}

/**
 * `@xyflow/system` reads the viewport zoom out of `new DOMMatrixReadOnly(style.transform)`.
 * Only `m22` (the y-scale) is ever consumed, and jsdom's computed `transform` is
 * always `''` in these tests, so an identity matrix is the honest answer.
 */
class IdentityDomMatrixReadOnly {
  readonly m11 = 1
  readonly m12 = 0
  readonly m21 = 0
  readonly m22 = 1
  readonly m41 = 0
  readonly m42 = 0
  readonly a = 1
  readonly b = 0
  readonly c = 0
  readonly d = 1
  readonly e = 0
  readonly f = 0
}

/** A non-zero box for every element: React Flow refuses to lay out a zero-sized container. */
const DEFAULT_ELEMENT_BOX = { width: 800, height: 640 }

function installJsdomStubs(): void {
  const globalWithStubs = globalThis as unknown as Record<string, unknown>

  if (typeof globalWithStubs.ResizeObserver === 'undefined') {
    globalWithStubs.ResizeObserver = NoopResizeObserver
  }
  if (typeof globalWithStubs.DOMMatrixReadOnly === 'undefined') {
    globalWithStubs.DOMMatrixReadOnly = IdentityDomMatrixReadOnly
  }
  if (typeof globalWithStubs.DOMMatrix === 'undefined') {
    globalWithStubs.DOMMatrix = IdentityDomMatrixReadOnly
  }

  // jsdom only provides rAF when `pretendToBeVisual` is on. Install a macrotask-based
  // fallback rather than a synchronous one: React Flow schedules focus/blur work through
  // rAF, and running that inline would re-enter React mid-commit.
  if (typeof globalWithStubs.requestAnimationFrame === 'undefined') {
    globalWithStubs.requestAnimationFrame = (callback: FrameRequestCallback) =>
      setTimeout(() => callback(performance.now()), 0) as unknown as number
    globalWithStubs.cancelAnimationFrame = (handle: number) => clearTimeout(handle)
  }

  if (typeof window.matchMedia === 'undefined') {
    window.matchMedia = (query: string) =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => false,
      }) as MediaQueryList
  }

  // jsdom implements no Web Animations API; Base UI's ScrollArea (the node palette)
  // polls `getAnimations()` on a timer and throws asynchronously without it.
  if (typeof Element.prototype.getAnimations === 'undefined') {
    Element.prototype.getAnimations = () => []
  }

  // jsdom has no layout, so every rect is 0x0 and React Flow logs
  // "the parent container needs a width and a height".
  Element.prototype.getBoundingClientRect = function getBoundingClientRectStub(this: Element): DOMRect {
    const { width, height } = DEFAULT_ELEMENT_BOX
    return {
      x: 0,
      y: 0,
      top: 0,
      left: 0,
      bottom: height,
      right: width,
      width,
      height,
      toJSON: () => ({}),
    } as DOMRect
  }
  Object.defineProperty(HTMLElement.prototype, 'offsetWidth', {
    configurable: true,
    get: () => DEFAULT_ELEMENT_BOX.width,
  })
  Object.defineProperty(HTMLElement.prototype, 'offsetHeight', {
    configurable: true,
    get: () => DEFAULT_ELEMENT_BOX.height,
  })
}

installJsdomStubs()

afterEach(() => {
  cleanup()
})
