using System.Reflection;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Inventory;

/// <summary>
/// Every `*Fence.cs` names at least one real-DB test whose success path commits through it: a fence whose guards
/// never pass fails closed silently, and only an allow-path fixture notices. See Round4Gates/AGENTS.md §fence-allow-paths.
/// </summary>
public sealed class FenceAllowPathInventoryTests
{
    /// <summary>Fence file → test methods (Class.Method under AZOA.WebAPI.IntegrationTests) that commit through it.</summary>
    private static readonly Dictionary<string, string[]> AllowPaths = new(StringComparer.Ordinal)
    {
        ["ActionExecutionFence"] = ["Persistence.Surreal.SurrealActionEmitTests.Original_dispatch_and_absent_receipt_recovery_accept_one_local_receipt"],
        ["DeploymentAuthorityFence"] = ["Persistence.Surreal.SurrealDeploymentAuthorityTests.ActiveAuthorityCanAdmitAndAuthorizeExactStoredWire"],
        ["ExchangeAuthorityFence"] = ["Persistence.Surreal.SurrealExchangeAuthorityTests.ActualTenantAndDistinctParticipantAuthorizeTogetherWithoutImpersonation"],
        ["FungibleCreationAuthorityFence"] = ["Persistence.Surreal.SurrealFungibleProjectionTests.OriginalOwnerProjectionIsAtomicAndDoesNotNeedRenewedSigningCredentials"],
        ["LocalEntityMutationFence"] = ["Persistence.Surreal.SurrealActionLocalMutationTests.SplitGrantsAuthorizeOnlyWhileBothRequiredScopesAreLive"],
        ["NodeGovernancePolicyFence"] = ["Persistence.Surreal.SurrealNodeGovernanceStoreTests.AbsentPolicyWitnessCannotAuthorizeAnEffectAfterInitialPolicyCreation"],
        ["PlatformGenesisFence"] = ["Persistence.Surreal.SurrealFungibleProjectionTests.FinalizedGenesisActivatesWithAuthorityAndReserveAfterOperatorSessionRevocation"],
        ["SigningAuthorityFence"] = ["Persistence.Surreal.SurrealWorkflowSigningAuthorityTests.SplitOriginalGrantsAdmitOneNodeAndPersistItsExactAuthority"],
        ["HostedKycCreationFence"] = ["Persistence.Surreal.SurrealHostedProviderAttemptTests.OnlyOneSendLeaseWins_ExpiryPermitsSameRequestReplay_AndSavedResponseSurvivesLostAttachment"],
        ["HostedSessionAliasFence"] = ["Persistence.Surreal.SurrealHostedSessionRecoveryTests.QuarantinePreservesAdmissionAndOriginalCreatorCanAttachBothAliases"],
        ["HostedSessionLeaseFence"] = ["Http.IdentityVerificationFlowTests.The_kyc_gate_opens_only_after_the_vendor_result_approves_and_replay_changes_nothing"],
        ["KycEvidenceFence"] = ["Persistence.Surreal.SurrealHostedProviderAttemptTests.BoundAmlAppendsShareOneEvidenceRevision_SubjectlessScreeningsNeverCreateEmptySubjectGuard"],
        ["PersonOperationClaimFence"] = ["Persistence.Surreal.SurrealPersonOperationClaimTests.Concurrent_first_claims_return_one_incumbent_without_overwriting_its_tenant_or_decision"],
        ["DexSigningAuthorityFence"] = ["Persistence.Surreal.SurrealSwapAdmissionTests.CompletionCommitsImmutableReceiptAndOriginalReplayCannotChangeEffects"],
        ["HoldReleaseExchangeFence"] = ["Persistence.Surreal.SurrealHoldCommandTests.SettledPurchaseReleasesOnlyThroughItsExactMandateWhileTheOriginalApiKeyLives"],
        ["HoldReleaseMandateFence"] = ["Persistence.Surreal.SurrealHoldCommandTests.SettledPurchaseReleasesOnlyThroughItsExactMandateWhileTheOriginalApiKeyLives"],
        ["HoldReleaseWorkflowFence"] = ["Persistence.Surreal.SurrealHoldCommandTests.WorkflowOpeningConsentCommitsLiabilityAndMandateTogetherAndCancelledSourceCannotRelease"],
        ["NftProtocolCostAdmissionFence"] = ["Persistence.Surreal.SurrealNftLifecycleStoreTests.SelectedReceiptAdmitsOnceAndReplayDoesNotDependOnOfferAvailability"],
        ["NftWorkflowCostAuthorityFence"] = ["Persistence.Surreal.SurrealNftLifecycleStoreTests.WorkflowCostAcceptanceCommitsOnceAndReplaysAfterAuthorityAndQuoteRemoval"],
        ["TenantCustodialAuthorityFence"] = ["Persistence.Surreal.SurrealConsentLifecycleTests.Custodial_kyc_mutations_and_first_send_reject_changed_authority"],
        ["WttSigningAuthorityFence"] = ["Persistence.Surreal.SurrealWttOperationStoreTests.ConcurrentAdmissionsConsumeTheOfferOnceAndAnotherRequestCannotReuseIt"],
    };

    [Fact]
    public void Every_fence_names_an_allow_path_fixture_and_no_entry_is_stale()
    {
        var fences = new[] { "Core", "Helpers", "Providers", "Services", "Managers" }
            .Select(folder => Path.Combine(Root(), folder)).Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*Fence.cs", SearchOption.AllDirectories))
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.Ordinal);
        fences.Should().HaveCountGreaterThan(15, "the scan must find the fence surface");

        fences.Except(AllowPaths.Keys).Should().BeEmpty("a new fence needs a real-DB fixture whose success path commits through it");
        AllowPaths.Keys.Except(fences).Should().BeEmpty("a removed or renamed fence must leave this inventory too");
    }

    [Fact]
    public void Every_named_allow_path_test_exists()
    {
        var assembly = typeof(FenceAllowPathInventoryTests).Assembly;
        var missing = AllowPaths.Values.SelectMany(tests => tests).Distinct().Where(test =>
        {
            var split = test.LastIndexOf('.');
            var type = assembly.GetType("AZOA.WebAPI.IntegrationTests." + test[..split]);
            return type?.GetMethod(test[(split + 1)..], BindingFlags.Public | BindingFlags.Instance) is not { } method
                || !method.GetCustomAttributes().Any(attribute => attribute is FactAttribute);
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
