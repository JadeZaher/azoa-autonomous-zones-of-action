import { describe, expect, it } from 'vitest'

/**
 * Contract test for `setup.ts`. React Flow cannot mount without these, and a
 * missing stub otherwise surfaces as an unrelated-looking failure deep inside
 * `@xyflow/system`.
 */
describe('jsdom test environment', () => {
  it('provides the observer and geometry APIs React Flow measures with', () => {
    expect(typeof globalThis.ResizeObserver).toBe('function')
    expect(typeof globalThis.DOMMatrixReadOnly).toBe('function')
    expect(typeof globalThis.requestAnimationFrame).toBe('function')
    expect(new globalThis.DOMMatrixReadOnly().m22).toBe(1)
  })

  it('gives elements a non-zero box so the canvas is not laid out at 0x0', () => {
    const element = document.createElement('div')
    document.body.appendChild(element)
    expect(element.getBoundingClientRect().width).toBeGreaterThan(0)
    expect(element.getBoundingClientRect().height).toBeGreaterThan(0)
  })
})
