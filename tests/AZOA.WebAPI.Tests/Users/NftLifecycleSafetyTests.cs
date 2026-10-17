using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Users;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Users;

public sealed class NftLifecycleSafetyTests
{
    [Fact]
    public async Task UnknownAcceptedProtocolQuoteCannotAdmitOrPrepareMint()
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        var request = NftLifecycleHarness.MintRequest();
        request.ProtocolCostAuthorization = new(Guid.NewGuid().ToString("N"), new string('a', 64), new("1", "2", "3"));
        var result = await h.Service.MintAsync(actor, actor, request, "unknown-cost-quote", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        result.IsError.Should().BeTrue();
        h.ProtocolCosts.Verify(store => store.GetAsync(request.ProtocolCostAuthorization.QuoteId, actor, Guid.Empty,
            It.IsAny<CancellationToken>()), Times.Once);
        h.Prepared.Should().BeEmpty();
        h.Submitted.Should().BeEmpty();
        (await h.Service.GetRequestAsync(actor, "Mint", "unknown-cost-quote")).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task OverlappingMintPreparationsCanSubmitOnlyTheWinningPersistedIdentity()
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        h.BeforePrepare = async _ => { if (Interlocked.Increment(ref count) == 2) entered.TrySetResult(); await release.Task; };
        var first = h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "overlap", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        var second = h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "overlap", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        var persisted = await h.Service.GetRequestAsync(actor, "Mint", "overlap");
        persisted.Result!.State.Should().Be("Completed");
        h.Submitted.Select(item => item.TransactionId).Distinct().Should().ContainSingle();
        h.Store.Ownership.Should().ContainSingle();
    }

