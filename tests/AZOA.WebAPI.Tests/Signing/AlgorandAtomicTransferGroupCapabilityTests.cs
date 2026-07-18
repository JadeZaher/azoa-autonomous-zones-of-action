using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Services.Signing;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class AlgorandAtomicTransferGroupCapabilityTests
{
    private static readonly string ValidGroupId = Convert.ToBase64String(new byte[32]);
    private static readonly string ValidPrimaryTransactionId = new('A', 52);
    private static readonly string ValidTreasuryTransactionId = new string('A', 51) + 'Q';

    [Fact]
    public async Task Invalid_group_input_fails_before_http_or_custody()
    {
        using var handler = new AlgodStub();
        var custody = new Mock<IKeyCustodyService>(MockBehavior.Strict);
        var provider = NewProvider(new RecordingSigner(), handler, custody.Object);
        var request = GroupRequest(
            source: "not-an-algorand-address",
            signingContext: SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()));

        var result = await provider.SubmitAtomicTransferGroupAsync(request);

        result.IsError.Should().BeTrue();
        handler.RequestCount.Should().Be(0);
        custody.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Group_uses_one_sdk_group_id_and_one_batch_broadcast()
    {
        using var handler = new AlgodStub(pendingResponse: _ => Confirmed());
        var platform = new AlgoAccount();
        var signer = new RecordingSigner();
        var provider = NewProvider(signer, handler, platform: platform);
        var request = GroupRequest(source: platform.Address.EncodeAsString());

        provider.TryGetModule<IAtomicTransferGroupModule>(out var module).Should().BeTrue();
        module!.SupportsAtomicTransferGroups.Should().BeTrue();

        var result = await module.SubmitAtomicTransferGroupAsync(request);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(AtomicTransferGroupSubmissionState.Confirmed);
        result.Result.ChainGroupId.Should().NotBeNullOrWhiteSpace();
        signer.Groups.Should().HaveCount(2);
        signer.Groups.Distinct(StringComparer.Ordinal).Should().ContainSingle();
        handler.BroadcastCount.Should().Be(1);
        handler.LastBroadcast.Should().HaveCount(2, "both signed legs are one Algod batch body");
    }

    [Fact]
    public async Task Group_sender_must_match_the_resolved_signing_key()
    {
        using var handler = new AlgodStub();
        var provider = NewProvider(new RecordingSigner(), handler, platform: new AlgoAccount());

        var result = await provider.SubmitAtomicTransferGroupAsync(
            GroupRequest(source: new AlgoAccount().Address.EncodeAsString()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("shared sender");
        handler.BroadcastCount.Should().Be(0);
    }

    [Fact]
    public async Task Real_signer_emits_decodable_signed_group_with_bound_sender()
    {
        using var handler = new AlgodStub(pendingResponse: _ => Confirmed());
        var platform = new AlgoAccount();
        var signer = new CapturingRealSigner();
        var provider = NewProvider(signer, handler, platform: platform);

        var result = await provider.SubmitAtomicTransferGroupAsync(
            GroupRequest(source: platform.Address.EncodeAsString()));

        result.IsError.Should().BeFalse(result.Message);
        signer.Envelopes.Should().HaveCount(2);
        var signed = signer.Envelopes
            .Select(Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>)
            .ToArray();
        signed.Should().OnlyContain(envelope => envelope.Sig != null);
        signed.Select(envelope => envelope.Tx.Group!.ToString()).Distinct(StringComparer.Ordinal).Should().ContainSingle();
        signed.Should().OnlyContain(envelope =>
            envelope.Tx.Sender.EncodeAsString() == platform.Address.EncodeAsString());
        handler.LastBroadcast.Should().Equal(signer.Envelopes.SelectMany(static envelope => envelope));
    }

    [Fact]
    public async Task Custody_consent_failure_never_broadcasts()
    {
        using var handler = new AlgodStub();
        var custody = new Mock<IKeyCustodyService>();
        custody.Setup(c => c.WithSigningKeyAsync(
                It.IsAny<SigningContext>(),
                It.IsAny<Func<byte[], Task<AZOAResult<byte[][]>>>>()))
            .ReturnsAsync(new AZOAResult<AZOAResult<byte[][]>>
            {
                IsError = true,
                Message = "Tenant consent grant is missing.",
            });
        var user = new AlgoAccount();
        var provider = NewProvider(new RecordingSigner(), handler, custody.Object);
        var request = GroupRequest(
            source: user.Address.EncodeAsString(),
            signingContext: SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()));

        var result = await provider.SubmitAtomicTransferGroupAsync(request);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Tenant consent");
        handler.BroadcastCount.Should().Be(0);
        custody.Verify(c => c.WithSigningKeyAsync(
            It.IsAny<SigningContext>(),
            It.IsAny<Func<byte[], Task<AZOAResult<byte[][]>>>>()), Times.Once);
    }

    [Fact]
    public async Task Confirmation_timeout_is_recoverable_with_both_transaction_ids()
    {
        using var handler = new AlgodStub(pendingResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var platform = new AlgoAccount();
        var provider = NewProvider(new RecordingSigner(), handler, platform: platform);

        var result = await provider.SubmitAtomicTransferGroupAsync(GroupRequest(source: platform.Address.EncodeAsString()));

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(AtomicTransferGroupSubmissionState.PendingConfirmation);
        result.Result.ChainGroupId.Should().NotBeNullOrWhiteSpace();
        result.Result.PrimaryTransactionId.Should().NotBeNullOrWhiteSpace();
        result.Result.TreasuryTransactionId.Should().NotBeNullOrWhiteSpace();
        result.Result.PrimaryTransactionId.Should().NotBe(result.Result.TreasuryTransactionId);
        handler.BroadcastCount.Should().Be(1);
    }

    [Fact]
    public async Task Confirmation_infrastructure_error_is_not_misclassified_as_pending()
    {
        using var handler = new AlgodStub(pendingResponse: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var platform = new AlgoAccount();
        var provider = NewProvider(new RecordingSigner(), handler, platform: platform);

        var result = await provider.SubmitAtomicTransferGroupAsync(GroupRequest(source: platform.Address.EncodeAsString()));

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Failed to read atomic transfer group leg");
        handler.BroadcastCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_during_group_parameter_lookup_bubbles_to_the_caller()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new CancellingParamsHandler(cancellation);
        var platform = new AlgoAccount();
        var provider = NewProvider(new RecordingSigner(), handler, platform: platform);

        var action = () => provider.SubmitAtomicTransferGroupAsync(
            GroupRequest(source: platform.Address.EncodeAsString()), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.BroadcastCount.Should().Be(0);
    }

    [Fact]
    public async Task One_confirmed_leg_is_nonterminal_until_group_reconciliation()
    {
        var pendingReads = 0;
        using var handler = new AlgodStub(_ =>
            Interlocked.Increment(ref pendingReads) == 1
                ? Confirmed()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var platform = new AlgoAccount();
        var provider = NewProvider(new RecordingSigner(), handler, platform: platform);

        var result = await provider.SubmitAtomicTransferGroupAsync(GroupRequest(source: platform.Address.EncodeAsString()));

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.State.Should().Be(AtomicTransferGroupSubmissionState.PendingConfirmation);
        result.Message.Should().Contain("incomplete confirmation observation");
        handler.BroadcastCount.Should().Be(1);
    }

    [Fact]
    public async Task Observation_confirms_only_exact_two_leg_indexer_evidence_at_one_round()
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission);
        observation.IsError.Should().BeFalse(observation.Message);

        using var handler = new AtomicGroupObservationHandler((transactionId, _) => IndexedAtomicTransfer(
            transactionId,
            submission.ChainGroupId,
            transactionId == submission.PrimaryTransactionId ? request.Primary : request.Treasury,
            confirmedRound: 42));
        var provider = NewObservationProvider(handler);

        provider.TryGetModule<IAtomicTransferGroupObservationModule>(out var module).Should().BeTrue();
        var result = await module!.ObserveAtomicTransferGroupAsync(observation.Result!);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be(AtomicTransferGroupObservationVerdict.Confirmed);
        result.Result.Primary.ConfirmedRound.Should().Be(42);
        result.Result.Treasury.ConfirmedRound.Should().Be(42);
        handler.IndexerRequestCount.Should().Be(2);
        handler.AlgodRequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Observation_marks_confirmed_evidence_with_wrong_amount_as_mismatched()
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;

        using var handler = new AtomicGroupObservationHandler((transactionId, _) =>
            transactionId == submission.PrimaryTransactionId
                ? IndexedAtomicTransfer(transactionId, submission.ChainGroupId, request.Primary with { Amount = 91 }, 42)
                : IndexedAtomicTransfer(transactionId, submission.ChainGroupId, request.Treasury, 42));
        var provider = NewObservationProvider(handler);

        var result = await provider.ObserveAtomicTransferGroupAsync(observation);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be(AtomicTransferGroupObservationVerdict.Mismatched);
        result.Result.Primary.Verdict.Should().Be(AtomicTransferLegObservationVerdict.Mismatched);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("group")]
    [InlineData("sender")]
    [InlineData("type")]
    [InlineData("asset")]
    [InlineData("receiver")]
    [InlineData("amount")]
    [InlineData("close-to")]
    [InlineData("clawback")]
    [InlineData("rekey")]
    public async Task Observation_rejects_each_hostile_confirmed_indexer_field(string field)
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;
        using var handler = new AtomicGroupObservationHandler((transactionId, _) =>
            IndexedAtomicTransfer(
                transactionId,
                submission.ChainGroupId,
                transactionId == submission.PrimaryTransactionId ? request.Primary : request.Treasury,
                42,
                transaction => CorruptIndexedField(transaction, field)));
        var provider = NewObservationProvider(handler);

        var result = await provider.ObserveAtomicTransferGroupAsync(observation);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be(AtomicTransferGroupObservationVerdict.Mismatched);
        result.Result.Primary.Verdict.Should().Be(AtomicTransferLegObservationVerdict.Mismatched);
        result.Result.Treasury.Verdict.Should().Be(AtomicTransferLegObservationVerdict.Mismatched);
    }

    [Fact]
    public async Task Observation_treats_nonpositive_indexer_round_as_nonterminal()
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;
        using var handler = new AtomicGroupObservationHandler((transactionId, _) =>
            IndexedAtomicTransfer(
                transactionId,
                submission.ChainGroupId,
                transactionId == submission.PrimaryTransactionId ? request.Primary : request.Treasury,
                confirmedRound: 0));
        var provider = NewObservationProvider(handler);

        var result = await provider.ObserveAtomicTransferGroupAsync(observation);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be(AtomicTransferGroupObservationVerdict.Incomplete);
        result.Result.Primary.Verdict.Should().Be(AtomicTransferLegObservationVerdict.Pending);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("server-error")]
    public async Task Observation_marks_malformed_or_failed_indexer_evidence_unavailable(string mode)
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;
        using var handler = new AtomicGroupObservationHandler((_, _) => mode == "malformed"
            ? RawJson("{")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = NewObservationProvider(handler);

        var result = await provider.ObserveAtomicTransferGroupAsync(observation);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be(AtomicTransferGroupObservationVerdict.Unavailable);
        result.Result.Primary.Verdict.Should().Be(AtomicTransferLegObservationVerdict.Unavailable);
    }

    [Fact]
    public async Task Observation_requires_both_confirmed_legs_in_the_same_round()
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;

        using var handler = new AtomicGroupObservationHandler((transactionId, _) =>
            transactionId == submission.PrimaryTransactionId
                ? IndexedAtomicTransfer(transactionId, submission.ChainGroupId, request.Primary, 42)
                : IndexedAtomicTransfer(transactionId, submission.ChainGroupId, request.Treasury, 43));
        var provider = NewObservationProvider(handler);

        var result = await provider.ObserveAtomicTransferGroupAsync(observation);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be(AtomicTransferGroupObservationVerdict.Mismatched);
        result.Result.Primary.Verdict.Should().Be(AtomicTransferLegObservationVerdict.Confirmed);
        result.Result.Treasury.Verdict.Should().Be(AtomicTransferLegObservationVerdict.Confirmed);
    }

    [Fact]
    public async Task Observation_uses_algod_only_to_classify_indexer_lag_as_nonterminal()
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;

        using var handler = new AtomicGroupObservationHandler(
            (_, isIndexer) => isIndexer
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(new Dictionary<string, object?> { ["confirmed-round"] = 0, ["pool-error"] = string.Empty }));
        var provider = NewObservationProvider(handler);

        var result = await provider.ObserveAtomicTransferGroupAsync(observation);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be(AtomicTransferGroupObservationVerdict.Incomplete);
        result.Result.Primary.Verdict.Should().Be(AtomicTransferLegObservationVerdict.Pending);
        result.Result.Treasury.Verdict.Should().Be(AtomicTransferLegObservationVerdict.Pending);
        handler.AlgodRequestCount.Should().Be(2);
    }

    [Theory]
    [InlineData("unseen")]
    [InlineData("rejected")]
    public async Task Observation_classifies_algod_unseen_and_pool_rejection_after_indexer_lag(string mode)
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;
        using var handler = new AtomicGroupObservationHandler((_, isIndexer) =>
        {
            if (isIndexer)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return mode == "unseen"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(new Dictionary<string, object?> { ["confirmed-round"] = 0, ["pool-error"] = "rejected" });
        });
        var provider = NewObservationProvider(handler);

        var result = await provider.ObserveAtomicTransferGroupAsync(observation);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be(mode == "unseen"
            ? AtomicTransferGroupObservationVerdict.Incomplete
            : AtomicTransferGroupObservationVerdict.Rejected);
        result.Result.Primary.Verdict.Should().Be(mode == "unseen"
            ? AtomicTransferLegObservationVerdict.Unseen
            : AtomicTransferLegObservationVerdict.PoolRejected);
    }

    [Fact]
    public async Task Observation_marks_noncaller_indexer_timeout_unavailable()
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;
        using var handler = new AtomicGroupObservationHandler((_, _) => throw new OperationCanceledException());
        var provider = NewObservationProvider(handler);

        var result = await provider.ObserveAtomicTransferGroupAsync(observation);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be(AtomicTransferGroupObservationVerdict.Unavailable);
    }

    [Fact]
    public async Task Observation_rejects_non_native_identifiers_before_any_uri_or_network_access()
    {
        var request = GroupRequest();
        foreach (var (groupId, primaryTransactionId) in new[]
                 {
                     ("not-base64", ValidPrimaryTransactionId),
                     (ValidGroupId, "../../hostile-uri"),
                 })
        {
            var submission = AtomicTransferGroupSubmission.Accepted(
                request, groupId, primaryTransactionId, ValidTreasuryTransactionId,
                AtomicTransferGroupSubmissionState.PendingConfirmation);
            var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission);
            observation.IsError.Should().BeFalse(observation.Message, "generic contracts retain provider-neutral identifiers");
            using var handler = new AtomicGroupObservationHandler((_, _) =>
                throw new InvalidOperationException("Invalid native identifiers must not reach HTTP."));
            var provider = NewObservationProvider(handler);

            var result = await provider.ObserveAtomicTransferGroupAsync(observation.Result!);

            result.IsError.Should().BeTrue();
            handler.IndexerRequestCount.Should().Be(0);
            handler.AlgodRequestCount.Should().Be(0);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("+12345")]
    public async Task Observation_rejects_nonpositive_or_noncanonical_asa_before_network_access(string assetId)
    {
        var request = GroupRequest(assetId: assetId);
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;
        using var handler = new AtomicGroupObservationHandler((_, _) =>
            throw new InvalidOperationException("Invalid ASA identifiers must not reach HTTP."));
        var provider = NewObservationProvider(handler);

        var result = await provider.ObserveAtomicTransferGroupAsync(observation);

        result.IsError.Should().BeTrue();
        handler.IndexerRequestCount.Should().Be(0);
        handler.AlgodRequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Observation_mismatch_dominates_pool_rejection()
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;
        using var handler = new AtomicGroupObservationHandler((transactionId, isIndexer) =>
        {
            if (transactionId == submission.PrimaryTransactionId)
            {
                return IndexedAtomicTransfer(
                    transactionId, submission.ChainGroupId, request.Primary with { Amount = 91 }, 42);
            }

            return isIndexer
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(new Dictionary<string, object?> { ["confirmed-round"] = 0, ["pool-error"] = "rejected" });
        });
        var provider = NewObservationProvider(handler);

        var result = await provider.ObserveAtomicTransferGroupAsync(observation);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Verdict.Should().Be(AtomicTransferGroupObservationVerdict.Mismatched);
        result.Result.Primary.Verdict.Should().Be(AtomicTransferLegObservationVerdict.Mismatched);
        result.Result.Treasury.Verdict.Should().Be(AtomicTransferLegObservationVerdict.PoolRejected);
    }

    [Fact]
    public async Task Observation_preserves_caller_cancellation()
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);
        var observation = AtomicTransferGroupObservationRequest.TryCreate(request, submission).Result!;
        using var cancellation = new CancellationTokenSource();
        using var handler = new CancellingObservationHandler(cancellation);
        var provider = NewObservationProvider(handler);

        var action = () => provider.ObserveAtomicTransferGroupAsync(observation, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Observation_request_rejects_unbound_or_nonaccepted_submission()
    {
        var request = GroupRequest();
        var other = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            other, ValidGroupId, ValidPrimaryTransactionId, ValidTreasuryTransactionId,
            AtomicTransferGroupSubmissionState.PendingConfirmation);

        var result = AtomicTransferGroupObservationRequest.TryCreate(request, submission);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("not bound");
    }

    [Fact]
    public void Observation_request_rejects_reusing_the_group_identifier_as_a_leg()
    {
        var request = GroupRequest();
        var submission = AtomicTransferGroupSubmission.Accepted(
            request, "provider-neutral-group", "provider-neutral-group", "other-leg",
            AtomicTransferGroupSubmissionState.PendingConfirmation);

        var result = AtomicTransferGroupObservationRequest.TryCreate(request, submission);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("distinct");
    }

    private static AlgorandProvider NewProvider(
        ITransactionSigner signer,
        HttpMessageHandler handler,
        IKeyCustodyService? custody = null,
        AlgoAccount? platform = null)
    {
        platform ??= new AlgoAccount();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = "unit-test-wallet-encryption-key-0123456789",
                ["AZOA:Algorand:PlatformMnemonic"] = platform.ToMnemonic(),
            })
            .Build();
        var provider = new AlgorandProvider(
            config,
            NullLogger<AlgorandProvider>.Instance,
            new TransactionSignerFactory(new[] { signer }),
            new WalletKeyService(config),
            custody,
            custodyScopeFactory: null,
            faucet: null,
            httpMessageHandler: handler);
        provider.Initialize(new BlockchainNetworkConfig { NodeUrl = "http://algod.test/" }, ChainNetwork.Devnet);
        return provider;
    }

    private static AlgorandProvider NewObservationProvider(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().Build();
        var provider = new AlgorandProvider(
            config,
            NullLogger<AlgorandProvider>.Instance,
            signerFactory: null,
            keyService: null,
            custodyService: null,
            custodyScopeFactory: null,
            faucet: null,
            httpMessageHandler: handler);
        provider.Initialize(new BlockchainNetworkConfig
        {
            NodeUrl = "http://algod.test/",
            IndexerUrl = "http://indexer.test/",
        }, ChainNetwork.Devnet);
        return provider;
    }

    private static HttpResponseMessage IndexedAtomicTransfer(
        string transactionId,
        string groupId,
        AtomicTransferEffect effect,
        long confirmedRound,
        Action<Dictionary<string, object?>>? mutate = null)
    {
        var transaction = new Dictionary<string, object?>
        {
            ["id"] = transactionId,
            ["confirmed-round"] = confirmedRound,
            ["group"] = groupId,
            ["sender"] = effect.FromAddress,
            ["tx-type"] = "axfer",
            ["asset-transfer-transaction"] = new Dictionary<string, object?>
            {
                ["asset-id"] = ulong.Parse(effect.AssetId),
                ["amount"] = effect.Amount,
                ["receiver"] = effect.ToAddress,
            },
        };
        mutate?.Invoke(transaction);
        return Json(new Dictionary<string, object?> { ["transaction"] = transaction });
    }

    private static void CorruptIndexedField(Dictionary<string, object?> transaction, string field)
    {
        var assetTransfer = (Dictionary<string, object?>)transaction["asset-transfer-transaction"]!;
        switch (field)
        {
            case "id": transaction["id"] = "other"; break;
            case "group": transaction["group"] = "other"; break;
            case "sender": transaction["sender"] = new AlgoAccount().Address.EncodeAsString(); break;
            case "type": transaction["tx-type"] = "pay"; break;
            case "asset": assetTransfer["asset-id"] = 999UL; break;
            case "receiver": assetTransfer["receiver"] = new AlgoAccount().Address.EncodeAsString(); break;
            case "amount": assetTransfer["amount"] = 1UL; break;
            case "close-to": assetTransfer["close-to"] = " "; break;
            case "clawback": assetTransfer["revocation-target"] = " "; break;
            case "rekey": transaction["rekey-to"] = " "; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private static AtomicTransferGroupRequest GroupRequest(
        string? source = null,
        SigningContext? signingContext = null,
        string assetId = "12345")
    {
        source ??= new AlgoAccount().Address.EncodeAsString();
        var request = AtomicTransferGroupRequest.TryCreate(
            ProviderBinding(),
            "Algorand",
            ChainNetwork.Devnet,
            "settlement-1",
            new AtomicTransferEffect(
                assetId, source, new AlgoAccount().Address.EncodeAsString(), 90,
                signingContext ?? SigningContext.Platform),
            new AtomicTransferEffect(
                assetId, source, new AlgoAccount().Address.EncodeAsString(), 10,
                signingContext ?? SigningContext.Platform));
        request.IsError.Should().BeFalse(request.Message);
        return request.Result!;
    }

    private static TestProvider ProviderBinding()
    {
        var provider = new TestProvider();
        provider.Initialize(new BlockchainNetworkConfig(), ChainNetwork.Devnet);
        return provider;
    }

    private static HttpResponseMessage Confirmed() => Json(new Dictionary<string, object?>
    {
        ["confirmed-round"] = 12,
        ["pool-error"] = string.Empty,
    });

    private static HttpResponseMessage Params() => Json(new Dictionary<string, object?>
    {
        ["fee"] = 0,
        ["min-fee"] = 1000,
        ["last-round"] = 100,
        ["genesis-id"] = "devnet-v1.0",
        ["genesis-hash"] = "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=",
    });

    private static HttpResponseMessage Json(object body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private static HttpResponseMessage RawJson(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class RecordingSigner : ITransactionSigner
    {
        public List<string> Groups { get; } = [];
        public string ChainType => "Algorand";

        public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
        {
            var transaction = Algorand.Utils.Encoder.DecodeFromMsgPack<Transaction>(canonicalTxn);
            transaction.Group.Should().NotBeNull();
            Groups.Add(transaction.Group!.ToString());
            return new AZOAResult<byte[]> { Result = [0x91] };
        }
    }

    private sealed class CapturingRealSigner : ITransactionSigner
    {
        private readonly AlgorandTransactionSigner _inner = new();

        public List<byte[]> Envelopes { get; } = [];
        public string ChainType => _inner.ChainType;

        public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
        {
            var result = _inner.Sign(canonicalTxn, key);
            if (!result.IsError && result.Result is not null)
                Envelopes.Add(result.Result);
            return result;
        }
    }

    private sealed class AlgodStub : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _pendingResponse;

        public AlgodStub(Func<string, HttpResponseMessage>? pendingResponse = null)
        {
            _pendingResponse = pendingResponse ?? (_ => Confirmed());
        }

        public int RequestCount { get; private set; }
        public int BroadcastCount { get; private set; }
        public byte[] LastBroadcast { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v2/transactions/params", StringComparison.Ordinal))
                return Params();
            if (path == "/v2/transactions")
            {
                BroadcastCount++;
                LastBroadcast = request.Content is null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                return Json(new Dictionary<string, object?> { ["txId"] = "stub" });
            }
            if (path.Contains("/v2/transactions/pending/", StringComparison.Ordinal))
                return _pendingResponse(path);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class CancellingParamsHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingParamsHandler(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public int BroadcastCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/v2/transactions/params", StringComparison.Ordinal))
            {
                _cancellation.Cancel();
                throw new OperationCanceledException(_cancellation.Token);
            }

            if (request.RequestUri!.AbsolutePath == "/v2/transactions")
                BroadcastCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class AtomicGroupObservationHandler : HttpMessageHandler
    {
        private readonly Func<string, bool, HttpResponseMessage> _respond;

        public AtomicGroupObservationHandler(Func<string, bool, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public int IndexerRequestCount { get; private set; }
        public int AlgodRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isIndexer = string.Equals(request.RequestUri!.Host, "indexer.test", StringComparison.Ordinal);
            if (isIndexer)
                IndexerRequestCount++;
            else
                AlgodRequestCount++;
            var transactionId = request.RequestUri.AbsolutePath.Split('/').Last();
            return Task.FromResult(_respond(transactionId, isIndexer));
        }
    }

    private sealed class CancellingObservationHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingObservationHandler(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            throw new OperationCanceledException(_cancellation.Token);
        }
    }

    private sealed class TestProvider : AZOA.WebAPI.Providers.Blockchain.Base.BaseBlockchainProvider
    {
        public TestProvider()
            : base(new ConfigurationBuilder().Build(), NullLogger<TestProvider>.Instance)
        {
        }

        public override string ChainType => "Algorand";
    }
}
