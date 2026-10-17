export type ConsentOrigin = "UserExplicit" | "Participation";

export interface ConsentGrantCommand {
  requestId: string;
  tenantId: string;
  scopes: string[];
  origin?: ConsentOrigin;
  participationRef?: string | null;
  expiresAt?: string | null;
}

export interface ConsentGrantView {
  grantId: string;
  tenantId: string;
  scopes: string[];
  origin: ConsentOrigin;
  participationRef?: string | null;
  grantedAt: string;
  expiresAt?: string | null;
  revokedAt?: string | null;
}

/** Original admission receipt; read the grant separately for current revocation state. */
export interface ConsentGrantReceipt extends Omit<ConsentGrantView, "revokedAt"> {
  requestId: string;
  grantorUserId: string;
}

export interface ConsentRevocationReceipt {
  grantId: string;
  revokedAt: string;
  notificationRecorded: boolean;
}

export interface ConsentOffboardReceipt {
  tenantId: string;
  participationRef: string;
  closedAt: string;
  revokedCount: number;
}

export interface ConsentCommandWithdrawal {
  requestId: string;
  withdrawn: boolean;
  receipt: ConsentGrantReceipt | null;
}
