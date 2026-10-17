/** Wormhole Token Transfer journey types mirroring `/api/bridge/wtt`; amounts and sequences are lossless decimal strings. */

export interface WttEndpoint { chainType: string; network: string; walletId: string }
export interface WttChainBudget { maximumNetworkFee: string; maximumLockedDeposit: string; maximumValue: string }

/**
 * The request key fixes the operation identity; reuse it as the start `Idempotency-Key`.
 * `relayerFee` must be omitted or "0": the platform redeems its own transfers. Tenant credentials may only deliver to the
 * user's own custodial wallets on the target chain.
 */
export interface WttQuoteRequest {
  requestKey: string; source: WttEndpoint; tokenAddress: string; nativeAsset: boolean; amount: string; relayerFee?: "0" | null;
  target: WttEndpoint; recipientAddress: string; unwrapNative: boolean; sourceBudget: WttChainBudget; targetBudget: WttChainBudget;
  returnOfOperationId?: string | null;
}

export interface WttDeploymentIdentity { protocolChain: number; genesisHex: string; coreBridgeHex: string; tokenBridgeHex: string }
export interface WttNetworkDescriptor {
  chainType: string; network: string; protocolIdentity: string; environment: string; protocolChain: number;
  coreBridge: string; tokenBridge: string; codeIdentity: string; deploymentIdentity: WttDeploymentIdentity; emitterAddressHex: string;
}
export interface WttAssetDescriptor {
  tokenAddress: string; nativeAsset: boolean; decimals: number; originChain: number; originAddress: string;
  originDecimals?: number | null; symbolHex: string; nameHex: string; behaviorIdentity: string;
}
export interface WttSigningContext {
  userId: string; walletId: string; isPlatform: boolean; actingTenantId: string; grantorUserId: string;
  scope?: string | null; chainType?: string | null;
}
export interface WttSigningAuthority { address: string; context: WttSigningContext }
export interface WttNormalizedAmount { requested: string; debited: string; dust: string; amount: string; fee: string; recipientAmount: string }
export interface WttOperationIntent {
  version: number; operationId: string; actorUserId: string; tenantId?: string | null;
  source: WttNetworkDescriptor; target: WttNetworkDescriptor; asset: WttAssetDescriptor;
  sourceAuthority: WttSigningAuthority; targetAuthority: WttSigningAuthority;
  recipientAddress: string; recipientProtocolAddress: string; targetAssetAddress?: string | null; targetDecimals: number;
  amount: WttNormalizedAmount; nonce: number; consistencyLevel: number; unwrapNative: boolean; requiresAttestation: boolean;
  sourceBudget: WttChainBudget; targetBudget: WttChainBudget;
}
export interface WttStageReservation { stage: string; networkFee: string; lockedDeposit: string; value: string; participantAddress?: string | null }
export interface WttStageQuoteView { quoteId: string; protocolIdentity: string; phase: string; stages: WttStageReservation[]; expiresAt: string }
export interface WttTargetInspection {
  network: WttNetworkDescriptor; assetAddress?: string | null; decimals: number; registered: boolean; recipientReady: boolean;
  recipientProtocolAddress: string;
}
export interface WttOfferView {
  offerId: string; offerHash: string; operationId: string; intent: WttOperationIntent; sourceQuote: WttStageQuoteView;
  target: WttTargetInspection; expiresAt: string; returnOfOperationId?: string | null;
}

export interface WttStartRequest { offerId: string; offerHash: string; acceptEconomicEffects: boolean }
export interface WttCancelRequest { offerId: string; offerHash: string }
export interface WttContinueCommand { expectedVersion: number; authorizeSubmission: boolean }
export interface WttStageCommand { expectedVersion: number; stageId: string; expectedStageVersion: number }
export interface WttParticipantSignatureCommand extends WttStageCommand { signedTransactionBase64: string; authorizeSubmission: boolean }
export interface WttNativeOutputRequest {
  expectedVersion: number; requestKey: string; maximumNetworkFee: string; maximumTemporaryRent: string; acceptNativeOutput: boolean;
}
export interface WttCleanupRequest { expectedVersion: number; stageId: string; requestKey: string; maximumNetworkFee: string; acceptReturn: boolean }

export type WttPublicState =
  | "Admitted" | "AwaitingSourceAuthorization" | "SourcePrepared" | "SourceSubmitted" | "AwaitingAttestation"
  | "AwaitingTargetParticipant" | "AwaitingTargetAuthorization" | "TargetPrepared" | "TargetSubmitted" | "Settled"
  | "FailedNoEffect" | "CancelledBeforeEffect" | "NeedsOperator";
export type WttNextAction =
  | "Continue" | "Submit" | "SignParticipant" | "AwaitFinality" | "AwaitProof" | "StartNativeOutput" | "Operator" | "None";

export interface WttStageView {
  stageId: string; phase: string; stage: string; generation: number; version: number; state: string;
  reservation?: WttStageReservation | null; transactionId?: string | null; networkFeePaid?: string | null; detail?: string | null;
  submissionAuthorized: boolean; exposed: boolean; participantAddress?: string | null;
}
export interface WttSettlement {
  version: number; targetTransactionId: string; targetProtocolIdentity: string; bodyDigest: string; recipientAddress: string;
  recipientAmount: string; redemptionEvidenceJson: string; nativeOutputTransactionId?: string | null; nativeAmountReceived?: string | null;
}
/** Submission alone never settles: only `Settled` carries finalized target evidence. */
export interface WttOperationView {
  operationId: string; offerId: string; version: number; state: string; publicState: WttPublicState; phase: string;
  intent: WttOperationIntent; stages: WttStageView[]; currentStageId?: string | null; transferSequence?: string | null;
  attestationSequence?: string | null; proofVerified: boolean; nextAction: WttNextAction; detail?: string | null;
  settlement?: WttSettlement | null; returnOfOperationId?: string | null; createdAt: string; updatedAt: string;
}
export interface WttOperationPage { items: WttOperationView[]; nextCursor?: string | null }
/** Returned only after the server durably records exposure; sign it unchanged in the participant's wallet. */
export interface WttParticipantSigningView {
  operationId: string; stageId: string; operationVersion: number; stageVersion: number; participantAddress: string;
  protocolIdentity: string; unsignedTransactionBase64: string; reservation: WttStageReservation;
}
export interface WttNativeOutputOffer { nativeAmount: string; networkFee: string; temporaryRent: string; recipientAddress: string; expiresAt: string }
export interface WttCleanupOffer {
  stageId: string; returnAmount: string; originalAttributedDepositReturn: string; networkFee: string; destinationAddress: string; expiresAt: string;
}
