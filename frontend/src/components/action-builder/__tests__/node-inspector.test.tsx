import { StrictMode } from 'react'
import type { Node } from '@xyflow/react'
import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { ActionNodeData } from '../action-node'
import { NodeInspector } from '../panels/node-inspector'

function node(id: string): Node<ActionNodeData> {
  return {
    id, type: 'action', position: { x: 0, y: 0 },
    data: { label: id, nodeType: 'Emit', config: '{}', isEntry: true, isTerminal: true },
  }
}

describe('NodeInspector accessible fields', () => {
  it('keeps labels local to each mounted inspector and stable across node changes', async () => {
    const user = userEvent.setup()
    const firstPatch = vi.fn()
    const secondPatch = vi.fn()
    const renderInspectors = (first: Node<ActionNodeData>) => (
      <StrictMode>
        <section aria-label="First node inspector">
          <NodeInspector node={first} onPatch={firstPatch} onDelete={vi.fn()} />
        </section>
        <section aria-label="Second node inspector">
          <NodeInspector node={node('Second')} onPatch={secondPatch} onDelete={vi.fn()} />
        </section>
      </StrictMode>
    )
    const view = render(renderInspectors(node('First')))
    const first = within(screen.getByRole('region', { name: 'First node inspector' }))
    const second = within(screen.getByRole('region', { name: 'Second node inspector' }))
    const firstName = first.getByRole('textbox', { name: 'Name' })
    const firstConfig = first.getByRole('textbox', { name: 'Config (JSON)' })
    const secondName = second.getByRole('textbox', { name: 'Name' })
    const secondConfig = second.getByRole('textbox', { name: 'Config (JSON)' })
    const fields = [firstName, firstConfig, secondName, secondConfig]
    const originalIds = fields.map(field => field.id)
    expect(new Set(originalIds).size).toBe(4)

    await user.click(first.getByText('Name', { exact: true }))
    expect(firstName).toHaveFocus()
    await user.click(second.getByText('Config (JSON)', { exact: true }))
    expect(secondConfig).toHaveFocus()
    fireEvent.change(firstName, { target: { value: 'First edited' } })
    fireEvent.change(secondConfig, { target: { value: '{"eventType":"review"}' } })
    expect(firstPatch).toHaveBeenCalledExactlyOnceWith({ label: 'First edited' })
    expect(secondPatch).toHaveBeenCalledExactlyOnceWith({ config: '{"eventType":"review"}' })

    view.rerender(renderInspectors(node('Replacement')))
    expect(first.getByRole('textbox', { name: 'Name' })).toHaveValue('Replacement')
    expect([
      first.getByRole('textbox', { name: 'Name' }), first.getByRole('textbox', { name: 'Config (JSON)' }),
      second.getByRole('textbox', { name: 'Name' }), second.getByRole('textbox', { name: 'Config (JSON)' }),
    ].map(field => field.id)).toEqual(originalIds)
  })
})
