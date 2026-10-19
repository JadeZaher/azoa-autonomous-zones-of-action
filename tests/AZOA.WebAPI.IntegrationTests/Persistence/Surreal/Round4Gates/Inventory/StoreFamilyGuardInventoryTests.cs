using System.Reflection;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Inventory;

/// <summary>
/// Every store family that commits a BEGIN…COMMIT batch names one real-DB case where a THROW guard refuses (typed outcome,
/// nothing partial) and one duplicate/replay case (one winner), or records the gap with its owner. See Round4Gates/AGENTS.md §store-family-guards.
/// </summary>
public sealed class StoreFamilyGuardInventoryTests
{
    private sealed record Cases(string? ThrowMarker, string? Duplicate, string? Gap = null);

    private const string Surreal = "Persistence.Surreal.";

    private static readonly Dictionary<string, Cases> Families = new(StringComparer.Ordinal)
    {
        ["NodeAdminAuditLog"] = new(Surreal + "SurrealNodeAdminAuditLogTests.ConcurrentAppendRetriesRefusedHeadCasWithoutForkOrGap", Surreal + "SurrealNodeAdminAuditLogTests.DuplicateNextRowRollsBackEveryHeadAdvanceAndPreservesExistingEvidence"),
        ["SurrealActionEmitStore"] = new(Surreal + "SurrealActionEmitTests.Changed_frozen_input_or_cancelled_run_cannot_accept", Surreal + "SurrealActionEmitTests.Original_dispatch_and_absent_receipt_recovery_accept_one_local_receipt"),
        ["SurrealActionLocalMutationStore"] = new(Surreal + "SurrealActionLocalMutationTests.ChangedQueryMembershipRollsBackEveryStagedWriteAndReceipt", Surreal + "SurrealActionNftHolonLinkTests.ConcurrentAttachmentHasOneReceiptAndReplayDoesNotTouchChangedOwner"),
        ["SurrealActionNodeExecutionStore"] = new(Surreal + "SurrealActionNodeExecutionStoreTests.FrozenAdmissionUsesRecordLinksAndFencesStaleCompletion", Surreal + "SurrealActionNodeExecutionStoreTests.ConcurrentFrozenAdmissionsHaveOneWinnerAndRetainItsPayload"),
        ["SurrealActionRecoveryStore"] = new(Surreal + "SurrealActionRecoveryTests.RealSagaPark_ConcurrentRetryCreatesOneAudit_AndOldRequestCannotWakeLaterGeneration", Surreal + "SurrealActionRecoveryTests.RealSagaPark_ConcurrentRetryCreatesOneAudit_AndOldRequestCannotWakeLaterGeneration"),
        ["SurrealActionRunStore"] = new(null, null, "fork run CREATE + RELATE has no guard by design (TransactionScopeGateTests.GuardlessBatches); duplicate fork id case owed (L4)"),
        ["SurrealActionSigningAuthorizationStore"] = new(Surreal + "SurrealWorkflowSigningAuthorityTests.DelayedAdmissionCannotSubstituteCurrentAuthorityForItsOriginalWitness", Surreal + "SurrealWorkflowSigningAuthorityTests.ExplicitParticipantAuthorizationPreservesLegacyRunAndReplaysWithoutWakingLaterStep"),
        ["SurrealActionWebhookDeliveryStore"] = new(null, Surreal + "SurrealActionEmitTests.Retry_is_audited_once_and_preserves_lifetime_attempts_and_original_event", "THROW-marker case owed (L1): mirror Changed_destination_cannot_retarget_original_event_or_audited_retry for action events"),
        ["SurrealActionWorkflowAdmissionStore"] = new(Surreal + "SurrealActionAdmissionSafetyTests.FailureAfterSchedulerSnapshotFencesSuspendedSiblingAdmission", Surreal + "SurrealActionAdmissionSafetyTests.OverlappingFreshAdmissionsConsumeOneSchedulerEpoch"),
        ["SurrealAdminBootstrapStateStore"] = new(null, null, "credential/session revision CAS cases owed (L1): stale revision refused, concurrent rotation one winner"),
        ["SurrealConsentLifecycleStore"] = new(Surreal + "SurrealConsentLifecycleTests.Revoked_actor_authority_leaves_no_partial_lifecycle_records", Surreal + "SurrealConsentLifecycleTests.Concurrent_command_creates_one_grant_audit_event_and_original_receipt"),
        ["SurrealConsentWebhookDeliveryStore"] = new(Surreal + "SurrealConsentLifecycleTests.Changed_destination_cannot_retarget_original_event_or_audited_retry", Surreal + "SurrealConsentLifecycleTests.Concurrent_delivery_claims_consume_one_attempt_and_only_exact_holder_can_finish"),
        ["SurrealDeploymentRecoveryAuditStore"] = new(Surreal + "SurrealDeploymentRecoveryLinkTests.WinningChildFencesDelayedParentAndCompetingChild", Surreal + "SurrealDeploymentRecoveryLinkTests.WithdrawalAndChildAdmissionCannotBothWinTheSameParentVersion"),
        ["SurrealDeploymentStore"] = new(Surreal + "SurrealDeploymentAuthorityTests.TerminalRunCannotAdmitSuspendedCreator", Surreal + "SurrealDeploymentRecoveryLinkTests.WithdrawalAndChildAdmissionCannotBothWinTheSameParentVersion"),
        ["SurrealEthereumDexAllowanceStore"] = new(Surreal + "SurrealEthereumDexAllowanceTests.ConcurrentOperationsCannotShareAllowanceButPhysicalNetworksAreIndependent", Surreal + "SurrealEthereumDexAllowanceTests.ConcurrentOperationsCannotShareAllowanceButPhysicalNetworksAreIndependent"),
        ["SurrealEthereumPreparationStore"] = new(null, "EthereumProtocolTests.NativeValue_ReconstructedProviderUsesSameWire_AndReplayDoesNotPayTwice", "nonce-slot THROW case owed (L5): a second live preparation for the same (sender, nonce) is refused"),
        ["SurrealFungibleCreationStore"] = new(Surreal + "SurrealFungibleProjectionTests.ImmutableDefinitionClaimChecksOperatorRevisionInTheCreateTransaction", Surreal + "SurrealFungibleProjectionTests.SubmittedRecoveryRebroadcastsOnlySavedWireAndProjectsOnceAfterLostResponse"),
        ["SurrealGovernanceStore"] = new(Surreal + "SurrealGovernanceTests.Unprovisioned_principal_and_owned_child_cannot_create_election", Surreal + "SurrealGovernanceTests.Concurrent_finalizers_publish_one_immutable_snapshot"),
        ["SurrealHoldCommandStore"] = new(Surreal + "SurrealHoldCommandTests.RefundAndChainRaceCannotReturnAndInheritTheSameCommitment", Surreal + "SurrealHoldCommandTests.ConcurrentOpenHasOneLiabilityAndImmutableReplayAfterRefundAndRevocation"),
        ["SurrealHoldReleaseMandateStore"] = new(Surreal + "SurrealHoldCommandTests.InterleavedMandateFirstEgressAndRevocationNeverAuthorizeUnderARevokedMandate", Surreal + "SurrealHoldCommandTests.DirectPurchaseJournalsBeforeSigningSettlesFromChainEvidenceAndReplaysWithoutASecondSignature"),
        ["SurrealHolonStore"] = new(null, Surreal + "SurrealActionNftHolonLinkTests.ConcurrentAttachmentHasOneReceiptAndReplayDoesNotTouchChangedOwner", "transfer-reservation THROW case owed (L4): a stale reservation key cannot complete the transfer"),
        ["SurrealHostedProviderAttemptStore"] = new(Surreal + "SurrealHostedProviderAttemptTests.ConflictingTerminalEvidencePermanentlyFencesUnappliedApproval_AndCallbacksBindExactPayload", Surreal + "SurrealHostedProviderAttemptTests.OnlyOneSendLeaseWins_ExpiryPermitsSameRequestReplay_AndSavedResponseSurvivesLostAttachment"),
        ["SurrealHostedSessionStore"] = new(Surreal + "SurrealHostedSessionRecoveryTests.ConflictingAlias_RollsBackSessionAttachment", null, "duplicate admission case owed (L2): two creations under one admission key return one session"),
        ["SurrealKybVerificationStore"] = new(null, null, "KYB attempt cases owed (L2): stale lease refused, concurrent begin one attempt"),
        ["SurrealKycControlStore"] = new(null, null, "provider profile/selection version CAS cases owed (L2)"),
        ["SurrealKycIdentityStore"] = new(null, null, "person-key binding cases owed (L2): stale lease refused, concurrent binding one identity"),
        ["SurrealKycScreeningStore"] = new(Surreal + "SurrealHostedProviderAttemptTests.BoundAmlAppendsShareOneEvidenceRevision_SubjectlessScreeningsNeverCreateEmptySubjectGuard", null, "duplicate monitor registration case owed (L2): kyc_screening_monitor_query UNIQUE"),
        ["SurrealKycStore"] = new(Surreal + "SurrealConsentLifecycleTests.Custodial_kyc_mutations_and_first_send_reject_changed_authority", Surreal + "Round4Gates.Interleaving.KycBeginInterleavingTests.ConcurrentBeginsForOneSubjectScopeAdmitOneAttemptAndOnlyItsDocuments"),
        ["SurrealNftLifecycleStore"] = new(Surreal + "SurrealNftLifecycleStoreTests.ChangedStoredEvidenceRollsBackLifecycleCreation", Surreal + "SurrealNftLifecycleStoreTests.AtomicCommitScopesTwoTypedWritersAndStaleReplayCannotChangeOwnership"),
        ["SurrealNftWorkflowCostAcceptanceStore"] = new(Surreal + "SurrealNftLifecycleStoreTests.EmptyWorkflowRejectionRequiresOriginalExecutionAndCannotContainPreparedWire", Surreal + "SurrealNftLifecycleStoreTests.WorkflowCostAcceptanceCommitsOnceAndReplaysAfterAuthorityAndQuoteRemoval"),
        ["SurrealNodeAdminStore"] = new(Surreal + "SurrealNodeAdminStoreTests.RefusedMutationRollsBackDirectoryAndEarlierRoleWrite", Surreal + "SurrealNodeAdminStoreTests.ConcurrentDirectoryClaimsCommitOneRoleAndDuplicateCannotOverwriteIt"),
        ["SurrealNodeFeeScheduleStore"] = new(null, null, "fee schedule version CAS cases owed (L1): stale version refused, concurrent save one audit"),
        ["SurrealNodeFeeSettlementStore"] = new(Surreal + "SurrealNodeFeeSettlementStoreTests.ManagerPrepare_DivergentDecisionForPinnedKey_ReturnsConflictWithoutOverwrite", Surreal + "SurrealNodeFeeSettlementStoreTests.ConcurrentClaimAndExpiredLeaseReclaim_ElectsOneWinner"),
        ["SurrealNodeGovernanceStore"] = new(Surreal + "SurrealNodeGovernanceStoreTests.AuditCreateFailure_RollsBackPolicyWrite", null, "concurrent same-version policy save case owed (L1)"),
        ["SurrealNodeTreasuryStore"] = new(Surreal + "SurrealNodeTreasuryStoreTests.ConcurrentSameVersionUpdate_AllowsExactlyOneWriterAndAudit", Surreal + "SurrealNodeTreasuryStoreTests.ConcurrentSameVersionUpdate_AllowsExactlyOneWriterAndAudit"),
        ["SurrealPersonOperationClaimStore"] = new(Surreal + "SurrealPersonOperationClaimTests.Stale_snapshot_guard_refuses_mutation_and_current_retry_succeeds", Surreal + "SurrealPersonOperationClaimTests.Concurrent_first_claims_return_one_incumbent_without_overwriting_its_tenant_or_decision"),
        ["SurrealSagaStore"] = new(null, "Gates.G2_IdempotencyTocTouTest.IdempotencyKey_50ConcurrentRedeemRequests_ProduceExactlyOneChainEffect", "step-claim THROW case owed (L4): a stale step version cannot complete"),
        ["SurrealSwapOperationStore"] = new(Surreal + "SurrealSwapAdmissionTests.InterleavedFirstSendAndSubmissionWithdrawalOfAnExposedStageHaveOneWinner", Surreal + "SurrealSwapAdmissionTests.CompletionCommitsImmutableReceiptAndOriginalReplayCannotChangeEffects"),
        ["SurrealUserStore"] = new(null, null, "claim-ownership cases owed (L4): stale invite refused, concurrent claim one owner (SQL-01 path)"),
        ["SurrealWalletStore"] = new(null, null, "tenant bootstrap cases owed (L4): SQL-02 path covered by SurrealConsentLifecycleTests.Tenant_bootstrap_* but not named per family"),
        ["SurrealWebhookRegistrationStore"] = new(Surreal + "SurrealWebhookRegistrationTests.Managed_mutation_refuses_cross_tenant_and_revoked_authority_atomically", Surreal + "SurrealWebhookRegistrationTests.Concurrent_tenant_creation_has_exactly_one_identity_and_secret"),
        ["SurrealWttOperationStore"] = new(Surreal + "SurrealWttOperationStoreTests.ChangedOfferValidityOrCurrentAuthorityRollsBackAdmission", Surreal + "SurrealWttOperationStoreTests.ConcurrentAdmissionsConsumeTheOfferOnceAndAnotherRequestCannotReuseIt"),
        ["TenantPayoutLedger"] = new(Surreal + "Round4Gates.TenantPayout.TenantPayoutLedgerRaceTests.A_replayed_reservation_for_the_same_payout_never_reserves_twice", Surreal + "Round4Gates.TenantPayout.TenantPayoutLedgerRaceTests.Two_concurrent_reservations_against_a_balance_that_covers_one_commit_exactly_one"),
    };

