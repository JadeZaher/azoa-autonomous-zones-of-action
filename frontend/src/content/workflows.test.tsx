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

  it('renders backtick spans as code and leaves the other workflow guides as plain text', () => {
    const pages = [
      { slug: 'index', blocks: WORKFLOW_INDEX.blocks, sources: [] },
      ...WORKFLOWS.map((workflow) => ({
        slug: workflow.slug,
        blocks: workflow.blocks,
        sources: workflow.sources,
      })),
    ]
    for (const page of pages) {
      const view = render(<Prose blocks={page.blocks} sources={page.sources} />)
      const codes = [...view.container.querySelectorAll('code')].map((node) => node.textContent)
      if (page.slug === 'safe-and-idempotent') {
        expect(codes).toEqual(['page?do=delete'])
        expect(view.container.textContent).toContain(
          "The RFC's example is a URI such as page?do=delete reached with GET.",
        )
      } else {
        expect(codes).toEqual([])
      }
      expect(view.container.innerHTML).not.toContain('`')
      view.unmount()
    }
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