    [Fact]
    public async Task CancelDuringPreparationFencesSubmissionAndReleasesTheAssetReservation()
    {
        var h = new NftLifecycleHarness();
        var owner = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        h.AddWallet(owner);
        var destination = h.AddWallet(recipient);
        var mint = await h.Service.MintAsync(owner, owner, NftLifecycleHarness.MintRequest(), "initial", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(owner));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforePrepare = async _ => { entered.TrySetResult(); await release.Task; };
        var transfer = h.Service.MoveAsync(owner, mint.Result!.NftId, destination.Address, false, "paused", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(owner));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            (await h.Service.MoveAsync(owner, mint.Result.NftId, destination.Address, false, "competitor", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(owner))).IsError.Should().BeTrue();
            (await h.Service.CancelRequestAsync(owner, "Transfer", "paused")).Result!.State.Should().Be("Cancelled");
        }
        finally { release.TrySetResult(); }
        (await transfer.WaitAsync(TimeSpan.FromSeconds(10))).Result!.State.Should().Be("Cancelled");
        h.Submitted.Should().ContainSingle("only the original mint was sent");
        (await h.Store.GetAuthorityAsync(mint.Result.NftId.ToString("N")))!.ActiveLifecycleId.Should().BeNull();
    }

    [Fact]
    public async Task CancelledKeyFencesAnAlreadySuspendedAdmission()
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Store.BeforeClaim = row => row.Stage == "Cancellation" ? Task.CompletedTask : Pause();
        async Task Pause() { entered.SetResult(); await release.Task; }
        var mint = h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "same-key", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        AZOAResult<NftLifecycleView> cancelled;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancelled = await h.Service.CancelRequestAsync(actor, "Mint", "same-key");
        }
        finally { release.TrySetResult(); }
        cancelled.Result!.State.Should().Be("Cancelled");
        (await mint).Result!.State.Should().Be("Cancelled");
        h.Prepared.Should().BeEmpty();
        h.Submitted.Should().BeEmpty();
        (await h.Service.RefreshAsync(actor, cancelled.Result.OperationId, false)).Result!.State.Should().Be("Cancelled");
        (await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "same-key", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor))).Result!.State.Should().Be("Cancelled");
        (await h.Service.GetRequestAsync(actor, "Mint", "same-key", Guid.NewGuid())).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task CancellationCannotReplaceASubmittedRequestOrItsWire()
    {
        var h = new NftLifecycleHarness { Confirm = false };
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        var minted = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "pending", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        minted.Result!.State.Should().Be("Submitted");
        (await h.Service.CancelRequestAsync(actor, "Mint", "pending")).IsError.Should().BeTrue();
        var first = h.Submitted.Single();
        await h.Service.RefreshAsync(actor, minted.Result.OperationId, false);
        h.Submitted.Should().HaveCount(2);
        h.Submitted.Should().OnlyContain(item => item.TransactionId == first.TransactionId && item.SignedTransactionBase64 == first.SignedTransactionBase64);
        h.Prepared.Should().ContainSingle();
    }

    [Fact]
    public async Task LostSubmitResponseRecoversSameMintWithoutNewPreparation()
    {
        var h = new NftLifecycleHarness { LoseSubmitResponse = true };
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        var initial = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(false), "lost", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        initial.Result!.State.Should().Be("Submitted");
        var recovered = await h.Service.RefreshAsync(actor, initial.Result.OperationId, false);
        recovered.Result!.State.Should().Be("Completed");
        h.Prepared.Should().ContainSingle();
        h.Submitted.Should().ContainSingle();
        h.Store.Ownership.Values.Single().IsTransferable.Should().BeFalse();
    }

    [Fact]
    public async Task CrashBeforeSendCanBroadcastThePersistedWireOnRecovery()
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        h.Module.Setup(module => module.SubmitNftTransactionAsync(It.IsAny<PreparedNftTransaction>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Process lost before transport"));
        var initial = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "before-send", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        initial.Result!.State.Should().Be("Submitted");
        h.Submitted.Should().BeEmpty();
        h.Module.Setup(module => module.SubmitNftTransactionAsync(It.IsAny<PreparedNftTransaction>(), It.IsAny<CancellationToken>()))
            .Returns((PreparedNftTransaction prepared, CancellationToken _) => { h.Submitted.Enqueue(prepared); return Task.CompletedTask; });
        var recovered = await h.Service.RefreshAsync(actor, initial.Result.OperationId, false);
        recovered.Result!.State.Should().Be("Completed");
        h.Prepared.Should().ContainSingle();
        h.Submitted.Should().ContainSingle();
    }

    [Fact]
    public async Task AmbiguousExpiryCannotPrepareAReplacement()
    {
        var h = new NftLifecycleHarness { ObservationOverride = "NeedsOperator" };
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        var first = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "ambiguous", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        first.Result!.State.Should().Be("NeedsOperator");
        await h.Service.RefreshAsync(actor, first.Result.OperationId, false);
        h.Prepared.Should().ContainSingle();
        h.Submitted.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ExpiredAbsent")]
    [InlineData("FailedOnChain")]
    public async Task AuthoritativeNoEffectAllowsCancellationWithoutDeletingHistory(string verdict)
    {
        var h = new NftLifecycleHarness { ObservationOverride = verdict };
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        var result = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "no-effect", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        result.Result!.State.Should().Be("Preparing");
        (await h.Service.CancelAsync(actor, result.Result.OperationId)).Result!.State.Should().Be("Cancelled");
        var row = await h.Store.GetAsync(result.Result.OperationId);
        row!.PreparationGeneration.Should().Be(1);
        row.TransactionHistoryJson.Should().Contain("SignedTransactionBase64");
        row.ConfirmedStageCount.Should().Be(0);
    }

    [Theory]
    [InlineData("ExpiredAbsent")]
    [InlineData("FailedOnChain")]
    public async Task NoEffectEvidenceForAnotherTransactionCannotAuthorizeReplacement(string verdict)
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        h.Module.Setup(module => module.ObserveNftTransactionAsync(It.IsAny<PreparedNftTransaction>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PreparedNftTransaction prepared, CancellationToken _) => AZOAResult<NftTransactionObservation>.Success(
                new(verdict, "another-transaction", null, null, prepared.ProtocolIdentity, prepared.ContractAddress)));
        var result = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "wrong-evidence", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        result.IsError.Should().BeTrue();
        var persisted = await h.Service.GetRequestAsync(actor, "Mint", "wrong-evidence");
        var row = await h.Store.GetAsync(persisted.Result!.OperationId);
        row!.State.Should().Be("Submitted");
        row.PreparationGeneration.Should().Be(0);
        h.Prepared.Should().ContainSingle();
    }

    [Fact]
    public async Task NonzeroFeeRequiresExactQuoteAndReplayKeepsItsOriginalTerms()
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        h.Configuration["NftFees:Policies:0:MintNativeBaseUnits"] = "50";
        var request = NftLifecycleHarness.MintRequest();
        (await h.Service.MintAsync(actor, actor, request, "quoted", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor))).Code.Should().Be("NFT_FEE_AUTHORIZATION_REQUIRED");
        h.Prepared.Should().BeEmpty();
        var quote = (await h.Service.QuoteFeeAsync(actor, "Algorand", "Mint")).Result!;
        request.FeeAuthorization = new(quote.Revision, quote.NativeBaseUnits, quote.QuoteId);
        var created = await h.Service.MintAsync(actor, actor, request, "quoted", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        created.Result!.State.Should().Be("Completed");
        h.Prepared.Single().Fee!.Quote.Should().Be(quote);
        h.Configuration["NftFees:Policies:0:MintNativeBaseUnits"] = "100";
        h.Configuration["NftFees:Policies:0:TreasuryAddress"] = "changed-treasury";
        var replay = await h.Service.MintAsync(actor, actor, request, "quoted", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        replay.Result!.FeeQuote.Should().Be(quote);
        h.Submitted.Should().ContainSingle();
    }

    [Fact]
    public void FeeAuthorizationBindsNetworkTreasuryPayerAndOperationEvenWithSameRevision()
    {
        var quote = new NftFeeQuote("Algorand", ChainNetwork.Devnet, "Transfer", 3, "ALGO", 6, "20", "treasury", Guid.NewGuid());
        var authorization = new NftFeeAuthorization(3, "20", quote.QuoteId);
        NftNativeFeePolicy.IsAuthorized(quote, authorization).Should().BeTrue();
        foreach (var changed in new[] { quote with { Network = ChainNetwork.Testnet }, quote with { TreasuryAddress = "other" },
            quote with { PayerUserId = Guid.NewGuid() }, quote with { Operation = "Mint" }, quote with { Currency = "ETH" } })
            NftNativeFeePolicy.IsAuthorized(changed, authorization).Should().BeFalse();
    }

    [Fact]
    public async Task RefundRequiresOriginalTransferAndCurrentHolderAuthorization()
    {
        var h = new NftLifecycleHarness();
        var sender = Guid.NewGuid();
        var holder = Guid.NewGuid();
        h.AddWallet(sender);
        var destination = h.AddWallet(holder);
        var mint = await h.Service.MintAsync(sender, sender, NftLifecycleHarness.MintRequest(), "mint", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(sender));
        var transfer = await h.Service.MoveAsync(sender, mint.Result!.NftId, destination.Address, false, "transfer", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(sender));
        transfer.Result!.State.Should().Be("Completed");
        (await h.Service.RefundAsync(holder, transfer.Result.OperationId, "invalid", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(holder))).IsError.Should().BeTrue();
        var refund = await h.Service.RefundAsync(sender, transfer.Result.OperationId, "refund", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(sender));
        refund.Result!.State.Should().Be("AwaitingRefundAuthorization");
        var sentBefore = h.Submitted.Count;
        (await h.Service.RefreshAsync(sender, refund.Result.OperationId, true, credential: NftLifecycleHarness.Credential(sender), acceptance: await h.Acceptance(refund.Result.OperationId))).IsError.Should().BeTrue();
        h.Submitted.Should().HaveCount(sentBefore);
        var accepted = await h.Service.RefreshAsync(holder, refund.Result.OperationId, true, credential: NftLifecycleHarness.Credential(holder), acceptance: await h.Acceptance(refund.Result.OperationId));
        accepted.Result!.State.Should().Be("Completed");
        h.Prepared.Last().SignerAddress.Should().Be(destination.Address);
        h.Prepared.Last().RecipientAddress.Should().Be(sender.ToString("N"));
        (await h.Store.GetAuthorityAsync(mint.Result.NftId.ToString("N")))!.HolderUserId.Should().Be(sender.ToString("N"));
    }
}
