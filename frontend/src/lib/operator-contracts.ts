export const OPERATOR_REQUEST_HEADER = "x-azoa-operator-request";
export const KYC_PROVIDER_DISPLAY_NAME_MAX_LENGTH = 80;

export interface OperatorSessionView {
    authenticated: boolean;
    username: string;
    expiresAt: string;
}

export interface OperatorSessionResponse {
    accessToken: string;
    expiresAt: string;
    username: string;
}

export interface AzoaEnvelope<T> {
    isError?: boolean;
    code?: string | null;
    message?: string | null;
    result?: T | null;
}

export interface NodeRuntimeSummary {
    environment: string;
    serviceVersion: string;
    generatedAt: string;
    persistenceReady: boolean;
}

export interface NodeOperatorIdentitySummary {
    username: string;
    credentialRevision: number;
    activatedAt: string;
    credentialUpdatedAt: string;
}

export interface KycControlSummary {
    profileCount: number;
    enabledProfileCount: number;
    readyProfileCount: number;
    pendingSubmissionCount: number;
    configuredTenantCount: number;
}

export interface NodeOperatorOverviewResponse {
    node: NodeRuntimeSummary;
    operator: NodeOperatorIdentitySummary;
    kyc: KycControlSummary;
}

export interface KycProviderProfileResponse {
    providerKey: string;
    displayName: string;
    adapterKey: string;
    enabled: boolean;
    available: boolean;
    apiKeyConfigured: boolean;
    webhookSecretConfigured: boolean;
    readinessCode: string;
    requiredConfigurationKeys: string[];
    missingConfigurationKeys: string[];
    policyVersion: string;
    assuranceLevel: string;
    version: number;
    trustRevision: number;
    updatedAt: string;
}

export interface KycProviderProfileUpdate {
    displayName: string;
    adapterKey: string;
    enabled: boolean;
    policyVersion: string;
    assuranceLevel: string;
    expectedVersion?: number;
}

export interface TenantKycSelectionResponse {
    tenantId: string;
    providerKey?: string | null;
    providerDisplayName?: string | null;
    selectionVersion: number;
    providerEnabled: boolean;
    providerAvailable: boolean;
    readinessCode: string;
    updatedAt?: string | null;
}

export interface TenantKycProviderChoiceResponse {
    providerKey: string;
    displayName: string;
    assuranceLevel: string;
    hostedVerification: boolean;
    acceptsDocumentReferences: boolean;
}

export interface OperatorTenantKycSummaryResponse extends TenantKycSelectionResponse {
    username: string;
}

export interface OperatorKycSubmissionQueueItem {
    id: string;
    avatarId: string;
    tenantId?: string | null;
    providerKey: string;
    status: string;
    submittedAt: string;
    expiresAt: string;
    humanReviewAllowed: boolean;
    reviewMode: "development_simulation" | "external_provider";
}

export type KycControlAuditAction =
    | "profile.trust-change"
    | "profile.metadata-change"
    | "tenant.provider-selection";

export interface KycControlAuditResponse {
    id: string;
    action: string;
    tenantId?: string | null;
    providerKey?: string | null;
    previousProviderKey?: string | null;
    version: number;
    previousDisplayName?: string | null;
    displayName?: string | null;
    previousAdapterKey?: string | null;
    adapterKey?: string | null;
    previousEnabled?: boolean | null;
    enabled?: boolean | null;
    previousPolicyVersion?: string | null;
    policyVersion?: string | null;
    previousAssuranceLevel?: string | null;
    assuranceLevel?: string | null;
    previousTrustRevision?: number | null;
    trustRevision?: number | null;
    actorAvatarId: string;
    occurredAt: string;
}

export interface CursorPage<T> {
    items: T[];
    nextCursor?: string | null;
}

export interface OperatorKycDecisionRequest {
    decision: "approve" | "reject";
    notes?: string;
    reason?: string;
}

export interface OperatorResponse<T> {
    result: T;
}

export function operatorErrorMessage(status: number): string {
    if (status === 400) return "Check the information and try again.";
    if (status === 401)
        return "Your operator session has expired. Sign in again.";
    if (status === 403)
        return "This operator session is not allowed to perform that action.";
    if (status === 409)
        return "The configuration changed elsewhere. Refresh before trying again.";
    if (status === 429)
        return "Too many attempts. Wait a moment before trying again.";
    if (status === 503) return "The node is not ready for this action yet.";
    return "The node could not complete the request. Try again.";
}
