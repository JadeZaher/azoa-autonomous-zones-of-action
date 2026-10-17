using AZOA.WebAPI.Tests.TestSupport;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Services.Users;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace AZOA.WebAPI.Tests.Users;

public sealed class NftLifecycleReconciliationTests
{
    [Fact]
    public async Task ConfirmedAcceptanceDoesNotSignTheNextDeliveryStage()
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid(); var recipient = Guid.NewGuid();
        h.AddWallet(actor); h.AddWallet(recipient);
        var request = NftLifecycleHarness.MintRequest(); request.IsSoulbound = true;
        var issued = await h.Service.MintAsync(actor, recipient, request, "accepted", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        issued.Result!.State.Should().Be("AwaitingRecipientAcceptance");
        h.Confirm = false;
        var accepting = await h.Service.RefreshAsync(recipient, issued.Result.OperationId, true, credential: NftLifecycleHarness.Credential(recipient), acceptance: await h.Acceptance(issued.Result.OperationId));
        accepting.Result!.State.Should().Be("Submitted");
        var signatures = h.Prepared.Count;
        h.Confirm = true;

        var observed = await h.Service.ReconcileAsync(issued.Result.OperationId);

        observed.Result!.State.Should().Be("Preparing"); observed.Result.Stage.Should().Be("Deliver");
        h.Prepared.Count.Should().Be(signatures);
        (await h.Store.ListPendingAsync(50)).Should().BeEmpty();
    }

    [Fact]
    public async Task FailedOldestBatchRotatesAndDoesNotStarveTheNextRecord()
    {
        var h = new NftLifecycleHarness { Confirm = false };
        var actor = Guid.NewGuid(); h.AddWallet(actor);
        var initial = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "fairness", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        var original = (await h.Store.GetAsync(initial.Result!.OperationId))!;
        var pending = new List<string>();
        for (var index = 0; index < 51; index++)
        {
            var row = JsonSerializer.Deserialize<AZOA.WebAPI.Persistence.SurrealDb.Models.NftLifecycle>(JsonSerializer.Serialize(original))!;
            row.Id = Guid.NewGuid().ToString("N"); row.Version = 0;
            row.UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(index);
            await h.Store.ClaimAsync(row); pending.Add(row.Id);
        }
        h.Module.Setup(module => module.ObserveNftTransactionAsync(It.IsAny<PreparedNftTransaction>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider outage"));
        using var services = new ServiceCollection().AddSingleton<INftLifecycleStore>(h.Store)
            .AddSingleton<INftLifecycleReconciler>(h.Service).BuildServiceProvider();
        using var worker = new NftLifecycleRecoveryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            new Mock<ILogger<NftLifecycleRecoveryWorker>>().Object);

        await worker.SweepAsync(default);
        (await h.Store.GetAsync(pending[49]))!.UpdatedAt.Should().BeAfter(DateTimeOffset.UnixEpoch.AddDays(1));
        (await h.Store.GetAsync(pending[50]))!.UpdatedAt.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(50));
        await worker.SweepAsync(default);
        (await h.Store.GetAsync(pending[50]))!.UpdatedAt.Should().BeAfter(DateTimeOffset.UnixEpoch.AddDays(1));
    }

    [Fact]
    public async Task SubmittedTransactionConvergesWithoutPreparingAnotherSignature()
    {
        var h = new NftLifecycleHarness { Confirm = false };
        var actor = Guid.NewGuid(); h.AddWallet(actor);
        var pending = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "worker", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        pending.Result!.State.Should().Be("Submitted");
        var prepared = h.Prepared.Count;
        h.Confirm = true;

        var completed = await h.Service.ReconcileAsync(pending.Result.OperationId);

        completed.Result!.State.Should().Be("Completed");
        h.Prepared.Count.Should().Be(prepared);
        h.Submitted.Select(item => item.TransactionId).Distinct().Should().ContainSingle();
        h.Store.Ownership.Should().ContainKey(completed.Result.NftId.ToString("N"));
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    [InlineData("AwaitingRecipientAcceptance")]
    [InlineData("AwaitingCreatorDestruction")]
    [InlineData("AwaitingRefundAuthorization")]
    [InlineData("AwaitingRefundDestinationAcceptance")]
    public async Task WorkerCannotAuthorizePreparationOrParticipantStages(string state)
    {
        var h = new NftLifecycleHarness { Confirm = false };
        var actor = Guid.NewGuid(); h.AddWallet(actor);
        var result = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "restricted", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        var row = (await h.Store.GetAsync(result.Result!.OperationId))!;
        row.State = state;
        await h.Store.UpdateAsync(row, row.Version);
        var signatures = h.Prepared.Count; var sends = h.Submitted.Count;

        (await h.Service.ReconcileAsync(row.Id)).Result!.State.Should().Be(state);

        h.Prepared.Count.Should().Be(signatures); h.Submitted.Count.Should().Be(sends);
        (await h.Store.ListPendingAsync(50)).Should().BeEmpty();
    }

    [Fact]
    public async Task FinalNoEffectProofStopsBeforeReplacementPreparation()
    {
        var h = new NftLifecycleHarness { Confirm = false };
        var actor = Guid.NewGuid(); h.AddWallet(actor);
        var result = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "expired", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        var signatures = h.Prepared.Count;
        h.ObservationOverride = "ExpiredAbsent";

        var reconciled = await h.Service.ReconcileAsync(result.Result!.OperationId);

        reconciled.Result!.State.Should().Be("Preparing");
        h.Prepared.Count.Should().Be(signatures);
        (await h.Store.ListPendingAsync(50)).Should().BeEmpty();
        var row = await h.Store.GetAsync(result.Result.OperationId);
        row!.PreparationGeneration.Should().Be(1); row.TransactionHistoryJson.Should().Contain("ExpiredAbsent");
    }

    [Fact]
    public async Task LosingReconciliationCasReloadsInteractiveStateWithoutSending()
    {
        var h = new NftLifecycleHarness { Confirm = false };
        var actor = Guid.NewGuid(); h.AddWallet(actor);
        var result = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "overlap", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Store.BeforeUpdate = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); };
        var pending = h.Service.ReconcileAsync(result.Result!.OperationId);
        var signatures = h.Prepared.Count; var sends = h.Submitted.Count;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            h.Store.BeforeUpdate = null;
            var interactive = (await h.Store.GetAsync(result.Result.OperationId))!;
            interactive.State = "Prepared";
            await h.Store.UpdateAsync(interactive, interactive.Version);
        }
        finally { release.TrySetResult(); }

        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Result!.State.Should().Be("Prepared");
        h.Prepared.Count.Should().Be(signatures); h.Submitted.Count.Should().Be(sends);
    }
}
