/** Immutable deployment targets and durable operation receipts. Amounts are base-unit strings. */
export interface DeploymentTarget { kind: "PlatformRuntime" | "NativeContract"; chainType: string; network: string }
export interface NativeArtifact { format: string; content: unknown; compilerIdentity: string }
export interface DeploymentInputMapping { sourceOccurrence: string; sourceNodeId: string; sourceField: string; targetOccurrence: string; targetNodeId: string; targetField: string }
export interface DeploymentGenerateRequest { target: DeploymentTarget; actionIds?: string[]; native?: NativeArtifact; mappings?: DeploymentInputMapping[] }
export interface DeploymentArtifactView { artifactId: string; payloadHash: string; artifactHash: string; target: DeploymentTarget; loader?: string | null; formatVersion: "2"; native?: NativeArtifact | null }
export interface NativeBudget { maximumNetworkFee: string; maximumLockedRent: string; maximumValue: string; quoteId: string }
export interface NativeSigningContext { userId: string; walletId: string; isPlatform: false; actingTenantId?: string; grantorUserId?: string; scope?: string; chainType?: string }
export interface NativeAuthority { address: string; signingContext: NativeSigningContext }
export interface NativeOperationDisclosure { effect: string; refundDestination?: string | null; estimatedReturnedBaseUnits?: string | null; resultingMetadata?: unknown; cleanupRefundDestination?: string | null }
export interface NativeOperationQuote { quoteId: string; intentHash: string; protocolIdentity: string; networkFee: string; lockedRent: string; value: string; planJson: string; expiresAt: string; disclosure?: NativeOperationDisclosure | null }
export interface NativeOperationIntent { version: 1; operationKey: string; kind: string; chainType: string; network: string; protocolIdentity: string; artifactHash: string; input: unknown; authorities: NativeAuthority[]; budget: NativeBudget; quote?: NativeOperationQuote | null }
export interface DeploymentQuoteRequest { artifactId: string; requestKey: string; intent: NativeOperationIntent }
export interface DeploymentStartRequest { artifactId: string; artifactHash: string; target: DeploymentTarget; nativeIntent?: NativeOperationIntent | null }
export interface NativeBalanceChange { address: string; beforeBaseUnits: string; afterBaseUnits: string }
export interface NativeStageAccounting { networkFeePaid: string; nativeBalances: NativeBalanceChange[] }
export interface DeploymentEconomics { reservedNetworkFee: string; reservedRent: string; reservedValue: string; observedNetworkFeePaid: string; confirmedAccounting: NativeStageAccounting[]; networkFeeAccountingComplete: boolean }
export type DeploymentState = "Admitted" | "AwaitingAuthorization" | "Preparing" | "Prepared" | "Submitted" | "NeedsOperator" | "Confirmed" | "Failed" | "Cancelled" | "AbortedClean" | "FailedClean" | "RecoveryQuoteRequired";
export interface NativeAuthorizationSummary { kind: string; budget: NativeBudget; quote: NativeOperationQuote; authorities: NativeAuthority[]; input: unknown }
export interface NativeResourceEffect { kind: "BufferFunded" | "Deployed" | "Upgraded" | "AuthorityFinalized" | "BufferClosed"; resourceId?: string | null; transactionId: string; evidenceJson?: string | null }
export interface DeploymentRecoverySummary { mode: string; parentOperationId?: string | null; childOperationId?: string | null; evidenceJson?: string | null; projected: boolean; replacesOperationId?: string | null; replacedByOperationId?: string | null }
export type DeploymentRecoveryMode = "AbortCleanup" | "ContinueCleanup" | "FinalizeAuthority";
export interface DeploymentOriginalBudgetRecoveryRequest { expectedVersion: number; mode: DeploymentRecoveryMode; acknowledgeRecovery: boolean }
export interface DeploymentRecoveryQuoteRequest { expectedVersion: number; requestKey: string; mode: DeploymentRecoveryMode; payerAddress: string; authorities: NativeAuthority[]; budget: NativeBudget }
export interface DeploymentRecoveryQuote { parentVersion: number; bindingHash: string; intent: NativeOperationIntent }
export interface DeploymentRecoveryStartRequest { requestKey: string; quote: DeploymentRecoveryQuote; acknowledgeCosts: boolean }
export interface DeploymentRecoveryAncestry { headOperationId: string; state: 'Running' | 'Complete' | 'Invalid'; inspectedCount: number; digest: string; detail?: string | null }
export interface DeploymentReceipt { operationId: string; artifactId: string; artifactHash: string; target: DeploymentTarget; state: DeploymentState; stage: string; resourceId?: string | null; invokeUrl?: string | null; transactionIds: string[]; detail?: string | null; version: number; economics?: DeploymentEconomics | null; authorization?: NativeAuthorizationSummary | null; resourceEffect?: NativeResourceEffect | null; cleanupEffect?: NativeResourceEffect | null; recovery?: DeploymentRecoverySummary | null }
export interface DeploymentPage { items: DeploymentReceipt[]; nextCursor?: string | null }
export interface DeploymentInvokeRequest { expectedPayloadHash: string; input: unknown; acknowledgeEconomicEffects: boolean }
export interface DappCompositionManifest { dappSeriesId: string; version: string; boundHolonIds: string[]; actionGraph: string; targetChain: string; config: Record<string, string>; generatedDate: string }
export interface DappCompositionValidation { allActionsCompleted: boolean; chainCompleteness: boolean; inputMappingConsistency: boolean; noCircularDependencies: boolean; holonBindingsResolved: boolean; diagnostics: string[]; isValid: boolean }
export interface DeploymentEcosystemNode { node: { id: string; ecosystemId: string; parentNodeId?: string | null; refKind: "DappSeries" | "ActionGraphBundle"; refId: string; label?: string; createdDate: string }; children: DeploymentEcosystemNode[] }
export interface DeploymentEcosystemTree { ecosystem: { id: string; name: string; description?: string; actionGraphBundleId: string; userId: string; targetChain?: string; createdDate: string; modifiedDate?: string }; roots: DeploymentEcosystemNode[] }
export interface DeploymentEcosystemAttachment { refId: string; refKind?: "DappSeries" | "ActionGraphBundle"; parentNodeId?: string; label?: string; ecosystemName?: string; targetChain?: string }
export interface AvmProgramInput { kind: "Teal" | "Bytecode"; data: string }
export interface AvmApplicationArtifact { approval: AvmProgramInput; clearState: AvmProgramInput; globalSchema: { integers: number; bytes: number }; localSchema: { integers: number; bytes: number }; extraPages: number; argumentsBase64: string[]; accounts: string[]; foreignApplications: string[]; foreignAssets: string[]; boxes: { appIndex: number; nameBase64: string }[] }
export interface AvmNativeInput { artifact: NativeArtifact; sender: string; operation?: "Create" | "Call"; applicationId?: string }
export interface EvmContractArtifact { initCodeHex: string; expectedRuntimeCodeHex: string; constructorSignature?: string; constructorArguments?: unknown[] }
export interface EvmCallArtifact { contractAddress: string; expectedCodeHash: string; method: string; arguments: unknown[] }
export interface EvmNativeInput { artifact: NativeArtifact; sender: string; valueBaseUnits?: string }
export interface ContractQueryRequest { chainType: string; network: string; call: { contractAddress: string; method: string; arguments: unknown[]; outputTypes: string[]; fromAddress?: string } }
export interface SolanaElfArtifact { elfBase64: string; loader?: "loader-v3"; sourceDigest?: string }
export interface SolanaInstructionArtifact { programId: string; accounts: { address: string; writable: boolean; signer: boolean }[]; dataBase64: string; expectedProgramDataHash?: string }
export interface SolanaAccountArtifact { operation: "CreateAta" | "CloseTokenAccount" | "CreateMetadata" | "UpdateMetadata"; mint: string; owner: string; tokenAccount?: string; destination?: string; name?: string; symbol?: string; uri?: string; sellerFeeBasisPoints?: number; mutable?: boolean; updateAuthority?: string; expectedMetadataHash?: string; creators?: { address: string; verified: boolean; share: number }[]; unwrapNative?: boolean }
export interface SolanaNativeInput { artifact: NativeArtifact; payer: string; authority: string; operation?: "Create" | "Upgrade" | "Call" | "Account"; programId?: string; immutable?: boolean; maximumDataLength?: number; rentRecipient?: string; expectedProgramDataHash?: string }




