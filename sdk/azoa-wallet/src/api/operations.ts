/** D-3 `GET /api/blockchain/operations/{id}/signing-plan`: the exact unsigned bytes the owner's external wallet signs. */
export interface ClientSigningPlanView {
  operationId: string;
  status: string;
  chainType: string;
  network: string;
  protocolIdentity: string;
  format: string;
  signer: string;
  unsignedPayloadBase64: string;
  predictedTransactionId?: string | null;
  validThroughHeight?: number | null;
  nonce?: string | null;
}

/** D-3 `POST /api/blockchain/operations/{id}/submit-signed` body; only the saved plan signed by its signer is accepted. */
export interface ClientSignedSubmission { signedTransactionBase64: string }

/** D-3 operator row: a client-signed operation unsigned past its TTL or parked as `NeedsOperator`. */
export interface StuckClientSignedOperationView {
  operationId: string;
  userId?: string | null;
  status: string;
  createdDate: string;
  chainType: string;
  network: string;
  signer: string;
  transactionId?: string | null;
  nonce?: string | null;
  validThroughHeight?: number | null;
  submitted: boolean;
  detail?: string | null;
}

/** D-3 operator resolution body; the outcome is re-proven from chain state, never taken from this request. */
export interface ClientSigningResolution { reason: string; transactionId?: string }
