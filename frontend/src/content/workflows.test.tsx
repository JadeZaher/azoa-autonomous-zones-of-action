import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Prose } from '@/components/landing/prose'
import { WORKFLOW_INDEX, WORKFLOWS } from './workflows'

describe('workflow prose', () => {
  it('groups the workflows index by guarantee and avoids citation headings there', () => {
    render(<Prose blocks={WORKFLOW_INDEX.blocks} />)
    expect(screen.getByRole('heading', { name: 'Idempotency & Sagas' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Consent & Access Control' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Audit & Fail-Closed Guards' })).toBeTruthy()
    expect(screen.queryByRole('heading', { name: 'Sources' })).toBeNull()
  })

  it('renders backtick spans cleanly as code elements across all workflow guides', () => {
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
        expect(codes).toContain('page?do=delete')
      }
      if (page.slug === 'reconciliation-over-blind-retry') {
        expect(codes).toContain('AwaitingReconciliation')
      }
      if (page.slug === 'version-pinned-execution') {
        expect(codes).toContain('expectedPublishedHash')
      }
      if (page.slug === 'durable-waits-and-signals') {
        expect(codes).toContain('AwaitingSignal')
      }
      expect(view.container.innerHTML).not.toContain('`')
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
