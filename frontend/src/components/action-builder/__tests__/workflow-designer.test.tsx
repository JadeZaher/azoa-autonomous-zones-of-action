import { beforeEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { WorkflowDesigner } from '../workflow-designer'
import { INTAKE_FIELDS, type DesignerReply } from '../workflow-designer-types'

const { request } = vi.hoisted(() => ({ request: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api: { request } }, isOk: (value: { ok: boolean }) => value.ok }))

const available = { available: true, provider: 'OpenRouter', model: 'Test model', disclosure: 'Your brief goes to the selected provider. Never submit keys.', tenantId: '3f91074f-b6db-4e81-bdbb-7370c7c6bdae' }
const draftReply: DesignerReply = {
  state: 'DraftReady', reviewQuestions: ['Which tax year should your adviser review?'], limitations: ['Manual/external settlement only.'],
  summary: 'A draft requiring committee review.', questions: [], concerns: ['US tax residence must be confirmed by your adviser.'], assumptions: [],
  readyForDraft: true, draftErrors: [], sources: [{ id: 'irs', title: 'IRS digital assets', url: 'https://www.irs.gov/filing/digital-assets', jurisdiction: 'United States only' }],
  draft: { name: 'Review plan', description: 'Notify a reviewer; no settlement.', isPublic: false, nodes: [
    { name: 'Notify reviewer', nodeType: 'Emit', config: '{"eventType":"review","payload":{}}', isEntry: true, isTerminal: true },
  ], edges: [] },
}

async function openAssistant(onImport = vi.fn(), planning = true) {
  const rendered = render(<WorkflowDesigner onImport={onImport} />)
  fireEvent.click(screen.getByText('Design an economic workflow with AI'))
  await screen.findByText(/AI data sharing: OpenRouter/)
  fireEvent.change(screen.getByLabelText('Tenant context'), { target: { value: available.tenantId } })
  fireEvent.change(screen.getByLabelText('What are you creating?'), { target: { value: 'A cooperative project review.' } })
  fireEvent.click(screen.getByLabelText(/I have identified the relevant countries/))
  fireEvent.click(screen.getByLabelText(/I have specified the economic terms/))
  if (planning) fireEvent.click(screen.getByLabelText(/I want a planning\/data-only graph/))
  return { ...rendered, onImport }
}

function consent() {
  fireEvent.click(screen.getByLabelText(/I am authorized to share this brief/))
}

