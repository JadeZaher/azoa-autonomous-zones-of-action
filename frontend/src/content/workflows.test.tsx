import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Prose } from '@/components/landing/prose'
import { WORKFLOW_INDEX, WORKFLOWS } from './workflows'

describe('workflow prose', () => {
  it('groups the workflows index cleanly and avoids citation headings there', () => {
    render(<Prose blocks={WORKFLOW_INDEX.blocks} />)
    expect(screen.getByRole('heading', { name: 'Reliable Money & Agreements' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Identity & Permission' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Trust, Waits & Clarity' })).toBeTruthy()
    expect(screen.queryByRole('heading', { name: 'Sources' })).toBeNull()
  })

  it('renders cleanly without unparsed backticks or SDK/enum jargon across all guides', () => {
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
      const text = view.container.textContent ?? ''
      expect(view.container.innerHTML).not.toContain('`')
      expect(text).not.toContain('AwaitingReconciliation')
      expect(text).not.toContain('expectedPublishedHash')
      expect(text).not.toContain('WorkflowClient')
      expect(text).not.toContain('ActionEconomicManifest')
      view.unmount()
    }
  })

  it('renders every child workflow guide cleanly with bottom sources and practical walkthroughs', () => {
    for (const workflow of WORKFLOWS) {
      const view = render(<Prose blocks={workflow.blocks} sources={workflow.sources} />)
      expect(screen.getByRole('heading', { name: 'Sources' })).toBeTruthy()
      expect(screen.getByRole('heading', { name: 'In practice' })).toBeTruthy()
      expect(screen.queryByRole('heading', { name: 'Hero figures' })).toBeNull()
      expect(screen.queryByRole('heading', { name: 'Hero points' })).toBeNull()
      for (const source of workflow.sources) {
        for (const url of source.urls) {
          expect(view.container.querySelector(`a[href="${url}"]`)).toBeTruthy()
        }
      }
      expect(view.container.innerHTML).not.toContain('azoa.io')
      expect(view.container.innerHTML).not.toContain('/patterns')
      expect(view.container.textContent).not.toContain('research pass (403)')
      expect(view.container.textContent).not.toContain('HTTP 500 / failed fetch')
      expect(view.container.textContent).not.toContain('never double-spend')
      view.unmount()
    }
  })
})
