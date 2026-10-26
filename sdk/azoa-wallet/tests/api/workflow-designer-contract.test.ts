import { describe, expect, it } from 'vitest'
import { hasWorkflowPlanningDraft, type WorkflowDesignerReply } from '../../src/api/workflow-designer.js'

function proposal(): WorkflowDesignerReply {
  return {
    state: 'DraftReady', summary: 'Planning notes only.', questions: [], concerns: [], assumptions: [],
    reviewQuestions: ['Ask a qualified adviser about the relevant tax year.'], limitations: ['No funds movement.'],
    readyForDraft: true, draftErrors: [], sources: [],
    draft: { name: 'Planning notes', isPublic: false, nodes: [{ name: 'Record handoff note', nodeType: 'Emit', config: '{}', isEntry: true, isTerminal: true }], edges: [] },
  }
}

describe('workflow designer readiness contract', () => {
  it('recognizes a private planning proposal while professional review remains outstanding', () => {
    expect(hasWorkflowPlanningDraft(proposal())).toBe(true)
  })

  it('refuses inconsistent state, missing graph, public graphs and economic nodes', () => {
    expect(hasWorkflowPlanningDraft({ ...proposal(), state: 'NeedsDetails' })).toBe(false)
    expect(hasWorkflowPlanningDraft({ ...proposal(), readyForDraft: false })).toBe(false)
    expect(hasWorkflowPlanningDraft({ ...proposal(), draft: null })).toBe(false)
    const publicDraft = proposal(); publicDraft.draft!.isPublic = true
    expect(hasWorkflowPlanningDraft(publicDraft)).toBe(false)
    const economicDraft = proposal(); economicDraft.draft!.nodes[0].nodeType = 'Transfer'
    expect(hasWorkflowPlanningDraft(economicDraft)).toBe(false)
    const emptyDraft = proposal(); emptyDraft.draft!.nodes = []
    expect(hasWorkflowPlanningDraft(emptyDraft)).toBe(false)
  })
})
