import type { WorkflowDesignerAvailability, WorkflowDesignerDraft, WorkflowDesignerIntake, WorkflowDesignerMessage, WorkflowDesignerReply } from 'azoa-sdk'
import type { ActionPreset } from './presets'

export const INTAKE_FIELDS = [
  ['purpose', 'What are you creating?', 'Describe the business model and the event that earns or releases value.'],
  ['participants', 'Who takes part?', 'Payers, recipients, operator, approvers, entity types, workers and any minors.'],
  ['jurisdictions', 'Where does it operate?', 'Countries/states of the operator and participants, tax residence and any unknown locations.'],
  ['valueMovement', 'How does value move?', 'Assets/currencies, units, exact amounts, fees, custody and who controls funds.'],
  ['allocationsAndTax', 'How are proceeds and taxes handled?', 'Each allocation, fee payer, records of income/valuation/basis, withholding/reporting responsibilities.'],
  ['failureAndRefunds', 'What happens when things go wrong?', 'Release approval, cancellation, disputes, expiry, partial completion, failed payments and refunds.'],
] as const

export type WorkflowIntake = WorkflowDesignerIntake
export type DesignerMessage = WorkflowDesignerMessage
export type DesignerAvailability = WorkflowDesignerAvailability
export type DesignerDraft = WorkflowDesignerDraft
export type DesignerReply = WorkflowDesignerReply

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
      condition: edge.condition ?? undefined,
    })),
  }
}
