import type { BuiltGraph } from './graph-serialization'
import type { ActionPreset } from './presets'

export const INTAKE_FIELDS = [
  ['purpose', 'What are you creating?', 'Describe the business model and the event that earns or releases value.'],
  ['participants', 'Who takes part?', 'Payers, recipients, operator, approvers, entity types, workers and any minors.'],
  ['jurisdictions', 'Where does it operate?', 'Countries/states of the operator and participants, tax residence and any unknown locations.'],
  ['valueMovement', 'How does value move?', 'Assets/currencies, units, exact amounts, fees, custody and who controls funds.'],
  ['allocationsAndTax', 'How are proceeds and taxes handled?', 'Each allocation, fee payer, records of income/valuation/basis, withholding/reporting responsibilities.'],
  ['failureAndRefunds', 'What happens when things go wrong?', 'Release approval, cancellation, disputes, expiry, partial completion, failed payments and refunds.'],
] as const

export type WorkflowIntake = Record<(typeof INTAKE_FIELDS)[number][0], string>
export interface DesignerMessage { role: 'user' | 'assistant'; content: string }
export interface DesignerAvailability { available: boolean; provider: string; model: string; disclosure: string; tenantId: string }
export interface DesignerDraft extends BuiltGraph { name: string; description?: string; isPublic: boolean }
export interface DesignerReply {
  summary: string
  questions: string[]
  concerns: string[]
  assumptions: string[]
  readyForDraft: boolean
  draft: DesignerDraft | null
  draftErrors: string[]
  sources: Array<{ id: string; title: string; url: string; jurisdiction: string }>
}

/** Convert reviewed server draft to the existing canvas preset contract. */
export function designerDraftPreset(draft: DesignerDraft): ActionPreset {
  return {
    id: 'ai-draft', name: draft.name, description: draft.description ?? '',
    nodes: draft.nodes.map((node, index) => ({
      key: String(index), label: node.name, nodeType: node.nodeType, config: node.config,
      isEntry: node.isEntry, isTerminal: node.isTerminal,
    })),
    edges: draft.edges.map(edge => ({
      from: String(edge.sourceNodeId), to: String(edge.targetNodeId), edgeType: edge.edgeType,
      condition: edge.condition,
    })),
  }
}
