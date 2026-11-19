export type WorkflowDesignerState = 'NeedsDetails' | 'NeedsConfirmation' | 'NeedsPlanningScope' | 'NeedsPlanningStructure' | 'DraftRejected' | 'DraftReady';

export interface WorkflowDesignerIntake {
  purpose: string;
  participants: string;
  jurisdictions: string;
  valueMovement: string;
  allocationsAndTax: string;
  failureAndRefunds: string;
}

export interface WorkflowDesignerMessage { role: 'user' | 'assistant'; content: string }
export interface WorkflowDesignerRequest {
  tenantId: string;
  consentToProvider: boolean;
  jurisdictionsConfirmed: boolean;
  economicTermsConfirmed: boolean;
  planningScopeAccepted: boolean;
  intake: WorkflowDesignerIntake;
  messages: WorkflowDesignerMessage[];
  confirmedResourceHandles?: string[];
}

export interface WorkflowDesignerDraft {
  name: string;
  description?: string | null;
  isPublic: boolean;
  nodes: Array<{ name: string; nodeType: string; config: string; isEntry: boolean; isTerminal: boolean }>;
  edges: Array<{ sourceNodeId: number; targetNodeId: number; edgeType: string; condition?: string | null }>;
}

export interface WorkflowDecisionCriterion {
  id: string;
  stage: string;
  gateQuestion: string;
  requiredEvidence: string;
  onFailure: string;
}

export interface WorkflowDecisionModel {
  policyId: string;
  title: string;
  riskTier: string;
  quorumRule: string;
  fallbackPolicy: string;
  stopConditions: string[];
  criteria: WorkflowDecisionCriterion[];
}

export interface WorkflowAgentSpec {
  agentId: string;
  role: string;
  stage: string;
  nodeName: string;
  mandate: string;
  checks: string[];
  vetoTriggers: string[];
}

export interface WorkflowDesignerReply {
  state: WorkflowDesignerState;
  summary: string;
  questions: string[];
  concerns: string[];
  assumptions: string[];
  reviewQuestions: string[];
  limitations: string[];
  readyForDraft: boolean;
  draft: WorkflowDesignerDraft | null;
  draftErrors: string[];
  sources: Array<{ id: string; title: string; url: string; jurisdiction: string }>;
  decisionModel?: WorkflowDecisionModel | null;
  criticAgent?: WorkflowAgentSpec | null;
  auditorAgent?: WorkflowAgentSpec | null;
}

export interface WorkflowDesignerAvailability {
  available: boolean;
  provider: string;
  model: string;
  disclosure: string;
  tenantId?: string | null;
}

/** Recognizes a server-ready private proposal; scope consent is still required at the UI. */
export function hasWorkflowPlanningDraft(reply: WorkflowDesignerReply): reply is WorkflowDesignerReply & { draft: WorkflowDesignerDraft } {
  return reply.state === 'DraftReady' && reply.readyForDraft === true && reply.draft !== null && reply.draft.isPublic === false
    && reply.draft.nodes.length > 0 && reply.draft.nodes.every(node => node.nodeType === 'Emit');
}
