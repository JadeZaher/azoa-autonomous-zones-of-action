export interface WebhookRegistrationCommand {
  requestId: string;
  registrationId: string;
  expectedVersion: number;
  url: string;
  isActive: boolean;
  rotateSecret: boolean;
  reason: string;
  replaceLegacy?: boolean;
  legacyFingerprint?: string | null;
}

export interface WebhookRegistrationStatus {
  registrationId: string;
  tenantId: string;
  version: number;
  destinationRevision: number;
  secretRevision: number;
  url: string;
  isActive: boolean;
  createdAt: string;
  secretRotatedAt?: string | null;
  legacyFingerprint?: string | null;
}

export interface WebhookRegistrationReceipt {
  requestId: string;
  registrationId: string;
  tenantId: string;
  version: number;
  destinationRevision: number;
  secretRevision: number;
  url: string;
  isActive: boolean;
  recordedAt: string;
}

export interface WebhookRegistrationMutationResult {
  receipt: WebhookRegistrationReceipt;
  newSecret?: string | null;
  replayed: boolean;
}

export interface WebhookRegistrationWithdrawal {
  requestId: string;
  alreadyApplied: boolean;
  receipt?: WebhookRegistrationReceipt | null;
}

export interface WebhookEventStatus {
  eventId: string;
  eventType: string;
  runId: string;
  nodeId: string;
  status: string;
  version: number;
  attemptCount: number;
  cycleAttemptCount: number;
  retryGeneration: number;
  occurredAt: string;
  nextAttemptAt: string;
  errorCode?: string | null;
  registrationId?: string | null;
  destinationRevision?: number | null;
  attemptInFlight: boolean;
  /** Same classifier the server's retry uses: offer Retry only when this is true. */
  retryable: boolean;
}

export interface WebhookEventRetryCommand {
  requestId: string;
  expectedVersion: number;
  reason: string;
}

export interface ConsentWebhookEventStatus extends Omit<WebhookEventStatus, "runId" | "nodeId"> {
  grantId: string;
  userId: string;
}

export interface WebhookEventRetryReceipt {
  requestId: string;
  eventId: string;
  tenantId: string;
  version: number;
  retryGeneration: number;
  recordedAt: string;
  /** Undelivered state the cycle restarted from; absent on pre-round-4 receipts. */
  retriedFrom?: WebhookRetriedFrom | null;
}

export type WebhookRetriedFrom = "DeadLettered" | "AttemptBudgetExhausted" | "AbandonedAttempt";