describe('WorkflowDesigner', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation(async (method: string) => ({ ok: true, value: method === 'GET' ? available : draftReply }))
  })

  it('requires provider disclosure consent before any interview egress', async () => {
    await openAssistant()
    expect(screen.getByRole('button', { name: 'Ask the assistant' })).toBeDisabled()
    expect(request.mock.calls.every(call => call[0] === 'GET')).toBe(true)
    consent()
    expect(screen.getByRole('button', { name: 'Ask the assistant' })).toBeEnabled()
  })

  it('shows follow-up questions and sends answers as user messages without importing', async () => {
    request.mockImplementation(async (method: string) => ({ ok: true, value: method === 'GET' ? available : {
      ...draftReply, questions: ['Which country is the cooperative tax resident in?'], readyForDraft: false, draft: null,
    } }))
    const { onImport } = await openAssistant()
    consent()
    fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant' }))
    await screen.findByText('Which country is the cooperative tax resident in?')
    expect(screen.queryByRole('button', { name: 'Import into visual builder' })).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Answer follow-up questions or challenge an assumption'), { target: { value: 'Colorado, United States; all members are Colorado tax residents.' } })
    fireEvent.click(screen.getByRole('button', { name: 'Continue the interview' }))
    await waitFor(() => expect(request.mock.calls.filter(call => call[0] === 'POST')).toHaveLength(2))
    const payload = request.mock.calls.filter(call => call[0] === 'POST')[1][2]
    expect(payload.messages.at(-1)).toEqual({ role: 'user', content: 'Colorado, United States; all members are Colorado tax residents.' })
    expect(onImport).not.toHaveBeenCalled()
  })

  it('requires explicit review before draft import and never posts a save or run', async () => {
    const { onImport } = await openAssistant()
    consent()
    fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant' }))
    const importButton = await screen.findByRole('button', { name: 'Import into visual builder' })
    expect(importButton).toBeDisabled()
    expect(screen.getByRole('link', { name: 'IRS digital assets' })).toHaveAttribute('href', 'https://www.irs.gov/filing/digital-assets')
    expect(screen.getByText(/not an automated economic workflow/)).toBeInTheDocument()
    expect(screen.getByText(/I want a planning\/data-only graph/)).toHaveTextContent('does not implement two-person approval, eligibility, caps, refunds, payouts or settlement')
    expect(screen.getByText(/I want a planning\/data-only graph/)).toHaveTextContent('later explicit run can enqueue webhook notifications')
    expect(screen.getByText(/I reviewed the planning handoff notes/)).toHaveTextContent('requires an active registration; checkpoints do not wait for or prove review or approval')
    fireEvent.click(screen.getByLabelText(/I reviewed the planning handoff notes/))
    fireEvent.click(importButton)
    expect(onImport).toHaveBeenCalledWith(draftReply.draft)
    expect(importButton).toBeDisabled()
    expect(request.mock.calls.filter(call => call[0] === 'POST').map(call => call[1])).toEqual(['/api/action-designer/interview'])
  })

  it('invalidates the reviewed draft when economic terms change', async () => {
    await openAssistant()
    consent()
    fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant' }))
    await screen.findByRole('button', { name: 'Import into visual builder' })
    fireEvent.click(screen.getByLabelText(/I reviewed the planning handoff notes/))
    fireEvent.change(screen.getByLabelText(INTAKE_FIELDS[3][1]), { target: { value: 'Actually this now pays recipients in tokens.' } })
    expect(screen.queryByRole('button', { name: 'Import into visual builder' })).not.toBeInTheDocument()
  })

  it('discards a previous account interview and its late reply', async () => {
    let resolve!: (value: unknown) => void
    request.mockImplementation((method: string) => method === 'GET' ? Promise.resolve({ ok: true, value: available }) : new Promise(done => { resolve = done }))
    const onImport = vi.fn()
    const { unmount } = await openAssistant(onImport)
    consent()
    fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant' }))
    unmount()
    await openAssistant(onImport)
    await act(async () => resolve({ ok: true, value: draftReply }))
    expect(screen.queryByText('A draft requiring committee review.')).not.toBeInTheDocument()
    expect(onImport).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Ask the assistant' })).toBeDisabled()
  })

  it('keeps interview-only scope as the default and sends that explicit choice', async () => {
    await openAssistant(vi.fn(), false)
    expect(screen.getByLabelText(/Continue the interview only/)).toBeChecked()
    consent()
    fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant' }))
    await screen.findByText(draftReply.summary)
    expect(request.mock.calls.find(call => call[0] === 'POST')?.[2].planningScopeAccepted).toBe(false)
    expect(screen.queryByRole('button', { name: 'Import into visual builder' })).not.toBeInTheDocument()
  })

  it('shows professional review questions without blocking a valid planning proposal', async () => {
    await openAssistant()
    consent()
    fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant' }))
    expect(await screen.findByText('Which tax year should your adviser review?')).toBeInTheDocument()
    expect(screen.getByText(/not planning blockers/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Import into visual builder' })).toBeInTheDocument()
  })

  it('hides inconsistent readiness and discards a proposal when scope changes', async () => {
    request.mockImplementation(async (method: string) => ({ ok: true, value: method === 'GET' ? available : { ...draftReply, state: 'NeedsDetails' } }))
    await openAssistant()
    consent()
    fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant' }))
    await screen.findByText(draftReply.summary)
    expect(screen.queryByRole('button', { name: 'Import into visual builder' })).not.toBeInTheDocument()
    request.mockImplementation(async (method: string) => ({ ok: true, value: method === 'GET' ? available : draftReply }))
    fireEvent.click(screen.getByRole('button', { name: 'Continue the interview' }))
    await screen.findByRole('button', { name: 'Import into visual builder' })
    fireEvent.click(screen.getByLabelText(/Continue the interview only/))
    expect(screen.queryByRole('button', { name: 'Import into visual builder' })).not.toBeInTheDocument()
  })

  it('makes provider refusal actionable while keeping the normal canvas path available', async () => {
    request.mockImplementation(async (method: string) => method === 'GET' ? { ok: true, value: available } : { ok: false, error: { status: 429, message: 'Rate limited' } })
    await openAssistant()
    consent()
    fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('request budget is used')
    expect(screen.queryByRole('button', { name: 'Import into visual builder' })).not.toBeInTheDocument()
  })
})
