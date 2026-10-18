/** Mirrors `KycStatus` (Models/Kyc/KycEnums.cs); string-serialized as written. */
export type KycStatus = "PENDING" | "IN_REVIEW" | "APPROVED" | "REJECTED" | "EXPIRED";

/** Mirrors `KycProvider`. */
export type KycProviderKind = "MANUAL" | "VERIFF" | "GENERIC_HOSTED" | "IDENTOMAT" | "UNAVAILABLE";

/** `POST /api/kyc/session`: the hosted verification to send the person to. */
export interface KycSessionStart {
  provider: KycProviderKind;
  providerKey: string;
  hostedVerification: boolean;
  acceptsDocumentReferences: boolean;
  developmentSimulation: boolean;
  providerSessionId?: string | null;
  verificationUrl?: string | null;
  expiresAt?: string | null;
  instructions?: string | null;
}

/** `GET /api/kyc/status` and `POST /api/kyc/refresh`: the caller's own verification. */
export interface KycSubmission {
  id: string;
  userId: string;
  tenantId?: string | null;
  provider: KycProviderKind;
  providerKey: string;
  status: KycStatus;
  rejectionReason?: string | null;
  providerSessionId?: string | null;
  hostedVerification: boolean;
  verificationUrl?: string | null;
  sessionInstructions?: string | null;
  submittedAt: string;
  reviewedAt?: string | null;
  expiresAt?: string | null;
  createdDate: string;
  modifiedDate?: string | null;
}

/** `POST /api/operator/hosted-sessions/{id}/refresh` (Identomat KYC/KYB included). */
export interface HostedSessionRefreshReceipt { sessionId: string; status: string; providerKey: string }

/** `POST /api/wallet/{id}/ownership-challenge`: the exact message the external wallet signs. */
export interface WalletOwnershipChallenge { address: string; chainType: string; nonce: string; message: string; expiresAt: string }

/** `POST /api/wallet/{id}/ownership-proof` result. */
export interface WalletOwnershipProof { userId: string; walletId: string; chainType: string; address: string; provenAt: string }