    [Fact]
    public void Every_batch_store_family_is_inventoried()
    {
        var root = Root();
        var families = new[] { "Core", "Helpers", "Providers", "Services", "Managers" }
            .Select(folder => Path.Combine(root, folder)).Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(path => File.ReadAllText(path).Contains("Of(\"BEGIN\")", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path).Split('.')[0])
            .Where(name => !name.EndsWith("Fence", StringComparison.Ordinal) && name is not ("TenantCustodialAccess" or "WalletAuthIdentityMigrationService"))
            .ToHashSet(StringComparer.Ordinal);
        families.Except(Families.Keys).Should().BeEmpty("a new batch store needs a THROW-marker and a duplicate case, or a recorded gap");
        Families.Keys.Except(families).Should().BeEmpty("a removed store must leave the inventory");
        Families.Where(entry => (entry.Value.ThrowMarker is null || entry.Value.Duplicate is null) && string.IsNullOrWhiteSpace(entry.Value.Gap))
            .Select(entry => entry.Key).Should().BeEmpty("a missing case must say who owes it");
    }

    [Fact]
    public void Every_named_case_exists()
    {
        var assembly = typeof(StoreFamilyGuardInventoryTests).Assembly;
        var missing = Families.Values.SelectMany(cases => new[] { cases.ThrowMarker, cases.Duplicate }).OfType<string>().Distinct().Where(test =>
        {
            var split = test.LastIndexOf('.');
            var type = assembly.GetType("AZOA.WebAPI.IntegrationTests." + test[..split]);
            return type?.GetMethod(test[(split + 1)..], BindingFlags.Public | BindingFlags.Instance)?.GetCustomAttributes().Any(attribute => attribute is FactAttribute) != true;
        });
        missing.Should().BeEmpty("an inventory row that points at no test proves nothing");
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AZOA.WebAPI.csproj"))) directory = directory.Parent;
        return directory!.FullName;
    }
}
