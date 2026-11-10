import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Prose } from '@/components/landing/prose'
import { SIMULATION_SENTENCE, WORKFLOW_INDEX, WORKFLOWS } from './workflows'

describe('workflow prose', () => {
  it('renders the simulation sentence in the index body', () => {
    render(<Prose blocks={WORKFLOW_INDEX.blocks} />)
    expect(screen.getByText(SIMULATION_SENTENCE)).toBeTruthy()
    expect(screen.queryByRole('heading', { name: 'Sources' })).toBeNull()
  })

  it('renders each child body with sources and an illustrative scenario', () => {
    for (const workflow of WORKFLOWS) {
      const view = render(<Prose blocks={workflow.blocks} sources={workflow.sources} />)
      expect(screen.getByText(SIMULATION_SENTENCE)).toBeTruthy()
      expect(screen.getByRole('heading', { name: 'Sources' })).toBeTruthy()
      expect(screen.getByRole('heading', { name: 'A situation like this' })).toBeTruthy()
      expect(screen.queryByRole('heading', { name: 'An illustrative scenario' })).toBeNull()
      expect(screen.queryByRole('heading', { name: 'Hero figures' })).toBeNull()
      expect(screen.queryByRole('heading', { name: 'Hero points' })).toBeNull()
      expect(screen.queryByText('Every stat on this page is cited.')).toBeNull()
      for (const source of workflow.sources) {
        for (const url of source.urls) {
          expect(view.container.querySelector(`a[href="${url}"]`)).toBeTruthy()
        }
      }
      expect(view.container.innerHTML).not.toContain('azoa.io')
      expect(view.container.innerHTML).not.toContain('/patterns')
      view.unmount()
    }
  })
})
