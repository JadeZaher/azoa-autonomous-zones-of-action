export type HoldNetwork = 'Devnet' | 'Testnet' | 'Mainnet';

export interface HoldAssetIdentity {
  chainType: string; network: HoldNetwork; protocolIdentity: string; assetId: string; holdingAddress: string;
}
export interface HoldDefinition {
  asset: HoldAssetIdentity; quantityBaseUnits: string; fundingSource: 'Reserve' | 'Funder';
  beneficiaryUserId: string; funderUserId?: string | null; reservePartition?: string | null;
  releaseNotBefore?: string | null; autoRelease: boolean; purpose?: string | null;
}
export interface HoldCommandIntent {
  version: 1; requestKey: string; kind: 'Open' | 'Release' | 'Refund' | 'ReleaseIntoHold'; holdId: string;
  actorUserId: string; ownerUserId: string; tenantId?: string | null; custodyTenantId?: string | null;
  asset: HoldAssetIdentity; definition?: HoldDefinition | null; nextHoldId?: string | null;
  nextBeneficiaryUserId?: string | null; nextReleaseNotBefore?: string | null; nextAutoRelease?: boolean; reason?: string | null;
}
export interface HoldCostBreakdown {
  payerAddress: string; refundRecipientAddress: string; networkFeeBaseUnits: string;
  lockedBalanceBaseUnits: string; nonrefundableProtocolCostBaseUnits: string;
}
export interface HoldCostQuote {
  version: 1; quoteId: string;
  request: { chainType: string; network: HoldNetwork; assetId: string; sender: string; recipient: string; quantityBaseUnits: string; holdingAddress: string };
  protocolIdentity: string; assetAuthorityHash: string; nativeCurrency: string; nativeDecimals: number;
  transferNetworkFeeBaseUnits: string; recipientReady: boolean; acceptanceNetworkFeeBaseUnits: string;
  acceptanceMinimumBalanceBaseUnits: string; quotedAt: string; expiresAt: string; transferCosts?: HoldCostBreakdown | null;
}
export interface HoldRecipientWallet { id: string; ownerUserId: string; address: string; chainType: string }
export interface HoldMandateTerms {
  version: 1; holdRecordId: string; definitionHash: string; asset: HoldAssetIdentity; quantityBaseUnits: string;
  recipient: HoldRecipientWallet; notBefore: string; expiresAt: string; nativeCurrency: string; nativeDecimals: number;
  maximumCosts: HoldCostBreakdown;
}
export interface HoldCommandView {
  operationId: string; version: number; kind: string; state: string; holdRecordId: string; holdId: string;
  asset: HoldAssetIdentity; quantityBaseUnits: string; actorUserId: string; ownerUserId: string; tenantId?: string | null;
  beneficiaryUserId: string; transactionId?: string | null; detail?: string | null; nextHoldRecordId?: string | null;
  custodyTenantId?: string | null; costQuote?: HoldCostQuote | null; acceptanceState?: string | null; generation: number;
  releaseMandateId?: string | null; mandateTerms?: HoldMandateTerms | null; mandateDraftHash?: string | null;
}
export interface HoldCommandPage { items: HoldCommandView[]; nextOffset?: number | null }
export interface HoldStartCommand { intent: HoldCommandIntent; senderWalletId?: string | null; recipientWalletId?: string | null }
export interface HoldConsentCommand { requestId: string; version: number; quoteId: string; maximumNetworkFeeBaseUnits: string; reason?: string | null }
export interface HoldRecoveryCommand { requestId: string; version: number; reason: string }
export interface HoldOpeningAcceptance { requestId: string; version: number; draftHash: string }
export type HoldRequestLookup =
  | { kind: 'Transfer' | 'Acceptance'; consent: HoldConsentCommand; recovery?: never }
  | { kind: 'Replace' | 'Cancel'; recovery: HoldRecoveryCommand; consent?: never };
export interface HoldRequestResolution { state: 'Applied' | 'Obsolete' | 'Unresolved'; command?: HoldCommandView | null }
export interface HoldOpeningRecoveryLookup { kind: 'WorkflowOpeningReplace' | 'WorkflowOpeningWithdraw'; request: HoldRecoveryCommand }
export interface HoldMandateOfferRequest { requestId: string; holdVersion: number; terms: HoldMandateTerms }
export interface HoldMandateOffer { id: string; requestHash: string; request: HoldMandateOfferRequest & { quote: HoldCostQuote } }
export interface HoldMandateAcceptance { offerId: string; requestHash: string }
export interface HoldMandateView { id: string; version: number; status: string; terms: HoldMandateTerms; operationId?: string | null; detail?: string | null }
export interface HoldMandatePage { items: HoldMandateView[]; nextOffset?: number | null }
export interface HoldMandateResolution { state: 'Applied' | 'Obsolete' | 'Unresolved'; mandate?: HoldMandateView | null }

/** Owner-chosen automatic-release deadline and independent cost caps for a held exchange purchase. */
export interface ExchangeReleaseMandateRequest {
  expiresAt: string; maximumNetworkFeeBaseUnits: string; maximumLockedBalanceBaseUnits: string; maximumNonrefundableCostBaseUnits: string;
}
/** Server-authored release terms and current quote a preview returns; accept it verbatim. */
export interface ExchangeReleaseDisclosure { terms: HoldMandateTerms; quote: HoldCostQuote; disclosureHash: string }
/** Explicit acceptance of one previewed disclosure; a revised disclosure needs a new requestId. */
export interface ExchangeReleaseAcceptance { requestId: string; disclosure: ExchangeReleaseDisclosure }
