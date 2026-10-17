export interface DexAsset { id: string; symbol: string; decimals: number; native: boolean }
export interface DexNetwork { chainType: string; network: string; protocolIdentity: string }
export interface DexParticipant { userId: string; walletId: string; address: string }
export interface DexBudget { maximumNetworkFee: string; maximumLockedBalance: string; maximumCleanupFee: string }
export interface DexFeeQuote {
  denomination: string; scheduleVersion: number; flatBaseUnits: string; basisPoints: number;
  amount: string; treasuryAddress: string; policyKeyVersion: string;
}
export interface DexEffectDisclosure {
  kind: string; assetId: string; amount: string; ownerAddress: string; destinationAddress: string;
  retainedOnSwapFailure: boolean;
}
export interface DexQuoteTerms {
  version: number; actorUserId: string; tenantId?: string | null; owner: DexParticipant; payer: DexParticipant;
  recipientAddress: string; network: DexNetwork; inputAsset: DexAsset; outputAsset: DexAsset;
  grossInput: string; swapInput: string; expectedOutput: string; minimumOutput: string; slippageBps: number;
  platformFee: DexFeeQuote; budget: DexBudget; provider: string; protocolVersion: string;
  protocolEvidence: Record<string, unknown>; expiresAt: string; prerequisites: DexEffectDisclosure[];
}
export interface DexQuoteView { quoteId: string; quoteHash: string; terms: DexQuoteTerms }
export interface DexQuoteAvailability { unavailable: boolean; quote?: DexQuoteView | null; reason?: string | null }
export interface DexQuoteRequest {
  walletId: string; network: DexNetwork; inputAsset: DexAsset; outputAsset: DexAsset;
  grossInput: string; slippageBps: number; budget: DexBudget;
}
export interface SwapAdmissionRequest { quoteId: string; quoteHash: string; requestKey: string; acceptEconomicEffects: boolean }
export interface SwapVersionCommand { expectedVersion: number }
export interface SwapStageCommand extends SwapVersionCommand { stageId: string; expectedStageVersion: number }
export interface SwapCleanupStageCommand extends SwapVersionCommand { stage: string }
export interface SwapBrowserStageCommand extends SwapStageCommand { signedPayloadJson: string }
export interface SwapExposureCommand extends SwapStageCommand { acceptPotentialExternalSubmission: boolean }
export interface SwapCleanupRequest extends SwapVersionCommand {
  requestKey: string; assetId: string; maximumNetworkFee: string; acceptCurrentZeroHoldingReplacement: boolean;
}
export interface SwapExternalSigningView {
  operationId: string; stageId: string; operationVersion: number; stageVersion: number; network: DexNetwork;
  planHash: string; exposureHash: string; requiredSignerAddresses: string[]; unsignedPayloadJson: string;
  reservedNetworkFee: string; reservedLockedBalance: string;
}
export interface SwapStageReceipt {
  stageId: string; stage: string; version: number; state: string; purpose: string; transactionIds: string[];
  actualInput?: string | null; actualOutput?: string | null; platformFeePaid?: string | null;
  networkFeePaid?: string | null; lockedBalance?: string | null; submissionAuthorized: boolean;
  externalSigningExposed: boolean; serverSubmissionWithdrawn: boolean;
}
export interface SwapAccountingView {
  complete: boolean; confirmedExecutionNetworkFee: string; confirmedCleanupNetworkFee: string;
  confirmedRentAllocated: string; unresolvedRentReservation: string; unresolvedRentStages: number;
  confirmedRentReleased: string;
}
export interface SwapOperationView {
  operationId: string; quoteId: string; quoteHash: string; state: string; stage: string; version: number;
  network: DexNetwork; transactionIds: string[]; actualInput?: string | null; actualOutput?: string | null;
  retainedEffects: DexEffectDisclosure[]; detail?: string | null; accounting?: SwapAccountingView | null;
  retentionReconciled: boolean; currentStage?: SwapStageReceipt | null;
}
