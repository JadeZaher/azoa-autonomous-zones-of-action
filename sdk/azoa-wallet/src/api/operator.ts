import type { DeploymentReceipt } from "./deployments.js";

/** `POST /api/operator/deployments/{id}/resolve` (F-H12): re-observe the chain, or close a row nothing further can affect. */
export type DeploymentOperatorAction = "Reobserve" | "CloseUnresolvable";
export interface DeploymentOperatorResolution {
  expectedVersion: number;
  action: DeploymentOperatorAction;
  reason?: string;
  acknowledgeNoFurtherEffect: boolean;
}
export type ParkedDeployment = DeploymentReceipt;

/** `GET /api/exchange/operator/retired-rows`: retired exchange operations awaiting an audited ledger attestation. */
export interface RetiredExchangeRowView {
  operationId: string;
  status: string;
  transactionHash?: string | null;
  chainType?: string | null;
  createdAt: string;
  resolvedOutcome?: string | null;
  resolvedReason?: string | null;
  resolvedAt?: string | null;
}
export interface RetiredExchangeResolution { outcome: "Confirmed" | "Failed"; reason: string }

/** `GET /api/operator/wallet-identities`: canonical wallet-identity migration status and its conflicts. */
export interface WalletIdentityConflict { userId: string; expectedIdentity: string; problem: string }
export interface WalletIdentityMigrationView { status: string; version: number; conflicts: WalletIdentityConflict[] }
export interface WalletIdentityConflictRepair { version: number; userId: string; expectedIdentity: string; reason: string }

/** `POST /api/operator/dex/operations/{id}/settle-review`. */
export interface SwapOperatorSettlementRequest { expectedVersion: number; reason: string }

/** `GET /api/operator/wtt/stage-keys/readiness`: a degraded keyring refuses new stage-key admissions. */
export interface WttStageKeyReadinessReport {
  status: string;
  configured: boolean;
  activeKeyId?: string | null;
  availableKeyVersions: string[];
  unreadableEnvelopeId?: string | null;
  rewrapPending: boolean;
  detail: string;
  checkedAt: string;
}
export interface WttStageKeyRewrapReport { rewrapped: number; conflicted: number; rewrapPending: boolean; readiness: WttStageKeyReadinessReport }

/** Hosted-session queue status; `AwaitingProviderOutcome` = attached and untouched for an hour, refresh re-reads the provider. */
export type HostedSessionQueueStatus = "NeedsOperator" | "AwaitingProviderOutcome" | "Claimed";
