using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Base;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealNodeFeeSettlementStoreTests : IAsyncLifetime
{
    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealNodeFeeSettlementStore _store = null!;
    private SurrealIdempotencyStore _idempotency = null!;
    private HttpSurrealConnection _connection = null!;
    private bool _surrealAvailable;

    public async Task InitializeAsync()
    {
        _surrealAvailable = await ProbeSurrealAsync();
        if (!_surrealAvailable)
            return;

        var options = new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _testNamespace,
            Database = "test",
            User = SurrealTestDefaults.User,
            Password = SurrealTestDefaults.Password,
        };
        _connection = new HttpSurrealConnection(
            new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) },
            options);
        var executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealNodeFeeSettlementStore(executor);
        _idempotency = new SurrealIdempotencyStore(executor);
        await SurrealTestSchema.BootstrapAsync(
            _testNamespace,
            IdempotencyKeyStore.SchemaNameConst,
            NodeFeeSettlement.SchemaNameConst,
            NodeFeeAtomicGroup.SchemaNameConst);
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable || _connection is null)
            return;

        try
        {
            await SurrealTestSchema.DropAsync(_testNamespace);
        }
        catch
        {
            // Best-effort test namespace cleanup.
        }
        finally
        {
            _connection.Dispose();
        }
    }

    [SkippableFact]
    public async Task DueRecovery_ClaimAndLeaseGuardedDefer_RoundTripWithoutEffectMutation()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var created = await AdmitPreparedAsync(now);
        var candidates = await _store.ListRecoverableAsync(now.AddSeconds(1), 10);
        candidates.IsError.Should().BeFalse(candidates.Message);
        candidates.Result.Should().ContainSingle();

        var claim = await _store.TryClaimRecoveryAsync(
            candidates.Result!.Single(),
            "worker-a",
            now.AddSeconds(1),
            now.AddSeconds(31));
        claim.IsError.Should().BeFalse(claim.Message);
        claim.Result.Should().NotBeNull();
        claim.Result!.LeaseToken.Should().Be("worker-a");
        claim.Result.AttemptCount.Should().Be(1);
        claim.Result.StateVersion.Should().Be(1);

        var staleToken = await _store.TryDeferToReconciliationAsync(
            new NodeFeeSettlementRecoveryLease(created.Id, "wrong-worker", claim.Result.StateVersion),
            "wrong lease must not mutate",
            now.AddMinutes(5),
            now.AddSeconds(2));
        staleToken.IsError.Should().BeFalse(staleToken.Message);
        staleToken.Result.Should().BeFalse();

        var deferred = await _store.TryDeferToReconciliationAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(claim.Result),
            "execution remains disabled",
            now.AddMinutes(5),
            now.AddSeconds(2));
        deferred.IsError.Should().BeFalse(deferred.Message);
        deferred.Result.Should().BeTrue();

        var persisted = await _store.GetAsync(created.Id);
        persisted.Result.Should().NotBeNull();
        persisted.Result!.State.Should().Be(NodeFeeSettlement.StateKind.AwaitingReconciliation);
        persisted.Result.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        persisted.Result.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        persisted.Result.LeaseToken.Should().BeNull();
        persisted.Result.LeaseExpiresAt.Should().BeNull();
        persisted.Result.ReconciliationReason.Should().Be("execution remains disabled");
        persisted.Result.StateVersion.Should().Be(2);
    }

    [SkippableFact]
    public async Task AcceptedGroupRecoveryClaim_WithoutReceipt_LeavesPreparedSettlementUntouched()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var created = await AdmitPreparedAsync(now);

        var claim = await _store.TryClaimAcceptedAtomicGroupRecoveryAsync(
            created,
            "accepted-group-worker",
            now.AddSeconds(1),
            now.AddMinutes(1));
        var persisted = await _store.GetAsync(created.Id);

        claim.IsError.Should().BeFalse(claim.Message);
        claim.Result.Should().BeNull();
        persisted.Result.Should().NotBeNull();
        persisted.Result!.State.Should().Be(NodeFeeSettlement.StateKind.Prepared);
        persisted.Result.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        persisted.Result.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        persisted.Result.AttemptCount.Should().Be(0);
        persisted.Result.StateVersion.Should().Be(0);
        persisted.Result.LeaseToken.Should().BeNull();
        persisted.Result.LeaseExpiresAt.Should().BeNull();
    }

    [SkippableFact]
    public async Task ConcurrentClaimAndExpiredLeaseReclaim_ElectsOneWinner()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var created = await AdmitPreparedAsync(now);
        var first = await _store.TryClaimRecoveryAsync(created, "expired-worker", now, now.AddSeconds(1));
        first.Result.Should().NotBeNull();

        var staleCandidates = await _store.ListRecoverableAsync(now.AddSeconds(2), 10);
        staleCandidates.Result.Should().ContainSingle();
        var staleCandidate = staleCandidates.Result!.Single();
        var claims = await Task.WhenAll(
            _store.TryClaimRecoveryAsync(staleCandidate, "worker-a", now.AddSeconds(2), now.AddSeconds(32)),
            _store.TryClaimRecoveryAsync(staleCandidate, "worker-b", now.AddSeconds(2), now.AddSeconds(32)));

        claims.Should().OnlyContain(claim => !claim.IsError, string.Join(Environment.NewLine, claims.Select(claim => claim.Message)));
        claims.Count(claim => claim.Result is not null).Should().Be(1);
        var winner = claims.Single(claim => claim.Result is not null).Result!;
        winner.AttemptCount.Should().Be(2);
        winner.StateVersion.Should().Be(2);

        var persisted = await _store.GetAsync(created.Id);
        persisted.Result!.LeaseToken.Should().BeOneOf("worker-a", "worker-b");
        persisted.Result.LeaseExpiresAt.Should().Be(now.AddSeconds(32));
    }

    [SkippableFact]
    public async Task ConcurrentAdmissions_NormalizeToOneCreatedAndOneReplayedPair()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var source = Guid.NewGuid().ToString("N");
        var first = CreateSettlement(source, now);
        var second = CreateSettlement(source, now);

        var results = await Task.WhenAll(
            _store.AdmitAsync(first, source),
            _store.AdmitAsync(second, source));

        results.Should().OnlyContain(result => !result.IsError,
            string.Join(Environment.NewLine, results.Select(result => result.Message)));
        results.Count(result => result.Result!.Disposition == NodeFeeSettlementAdmissionDisposition.Created)
            .Should().Be(1);
        results.Count(result => result.Result!.Disposition == NodeFeeSettlementAdmissionDisposition.Replayed)
            .Should().Be(1);
        results.Select(result => result.Result!.Settlement.Id).Distinct().Should().ContainSingle();

        var persisted = await _store.GetAsync(first.Id);
        persisted.Result.Should().NotBeNull();
        persisted.Result!.GrossAmount.Should().Be("1000");
    }

    [SkippableFact]
    public async Task ManagerPrepare_DivergentDecisionForPinnedKey_ReturnsConflictWithoutOverwrite()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var manager = new NodeFeeSettlementManager(_store);
        var parentKey = Guid.NewGuid().ToString("N");
        var original = Draft(parentKey);
        var prepared = await manager.PrepareAsync(original);
        prepared.IsError.Should().BeFalse(prepared.Message);

        var divergent = await manager.PrepareAsync(Draft(parentKey, feeAmount: "30", netAmount: "970"));

        divergent.IsError.Should().BeTrue();
        divergent.Message.Should().Contain("conflict");
        var persisted = await _store.GetAsync(prepared.Result!.Id);
        persisted.Result.Should().NotBeNull();
        persisted.Result!.FeeAmount.Should().Be("25");
        persisted.Result.NetAmount.Should().Be("975");
    }

    [SkippableFact]
    public async Task ConcurrentManagerAdmission_CreatesOneInProgressParentAndOneSettlement()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var manager = new NodeFeeSettlementManager(_store);
        var parentKey = "fee-admission:" + Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(
            manager.PrepareAsync(Draft(parentKey)),
            manager.PrepareAsync(Draft(parentKey)));

        results.Should().OnlyContain(result => !result.IsError,
            string.Join(Environment.NewLine, results.Select(result => result.Message)));
        results.Count(result => result.Message == "Settlement prepared.").Should().Be(1);
        results.Count(result => result.Message == "Settlement already prepared.").Should().Be(1);

        var parent = await _idempotency.GetAsync(parentKey, CancellationToken.None);
        parent.Should().NotBeNull();
        parent!.State.Should().Be(AZOA.WebAPI.Models.Idempotency.IdempotencyState.InProgress);
        parent.ResultPayload.Should().BeNull();
        parent.Error.Should().BeNull();

        var settlement = await _store.GetAsync(results[0].Result!.Id);
        settlement.Result.Should().NotBeNull();
        settlement.Result!.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        settlement.Result.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
    }

    [SkippableFact]
    public async Task Admission_SettlementSchemaFailure_RollsBackTheNewParentClaim()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var parentKey = "failed-admission:" + Guid.NewGuid().ToString("N");
        var invalid = CreateSettlement(parentKey, DateTimeOffset.UtcNow);
        invalid.AssetId = string.Empty;

        Func<Task> act = async () => await _store.AdmitAsync(invalid, parentKey);
        await act.Should().ThrowAsync<Exception>();

        (await _idempotency.GetAsync(parentKey, CancellationToken.None)).Should().BeNull();
        (await _store.GetAsync(invalid.Id)).Result.Should().BeNull();
    }

    [SkippableFact]
    public async Task Admission_EffectBearingInput_IsRejectedBeforeTheRawContentTransaction()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var parentKey = "effect-bearing-admission:" + Guid.NewGuid().ToString("N");
        var invalid = CreateSettlement(parentKey, DateTimeOffset.UtcNow);
        invalid.State = NodeFeeSettlement.StateKind.PrimarySubmitted;
        invalid.PrimaryEffectState = NodeFeeSettlement.EffectStateKind.Submitted;
        invalid.PrimaryOperationId = "operation:must-not-be-written";
        invalid.PrimaryTransactionHash = "transaction:must-not-be-written";
        invalid.LeaseToken = "must-not-be-written";

        Func<Task> act = async () => await _store.AdmitAsync(invalid, parentKey);
        await act.Should().ThrowAsync<ArgumentException>();

        (await _idempotency.GetAsync(parentKey, CancellationToken.None)).Should().BeNull();
        (await _store.GetAsync(invalid.Id)).Result.Should().BeNull();
    }

    [SkippableFact]
    public async Task Admission_UnbalancedEffectFreePreparedInput_IsRejectedBeforeTheRawContentTransaction()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var parentKey = "unbalanced-admission:" + Guid.NewGuid().ToString("N");
        var invalid = CreateSettlement(parentKey, DateTimeOffset.UtcNow);
        invalid.NetAmount = "974";

        Func<Task> act = async () => await _store.AdmitAsync(invalid, parentKey);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*gross = fee + net*");

        (await _idempotency.GetAsync(parentKey, CancellationToken.None)).Should().BeNull();
        (await _store.GetAsync(invalid.Id)).Result.Should().BeNull();
    }

    [SkippableFact]
    public async Task Admission_TerminalInput_IsRejectedBeforeTheRawContentTransaction()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var parentKey = "terminal-admission:" + Guid.NewGuid().ToString("N");
        var invalid = CreateSettlement(parentKey, DateTimeOffset.UtcNow);
        invalid.State = NodeFeeSettlement.StateKind.Settled;
        invalid.PrimaryEffectState = NodeFeeSettlement.EffectStateKind.Confirmed;
        invalid.FeeEffectState = NodeFeeSettlement.EffectStateKind.Confirmed;

        Func<Task> act = async () => await _store.AdmitAsync(invalid, parentKey);
        await act.Should().ThrowAsync<ArgumentException>();

        (await _idempotency.GetAsync(parentKey, CancellationToken.None)).Should().BeNull();
        (await _store.GetAsync(invalid.Id)).Result.Should().BeNull();
    }

    [SkippableFact]
    public async Task Admission_RejectsSettlementTerminalBeforeItsInProgressParent()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var parentKey = "reverse-terminal:" + Guid.NewGuid().ToString("N");
        var prepared = CreateSettlement(parentKey, DateTimeOffset.UtcNow);
        var admitted = await _store.AdmitAsync(prepared, parentKey);
        admitted.IsError.Should().BeFalse(admitted.Message);

        var executor = new DefaultSurrealExecutor(_connection);
        var corrupt = SurrealQuery
            .Of("UPDATE ONLY type::record($_t, $_id) SET state = $_state")
            .WithParam("_t", NodeFeeSettlement.SchemaNameConst)
            .WithParam("_id", prepared.Id)
            .WithParam("_state", NodeFeeSettlement.StateKind.Settled.ToString());
        var corruptResult = await executor.ExecuteAsync(corrupt, CancellationToken.None);
        corruptResult.EnsureAllOk();

        Func<Task> act = async () => await _store.AdmitAsync(CreateSettlement(parentKey, DateTimeOffset.UtcNow), parentKey);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*terminal states disagree*");
    }

    [SkippableFact]
    public async Task ManagerPrepare_NormalOuterIdempotencyClaim_FailsClosedAndPreservesTheClaim()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var parentKey = "outer-idempotency:" + Guid.NewGuid().ToString("N");
        var outerClaim = await _idempotency.TryClaimAsync(parentKey, "allocation/mint", CancellationToken.None);
        outerClaim.Won.Should().BeTrue();
        var manager = new NodeFeeSettlementManager(_store);

        var result = await manager.PrepareAsync(Draft(parentKey));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("already claimed");
        var preserved = await _idempotency.GetAsync(parentKey, CancellationToken.None);
        preserved.Should().NotBeNull();
        preserved!.OperationType.Should().Be("allocation/mint");
        preserved.State.Should().Be(AZOA.WebAPI.Models.Idempotency.IdempotencyState.InProgress);
        (await _store.GetAsync(NodeFeeSettlement.RecordIdFor(parentKey, "Transfer"))).Result.Should().BeNull();
    }

    [SkippableFact]
    public async Task ExpiredLease_ReclaimedByCurrentWinner_RejectsOldCorrectTokenAndAllowsCurrentToken()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var created = await AdmitPreparedAsync(now);
        var expiredClaim = await _store.TryClaimRecoveryAsync(
            created,
            "expired-worker",
            now,
            now.AddSeconds(1));
        expiredClaim.Result.Should().NotBeNull();

        var reclaimAt = now.AddSeconds(2);
        var currentClaim = await _store.TryClaimRecoveryAsync(
            expiredClaim.Result!,
            "current-worker",
            reclaimAt,
            reclaimAt.AddSeconds(30));
        currentClaim.Result.Should().NotBeNull();

        var staleDefer = await _store.TryDeferToReconciliationAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(expiredClaim.Result!),
            "stale worker must not mutate",
            now.AddMinutes(5),
            reclaimAt.AddSeconds(1));
        staleDefer.IsError.Should().BeFalse(staleDefer.Message);
        staleDefer.Result.Should().BeFalse();

        var currentDefer = await _store.TryDeferToReconciliationAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(currentClaim.Result!),
            "current worker may defer",
            now.AddMinutes(5),
            reclaimAt.AddSeconds(1));
        currentDefer.IsError.Should().BeFalse(currentDefer.Message);
        currentDefer.Result.Should().BeTrue();

        var persisted = await _store.GetAsync(created.Id);
        persisted.Result!.State.Should().Be(NodeFeeSettlement.StateKind.AwaitingReconciliation);
        persisted.Result.ReconciliationReason.Should().Be("current worker may defer");
        persisted.Result.LeaseToken.Should().BeNull();
        persisted.Result.StateVersion.Should().Be(3);
    }

    [SkippableFact]
    public async Task PairedTerminalization_CompletesConfirmedDistinctEffectsAndParentInOneTransaction()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var parentKey = "terminal-pair:" + Guid.NewGuid().ToString("N");
        var admitted = await _store.AdmitAsync(CreateSettlement(parentKey, now), parentKey);
        var claim = await _store.TryClaimRecoveryAsync(
            admitted.Result!.Settlement,
            "terminal-worker",
            now.AddSeconds(1),
            now.AddMinutes(1));

        var settled = await _store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(claim.Result!),
            new NodeFeeSettlementTerminalization(
                parentKey,
                "primary:confirmed-1",
                "fee:confirmed-2",
                "{\"settlement\":\"complete\"}"),
            now.AddSeconds(2));

        settled.IsError.Should().BeFalse(settled.Message);
        settled.Result.Should().BeTrue();
        var persisted = await _store.GetAsync(admitted.Result.Settlement.Id);
        var parent = await _idempotency.GetAsync(parentKey, CancellationToken.None);
        persisted.Result!.State.Should().Be(NodeFeeSettlement.StateKind.Settled);
        persisted.Result.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Confirmed);
        persisted.Result.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Confirmed);
        persisted.Result.PrimaryTransactionHash.Should().Be("primary:confirmed-1");
        persisted.Result.FeeTransactionHash.Should().Be("fee:confirmed-2");
        persisted.Result.LeaseToken.Should().BeNull();
        parent!.State.Should().Be(AZOA.WebAPI.Models.Idempotency.IdempotencyState.Completed);
        parent.ResultPayload.Should().Be("{\"settlement\":\"complete\"}");

        var reverse = await _store.TryDeferToReconciliationAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(claim.Result!),
            "terminal rows are irreversible",
            now.AddMinutes(5),
            now.AddSeconds(3));
        reverse.IsError.Should().BeFalse(reverse.Message);
        reverse.Result.Should().BeFalse();
        (await _idempotency.GetAsync(parentKey, CancellationToken.None))!.State
            .Should().Be(AZOA.WebAPI.Models.Idempotency.IdempotencyState.Completed);
    }

    [SkippableFact]
    public async Task PairedTerminalization_RejectsIllegalProofAndStaleLeaseWithoutPartialParentCompletion()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var parentKey = "terminal-stale:" + Guid.NewGuid().ToString("N");
        var admitted = await _store.AdmitAsync(CreateSettlement(parentKey, now), parentKey);
        var oldClaim = await _store.TryClaimRecoveryAsync(
            admitted.Result!.Settlement,
            "old-worker",
            now.AddSeconds(1),
            now.AddSeconds(2));
        var currentClaim = await _store.TryClaimRecoveryAsync(
            oldClaim.Result!,
            "current-worker",
            now.AddSeconds(3),
            now.AddMinutes(1));

        Func<Task> invalidProof = async () => await _store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(currentClaim.Result!),
            new NodeFeeSettlementTerminalization(parentKey, "same-effect", "same-effect", "payload"),
            now.AddSeconds(4));
        await invalidProof.Should().ThrowAsync<ArgumentException>();

        var stale = await _store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(oldClaim.Result!),
            new NodeFeeSettlementTerminalization(parentKey, "primary:stale", "fee:stale", "payload"),
            now.AddSeconds(4));
        stale.IsError.Should().BeFalse(stale.Message);
        stale.Result.Should().BeFalse();
        var persisted = await _store.GetAsync(admitted.Result.Settlement.Id);
        persisted.Result!.State.Should().NotBe(NodeFeeSettlement.StateKind.Settled);
        (await _idempotency.GetAsync(parentKey, CancellationToken.None))!.State
            .Should().Be(AZOA.WebAPI.Models.Idempotency.IdempotencyState.InProgress);
    }

    [SkippableFact]
    public async Task NonterminalReconciliation_UnknownOrFailedEffectsCannotCompleteTheParent()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var parentKey = "nonterminal-effect:" + Guid.NewGuid().ToString("N");
        var admitted = await _store.AdmitAsync(CreateSettlement(parentKey, now), parentKey);
        var claim = await _store.TryClaimRecoveryAsync(
            admitted.Result!.Settlement,
            "reconciliation-worker",
            now.AddSeconds(1),
            now.AddMinutes(1));

        var deferred = await _store.TryRecordNonTerminalReconciliationAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(claim.Result!),
            new NodeFeeSettlementEffectReconciliation(
                NodeFeeSettlement.EffectStateKind.Unknown,
                null,
                NodeFeeSettlement.EffectStateKind.Failed,
                null),
            "both effects require chain reconciliation",
            now.AddMinutes(5),
            now.AddSeconds(2));

        deferred.IsError.Should().BeFalse(deferred.Message);
        deferred.Result.Should().BeTrue();
        var persisted = await _store.GetAsync(admitted.Result.Settlement.Id);
        persisted.Result!.State.Should().Be(NodeFeeSettlement.StateKind.AwaitingReconciliation);
        persisted.Result.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Unknown);
        persisted.Result.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Failed);
        persisted.Result.PrimaryTransactionHash.Should().BeNull();
        persisted.Result.FeeTransactionHash.Should().BeNull();
        (await _idempotency.GetAsync(parentKey, CancellationToken.None))!.State
            .Should().Be(AZOA.WebAPI.Models.Idempotency.IdempotencyState.InProgress);
    }

    [SkippableFact]
    public async Task ConfirmedEffectReference_IsMonotonicAcrossReconciliationAndPairedTerminalization()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var parentKey = "confirmed-reference:" + Guid.NewGuid().ToString("N");
        var admitted = await _store.AdmitAsync(CreateSettlement(parentKey, now), parentKey);
        var firstClaim = await _store.TryClaimRecoveryAsync(
            admitted.Result!.Settlement, "first-worker", now.AddSeconds(1), now.AddSeconds(2));
        var recorded = await _store.TryRecordNonTerminalReconciliationAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(firstClaim.Result!),
            new NodeFeeSettlementEffectReconciliation(
                NodeFeeSettlement.EffectStateKind.Confirmed, "primary:observed",
                NodeFeeSettlement.EffectStateKind.Failed, "fee:failed"),
            "fee effect requires reconciliation", now.AddSeconds(3), now.AddSeconds(1));

        recorded.IsError.Should().BeFalse(recorded.Message);
        recorded.Result.Should().BeTrue();
        var secondClaim = await _store.TryClaimRecoveryAsync(
            (await _store.GetAsync(admitted.Result.Settlement.Id)).Result!,
            "second-worker", now.AddSeconds(4), now.AddSeconds(5));
        var conflicting = await _store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(secondClaim.Result!),
            new NodeFeeSettlementTerminalization(
                parentKey, "primary:replacement", "fee:confirmed", "payload"),
            now.AddSeconds(4));

        conflicting.IsError.Should().BeFalse(conflicting.Message);
        conflicting.Result.Should().BeFalse();
        var afterConflict = await _store.GetAsync(admitted.Result.Settlement.Id);
        afterConflict.Result!.State.Should().Be(NodeFeeSettlement.StateKind.AwaitingReconciliation);
        afterConflict.Result.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Confirmed);
        afterConflict.Result.PrimaryTransactionHash.Should().Be("primary:observed");
        (await _idempotency.GetAsync(parentKey, CancellationToken.None))!.State
            .Should().Be(AZOA.WebAPI.Models.Idempotency.IdempotencyState.InProgress);

        var finalClaim = await _store.TryClaimRecoveryAsync(
            afterConflict.Result, "final-worker", now.AddSeconds(6), now.AddMinutes(1));
        var settled = await _store.TrySettlePairedAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(finalClaim.Result!),
            new NodeFeeSettlementTerminalization(
                parentKey, "primary:observed", "fee:confirmed", "payload"),
            now.AddSeconds(6));

        settled.IsError.Should().BeFalse(settled.Message);
        settled.Result.Should().BeTrue();
    }

    [SkippableFact]
    public async Task AcceptedAtomicGroup_RecordsExactReplayAndLeavesTheParentInProgress()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var parentKey = "accepted-group:" + Guid.NewGuid().ToString("N");
        var request = AtomicRequest(parentKey);
        var admitted = await _store.AdmitAsync(
            CreateSettlement(parentKey, now, request.GroupIdentity), parentKey);
        var claim = await _store.TryClaimRecoveryAsync(
            admitted.Result!.Settlement, "receipt-worker", now.AddSeconds(1), now.AddMinutes(1));
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, "chain:group-1", "primary:tx-1", "treasury:tx-2",
            AtomicTransferGroupSubmissionState.PendingConfirmation);

        var recorded = await _store.TryRecordAcceptedAtomicGroupAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(claim.Result!),
            new NodeFeeAcceptedAtomicGroup(request, submission), now.AddSeconds(2));
        var replay = await _store.TryRecordAcceptedAtomicGroupAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(claim.Result!),
            new NodeFeeAcceptedAtomicGroup(request, submission), now.AddSeconds(3));

        recorded.IsError.Should().BeFalse(recorded.Message);
        recorded.Result!.Id.Should().Be(NodeFeeAtomicGroup.RecordIdFor(admitted.Result.Settlement.Id));
        recorded.Result.ChainGroupId.Should().Be("chain:group-1");
        recorded.Result.SettlementId.Should().Be("node_fee_settlement:" + admitted.Result.Settlement.Id);
        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.Id.Should().Be(recorded.Result.Id);

        var settlement = await _store.GetAsync(admitted.Result.Settlement.Id);
        settlement.Result!.State.Should().Be(NodeFeeSettlement.StateKind.AwaitingReconciliation);
        settlement.Result.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Submitted);
        settlement.Result.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.Submitted);
        settlement.Result.PrimaryTransactionHash.Should().Be("primary:tx-1");
        settlement.Result.FeeTransactionHash.Should().Be("treasury:tx-2");
        settlement.Result.LeaseToken.Should().BeNull();
        (await _idempotency.GetAsync(parentKey, CancellationToken.None))!.State
            .Should().Be(AZOA.WebAPI.Models.Idempotency.IdempotencyState.InProgress);

        var divergent = AtomicTransferGroupSubmission.Accepted(
            request, "chain:other-group", "primary:tx-other", "treasury:tx-other",
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var conflict = await _store.TryRecordAcceptedAtomicGroupAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(claim.Result!),
            new NodeFeeAcceptedAtomicGroup(request, divergent), now.AddSeconds(4));
        conflict.IsError.Should().BeTrue();
        conflict.Message.Should().Contain("conflicts");
    }

    [SkippableFact]
    public async Task AcceptedAtomicGroup_RejectsStaleLeaseAndMismatchedEconomics()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var parentKey = "accepted-group-guard:" + Guid.NewGuid().ToString("N");
        var request = AtomicRequest(parentKey);
        var admitted = await _store.AdmitAsync(
            CreateSettlement(parentKey, now, request.GroupIdentity), parentKey);
        var oldClaim = await _store.TryClaimRecoveryAsync(
            admitted.Result!.Settlement, "old-worker", now.AddSeconds(1), now.AddSeconds(2));
        var currentClaim = await _store.TryClaimRecoveryAsync(
            oldClaim.Result!, "current-worker", now.AddSeconds(3), now.AddMinutes(1));
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, "chain:group-2", "primary:tx-3", "treasury:tx-4",
            AtomicTransferGroupSubmissionState.Submitted);

        var stale = await _store.TryRecordAcceptedAtomicGroupAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(oldClaim.Result!),
            new NodeFeeAcceptedAtomicGroup(request, submission), now.AddSeconds(4));
        stale.IsError.Should().BeFalse(stale.Message);
        stale.Result.Should().BeNull();

        var mismatch = AtomicRequest(parentKey, primaryAmount: 974);
        var mismatchSubmission = AtomicTransferGroupSubmission.Accepted(
            mismatch, "chain:group-3", "primary:tx-5", "treasury:tx-6",
            AtomicTransferGroupSubmissionState.Submitted);
        var rejected = await _store.TryRecordAcceptedAtomicGroupAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(currentClaim.Result!),
            new NodeFeeAcceptedAtomicGroup(mismatch, mismatchSubmission), now.AddSeconds(4));
        rejected.IsError.Should().BeTrue();
        rejected.Message.Should().Contain("immutable economics");

        var persisted = await _store.GetAsync(admitted.Result.Settlement.Id);
        persisted.Result!.LeaseToken.Should().Be("current-worker");
        persisted.Result.PrimaryEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
        persisted.Result.FeeEffectState.Should().Be(NodeFeeSettlement.EffectStateKind.NotStarted);
    }

    [SkippableFact]
    public async Task AcceptedAtomicGroup_RequiresMatchingPrecommittedIdentity()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var absentKey = "accepted-group-absent:" + Guid.NewGuid().ToString("N");
        var absentRequest = AtomicRequest(absentKey);
        var absentAdmission = await _store.AdmitAsync(CreateSettlement(absentKey, now), absentKey);
        var absentClaim = await _store.TryClaimRecoveryAsync(
            absentAdmission.Result!.Settlement, "absent-worker", now.AddSeconds(1), now.AddMinutes(1));
        var absent = await _store.TryRecordAcceptedAtomicGroupAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(absentClaim.Result!),
            new NodeFeeAcceptedAtomicGroup(absentRequest, AtomicTransferGroupSubmission.Accepted(
                absentRequest, "chain:absent", "primary:absent", "treasury:absent",
                AtomicTransferGroupSubmissionState.Submitted)), now.AddSeconds(2));
        absent.IsError.Should().BeTrue();
        absent.Message.Should().Contain("precommitted");

        var mismatchKey = "accepted-group-mismatch:" + Guid.NewGuid().ToString("N");
        var mismatchRequest = AtomicRequest(mismatchKey);
        var mismatchAdmission = await _store.AdmitAsync(
            CreateSettlement(mismatchKey, now, new string('a', 64)), mismatchKey);
        var mismatchClaim = await _store.TryClaimRecoveryAsync(
            mismatchAdmission.Result!.Settlement, "mismatch-worker", now.AddSeconds(1), now.AddMinutes(1));
        var mismatch = await _store.TryRecordAcceptedAtomicGroupAsync(
            NodeFeeSettlementRecoveryLease.FromClaim(mismatchClaim.Result!),
            new NodeFeeAcceptedAtomicGroup(mismatchRequest, AtomicTransferGroupSubmission.Accepted(
                mismatchRequest, "chain:mismatch", "primary:mismatch", "treasury:mismatch",
                AtomicTransferGroupSubmissionState.Submitted)), now.AddSeconds(2));
        mismatch.IsError.Should().BeTrue();
        mismatch.Message.Should().Contain("precommitted");
    }

    [SkippableFact]
    public async Task AcceptedAtomicGroup_ConcurrentExactAndDivergentEvidenceLeavesOneImmutableReceipt()
    {
        Skip.IfNot(_surrealAvailable,
            "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var now = DateTimeOffset.UtcNow;
        var parentKey = "accepted-group-concurrent:" + Guid.NewGuid().ToString("N");
        var request = AtomicRequest(parentKey);
        var admitted = await _store.AdmitAsync(
            CreateSettlement(parentKey, now, request.GroupIdentity), parentKey);
        var claim = await _store.TryClaimRecoveryAsync(
            admitted.Result!.Settlement, "concurrent-worker", now.AddSeconds(1), now.AddMinutes(1));
        var lease = NodeFeeSettlementRecoveryLease.FromClaim(claim.Result!);
        var exact = new NodeFeeAcceptedAtomicGroup(request, AtomicTransferGroupSubmission.Accepted(
            request, "chain:concurrent", "primary:concurrent", "treasury:concurrent",
            AtomicTransferGroupSubmissionState.Submitted));
        var divergent = new NodeFeeAcceptedAtomicGroup(request, AtomicTransferGroupSubmission.Accepted(
            request, "chain:divergent", "primary:divergent", "treasury:divergent",
            AtomicTransferGroupSubmissionState.Submitted));

        var results = await Task.WhenAll(
            _store.TryRecordAcceptedAtomicGroupAsync(lease, exact, now.AddSeconds(2)),
            _store.TryRecordAcceptedAtomicGroupAsync(lease, divergent, now.AddSeconds(2)));

        results.Should().ContainSingle(result => !result.IsError && result.Result != null);
        results.Should().ContainSingle(result => result.IsError && result.Message.Contains("conflicts", StringComparison.Ordinal));
        var persisted = await new DefaultSurrealExecutor(_connection).QuerySingleAsync<NodeFeeAtomicGroup>(
            SurrealQuery<NodeFeeAtomicGroup>.Key(NodeFeeAtomicGroup.RecordIdFor(admitted.Result.Settlement.Id)),
            CancellationToken.None);
        persisted.Should().NotBeNull();
        persisted!.PrimaryTransactionId.Should().BeOneOf("primary:concurrent", "primary:divergent");
    }

    private static AtomicTransferGroupRequest AtomicRequest(string parentKey, ulong primaryAmount = 975)
    {
        var provider = new AtomicGroupTestProvider();
        provider.Initialize(new BlockchainNetworkConfig(), AZOA.WebAPI.Core.ChainNetwork.Mainnet);
        var request = AtomicTransferGroupRequest.TryCreate(
            provider,
            "algorand",
            AZOA.WebAPI.Core.ChainNetwork.Mainnet,
            parentKey,
            new AtomicTransferEffect("42", "source:wallet", "recipient:wallet", primaryAmount, SigningContext.Platform),
            new AtomicTransferEffect("42", "source:wallet", "TREASURY", 25, SigningContext.Platform));
        request.IsError.Should().BeFalse(request.Message);
        return request.Result!;
    }

    private sealed class AtomicGroupTestProvider : BaseBlockchainProvider
    {
        public AtomicGroupTestProvider()
            : base(new ConfigurationBuilder().Build(), NullLogger<AtomicGroupTestProvider>.Instance)
        {
        }

        public override string ChainType => "algorand";
    }

    private async Task<NodeFeeSettlement> AdmitPreparedAsync(DateTimeOffset now)
    {
        var source = Guid.NewGuid().ToString("N");
        var settlement = CreateSettlement(source, now);
        var admitted = await _store.AdmitAsync(settlement, source);
        admitted.IsError.Should().BeFalse(admitted.Message);
        return admitted.Result!.Settlement;
    }

    private static NodeFeeSettlement CreateSettlement(
        string source,
        DateTimeOffset now,
        string? expectedAtomicGroupIdentity = null)
        => new()
        {
            Id = NodeFeeSettlement.RecordIdFor(source, "Transfer"),
            ParentIdempotencyKeyHash = NodeFeeSettlement.HashParentIdempotencyKey(source),
            Operation = "Transfer",
            Chain = "algorand",
            Network = "Mainnet",
            AssetId = "42",
            GrossAmount = "1000",
            FeeAmount = "25",
            NetAmount = "975",
            FeeScheduleVersion = 7,
            TreasuryAddress = "TREASURY",
            TreasuryDestinationVersion = 3,
            ExpectedAtomicGroupIdentity = expectedAtomicGroupIdentity,
            State = NodeFeeSettlement.StateKind.Prepared,
            PrimaryEffectState = NodeFeeSettlement.EffectStateKind.NotStarted,
            FeeEffectState = NodeFeeSettlement.EffectStateKind.NotStarted,
            StateVersion = 0,
            AttemptCount = 0,
            NextAttemptAt = now,
            UpdatedAt = now,
        };

    private static NodeFeeSettlementDraft Draft(
        string parentKey,
        string feeAmount = "25",
        string netAmount = "975") => new()
    {
        ParentIdempotencyKey = parentKey,
        Operation = NodeFeeOperation.Transfer,
        Chain = "algorand",
        Network = AZOA.WebAPI.Core.ChainNetwork.Mainnet,
        AssetId = "42",
        GrossAmount = "1000",
        FeeAmount = feeAmount,
        NetAmount = netAmount,
        FeeScheduleVersion = 7,
        TreasuryAddress = "TREASURY",
        TreasuryDestinationVersion = 3,
    };

    private static async Task<bool> ProbeSurrealAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(SurrealTestDefaults.Endpoint + "/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
