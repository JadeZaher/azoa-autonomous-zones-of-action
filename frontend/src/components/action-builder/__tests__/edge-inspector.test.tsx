import { StrictMode, useState } from 'react'
import type { Edge } from '@xyflow/react'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { EdgeInspector } from '../panels/edge-inspector'

function EditableConditionalEdge() {
  const [edge, setEdge] = useState<Edge>({
    id: 'conditional-edge', source: 'request', target: 'review',
    data: { edgeType: 'Conditional', condition: '' },
  })
  return (
    <EdgeInspector
      edge={edge}
      onPatch={patch => setEdge(current => ({ ...current, data: { ...current.data, ...patch } }))}
      onDelete={() => undefined}
    />
  )
}

describe('EdgeInspector accessible condition', () => {
  it('focuses and edits the condition through its visible label', async () => {
    const user = userEvent.setup()
    render(<StrictMode><EditableConditionalEdge /></StrictMode>)
    const condition = screen.getByRole('textbox', { name: 'Condition' })
    expect(screen.getByText('required', { exact: true })).toBeInTheDocument()

    await user.click(screen.getByText('Condition', { exact: true }))
    expect(condition).toHaveFocus()
    await user.type(condition, 'output.ok == true')

    expect(condition).toHaveValue('output.ok == true')
    expect(screen.queryByText('required', { exact: true })).not.toBeInTheDocument()
  })
})
