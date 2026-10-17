using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>The public exchange seam over real stores and authority; only the chain and the gates outside the exchange are faked.</summary>
public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task DirectPurchaseJournalsBeforeSigningSettlesFromChainEvidenceAndReplaysWithoutASecondSignature()
    {
        var flow = await FlowAsync();

        var first = await flow.Manager.ExchangeAsync(flow.Request(), flow.Caller("purchase-1"));

        first.Code.Should().Be(ConsumerSafeErrorCodes.SettlementPending, "a submitted group is pending until finalized evidence");
        first.Result!.State.Should().Be(ExchangeReceiptState.AwaitingReconciliation);
        flow.Chain.Prepared.Should().Be(1);
        (await Counters()).Held.Should().Be(400, "the units were committed before the group was signed");
        (await flow.Settlement.ReconcileAsync(first.Result.SourceId!)).Result!.State.Should().Be("Completed");

        var replay = await flow.Manager.ExchangeAsync(flow.Request(), flow.Caller("purchase-1"));
        var receipt = await flow.Manager.GetReceiptAsync(new() { ApiKeyId = flow.ApiKeyId, CallerUserId = _actor, ClientIdempotencyKey = "purchase-1" });

        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result!.SourceId.Should().Be(first.Result.SourceId);
        receipt.Result!.State.Should().Be(ExchangeReceiptState.Completed);
        receipt.Result.ProjectCreditAmount.Should().Be("10");
        receipt.Result.SettlementLegReferences.Should().Equal(flow.Chain.TransactionIds);
        flow.Chain.Prepared.Should().Be(1, "the original source resolves a replay; custody is never asked to sign twice");
        (await TreasuryCounters()).Credited.Should().Be(10);
        var counters = await Counters();
        counters.Held.Should().Be(0);
        counters.Issued.Should().Be(400);
    }

    [Fact]
    public async Task LostSubmitResponseRebroadcastsTheSameSavedBytesUntilSettlement()
    {
        var flow = await FlowAsync();
        flow.Chain.FailNextSubmit = true;

        var first = await flow.Manager.ExchangeAsync(flow.Request(), flow.Caller("purchase-1"));
        first.Result!.State.Should().Be(ExchangeReceiptState.AwaitingReconciliation);
        flow.Chain.Confirmed.Should().BeFalse("the first send never reached the network");

        await flow.Settlement.ReconcileAsync(first.Result.SourceId!);
        await flow.Settlement.ReconcileAsync(first.Result.SourceId!);

        flow.Chain.Prepared.Should().Be(1);
        flow.Chain.SubmittedPayloads.Should().HaveCount(2).And.OnlyContain(payload => payload == flow.Chain.SubmittedPayloads[0]);
        (await _store.GetExchangeSourceAsync(first.Result.SourceId!))!.State.Should().Be("Completed");
        (await TreasuryCounters()).Credited.Should().Be(10, "a rebroadcast credits the project once");
    }

    [Fact]
    public async Task SameKeyWithADifferentBodyIsRefusedWithoutASecondSource()
    {
        var flow = await FlowAsync();
        await flow.Manager.ExchangeAsync(flow.Request(), flow.Caller("purchase-1"));
        var changed = flow.Request();
        changed.PlatformAmount = "11";

        var refused = await flow.Manager.ExchangeAsync(changed, flow.Caller("purchase-1"));

        refused.Code.Should().Be(AzoaErrorCodes.OperationNotAllowed);
        (await _executor.QueryAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.From())).Should().ContainSingle();
        (await Counters()).Held.Should().Be(400, "only the original commitment exists");
    }

    [Fact]
    public async Task OversubscribedPartitionIsRefusedBeforeAnySourceOrCommitment()
    {
        var flow = await FlowAsync();
        var oversized = flow.Request();
        oversized.ProjectUnitAmount = "2000";

        var result = await flow.Manager.ExchangeAsync(oversized, flow.Caller("purchase-1"));

        result.IsError.Should().BeTrue();
        (await _executor.QueryAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.From())).Should().BeEmpty();
        (await Counters()).Held.Should().Be(0);
        flow.Chain.Prepared.Should().Be(0);
    }

    [Fact]
    public async Task HeldPurchaseAutoReleaseNeedsTheAcceptedPreviewDisclosureAndCreatesItsMandateWithTheCommitment()
    {
        var flow = await FlowAsync(transferSign: true);
        var window = DateTimeOffset.UtcNow.AddDays(30);
        var request = flow.Request(window);
        request.ReleaseMandate = new() { ExpiresAt = window.AddDays(30), MaximumNetworkFeeBaseUnits = "2000" };

        var unaccepted = await flow.Manager.ExchangeAsync(request, flow.Caller("held-1"));
        request.PreviewOnly = true;
        var preview = await flow.Manager.ExchangeAsync(request, flow.Caller("held-1"));
        request.PreviewOnly = false;
        request.ReleaseAcceptance = new() { RequestId = Guid.NewGuid(), Disclosure = preview.Result!.ReleaseDisclosure };
        var accepted = await flow.Manager.ExchangeAsync(request, flow.Caller("held-1"));

        unaccepted.Code.Should().Be(AzoaErrorCodes.InvalidRequest, "a boolean wish is not consent to an unseen disclosure");
        preview.Result.ReleaseDisclosure!.Terms.MaximumCosts.NetworkFeeBaseUnits.Should().Be("2000");
        accepted.Result!.ReleaseMandateId.Should().NotBeNull(accepted.Message);
        (await new SurrealHoldReleaseMandateStore(_executor).GetAsync(accepted.Result.ReleaseMandateId!))!.Status.Should().Be("Active");
        var source = (await _store.GetExchangeSourceAsync(accepted.Result.SourceId!))!;
        var opening = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson)!.Opening!;
        (await _store.GetHoldAsync(HoldCommandIdentity.Hold(opening)))!.State
            .Should().Be("AwaitingSourceSettlement", "the release cannot run before the purchase settles");
    }

    [Theory]
    [InlineData(false, false, "101000", null)]
    [InlineData(false, false, "500000", ConsumerSafeErrorCodes.RecipientNotReady)]
    [InlineData(false, true, "0", ConsumerSafeErrorCodes.NotPermitted)]
    public async Task UnreadyParticipantAcceptanceIsFoldedIntoTheExactGroupOrRefusedBeforeAnyCommitment(
        bool canReceive, bool blocked, string shortfall, string? refusal)
    {
        var flow = await FlowAsync();
        flow.Chain.Readiness = new(canReceive, blocked, shortfall);

        var result = await flow.Manager.ExchangeAsync(flow.Request(), flow.Caller("purchase-1"));

        if (refusal is not null)
        {
            result.Code.Should().Be(refusal);
            (await _executor.QueryAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.From())).Should().BeEmpty();
            (await Counters()).Held.Should().Be(0);
            flow.Chain.Prepared.Should().Be(0);
            return;
        }
        result.Result!.State.Should().Be(ExchangeReceiptState.AwaitingReconciliation, result.Message);
        var source = (await _store.GetExchangeSourceAsync(result.Result.SourceId!))!;
        JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson)!.Units!.RecipientFundingBaseUnits.Should().Be(shortfall);
        var readiness = flow.Chain.LastIntent!.Readiness!;
        readiness.AssetId.Should().Be("42");
        readiness.FundingBaseUnits.Should().Be(shortfall);
        readiness.Recipient.Should().Be(flow.Chain.LastIntent.Legs[0].Sender, "only the participant who signs a leg is readied");
        (await flow.Settlement.ReconcileAsync(result.Result.SourceId!)).Result!.State.Should().Be("Completed");
    }

    [Fact]
    public async Task AnotherApiKeyCannotReadTheReceipt()
    {
        var flow = await FlowAsync();
        await flow.Manager.ExchangeAsync(flow.Request(), flow.Caller("purchase-1"));

        var foreign = await flow.Manager.GetReceiptAsync(new() { ApiKeyId = Guid.NewGuid(), CallerUserId = _actor, ClientIdempotencyKey = "purchase-1" });

        foreign.Code.Should().Be(AzoaErrorCodes.NotFound);
    }

    private async Task<ExchangeFlow> FlowAsync(bool transferSign = false)
    {
        var (intent, authority) = await ExchangeAsync("fixture");
        var credential = authority.Tenant.Credential;
        if (transferSign)
        {
            string[] scopes = [AzoaScopes.SwapSign, AzoaScopes.TransferSign];
            await Execute(SurrealWriter.UpdateOnly<ApiKey>(intent.ApiKeyId.ToString("N")).Set(row => row.Scopes, scopes).Build());
            credential = credential with { Scopes = scopes, StoredApiKeyScopes = scopes };
        }
        await SeedTreasuryAsync();
        var chain = new FakeExchangeChain();
        var provider = InterfaceFake<IBlockchainProvider>.Create(new()
        {
            ["TryGetModule"] = (method, args) =>
            {
                args[0] = method.GetGenericArguments()[0].IsInstanceOfType(chain) ? chain : null;
                return args[0] is not null;
            },
        });
        var providers = InterfaceFake<IBlockchainProviderFactory>.Create(new()
        {
            ["get_DefaultNetwork"] = (_, _) => ChainNetwork.Devnet,
            ["GetProvider"] = (_, _) => provider,
        });
        var users = new SurrealUserStore(_executor);
        var wallets = new SurrealWalletStore(_executor);
        var treasury = Fake<IProjectTreasury>("ResolveAsync", Task.FromResult(AZOAResult<ProjectTreasuryAccount>.Success(
            new("Algorand", _actor, "project", "treasury", "fixture", ProjectTreasuryStatus.Open))));
        var platformUnit = new PlatformTokenState(
            new("Algorand", "Devnet", "7", "Platform", "PLAT", 0, new BigInteger(1_000_000), "reserve", DateTimeOffset.UtcNow),
            new(new BigInteger(1_000_000), BigInteger.Zero, BigInteger.Zero, new BigInteger(1_000_000)));
        var options = new ExchangeOptions { MaximumGroupNetworkFeeBaseUnits = "10000", MaximumRecipientFundingBaseUnits = "201000" };
        var settlement = new ExchangeHoldSourceService(_store, providers, options);
        var signing = new SigningAdmissionAuthorityFactory(users, wallets, _executor);
        var manager = new ReserveExchangeManager(
            Fake<IValueAccessService>("RequireValueAccessAsync", Task.FromResult(AZOAResult<bool>.Success(true))),
            Fake<IPlatformReserve>("GetAccountAsync", Task.FromResult(AZOAResult<ReserveAccount>.Success(new("Algorand", "reserve", true)))),
            new SurrealPlatformReserveLedgerStore(_executor), treasury,
            Fake<IPlatformTokenGenesis>("GetStateAsync", Task.FromResult(AZOAResult<PlatformTokenState>.Success(platformUnit))),
            wallets,
            Fake<IExchangeRatePolicy>("EvaluateAsync", Task.FromResult(AZOAResult<ExchangeRateDecision>.Success(new(null, "policy-v1")))),
            Fake<IExchangeDeclaredRateWriter>("RecordAsync", null),
            _store, settlement, new ExchangeAuthorityFactory(signing, users, wallets, _executor), signing, providers, options);
        return new(manager, settlement, chain, intent.ApiKeyId, authority.ParticipantUserId, credential);
    }

    /// <summary>A process-edge gate that answers one member with a fixed result; <c>null</c> means the flow must never call it.</summary>
    private static T Fake<T>(string member, object? result) where T : class
        => InterfaceFake<T>.Create(new() { [member] = (_, _) => result ?? throw new NotSupportedException(member) });

    private sealed record ExchangeFlow(ReserveExchangeManager Manager, ExchangeHoldSourceService Settlement,
        FakeExchangeChain Chain, Guid ApiKeyId, Guid Participant, SigningCredentialWitness Credential)
    {
        public ExchangeRequest Request(DateTimeOffset? window = null) => new()
        {
            ChainType = "Algorand", Direction = ExchangeDirection.Buy, ProjectReference = "project", ProjectAssetId = "42",
            PlatformAmount = "10", ProjectUnitAmount = "400", ReservePartition = "contributors", ReleaseNotBefore = window,
        };

        public ExchangeCallerContext Caller(string key) => new()
        {
            ApiKeyId = ApiKeyId, CallerUserId = Credential.ActorUserId, ActingTenantId = Credential.ActorUserId,
            TargetUserId = Participant, ClientIdempotencyKey = key, Credential = Credential,
        };
    }

    /// <summary>Chain process edge: signs what it is given and confirms the exact group once it has been sent.</summary>
    private sealed class FakeExchangeChain : IExchangeTransactionModule, IValueCustodyModule, IRecipientPreparationModule
    {
        public RecipientAssetReadiness Readiness { get; set; } = new(true, false, "0");
        public ExchangeWireIntent? LastIntent { get; private set; }
        public bool SupportsRecipientPreparation => true;

        public Task<AZOAResult<RecipientAssetReadiness>> GetRecipientAssetReadinessAsync(string assetId, string recipientAddress,
            CancellationToken ct = default) => Task.FromResult(AZOAResult<RecipientAssetReadiness>.Success(Readiness));

        public Task<AZOAResult<RecipientPreparationReceipt>> PrepareRecipientForAssetAsync(RecipientPreparationRequest request,
            CancellationToken ct = default) => throw new NotSupportedException("Readiness is folded into the exact group, never signed apart.");

        public string CapabilityName => "exchange";
        public string ChainType => "Algorand";
        public int Prepared { get; private set; }
        public bool Confirmed { get; private set; }
        public bool FailNextSubmit { get; set; }
        public List<string> SubmittedPayloads { get; } = [];
        public IReadOnlyList<string> TransactionIds { get; } = ["tx-funds", "tx-units", "tx-fee"];

        public Task<AZOAResult<PreparedExchangeWire>> PrepareExchangeAsync(ExchangeWireIntent intent, CancellationToken ct = default)
        {
            Prepared++;
            LastIntent = intent;
            var ids = TransactionIds.Take(intent.Legs.Count).Append("tx-fee").ToArray();
            return Task.FromResult(AZOAResult<PreparedExchangeWire>.Success(new(intent, "preparation-" + intent.SourceId[..8], "group",
                ids, Enumerable.Repeat("c2lnbmVk", ids.Length).ToArray())));
        }

        public Task SubmitExchangeAsync(PreparedExchangeWire prepared, CancellationToken ct = default)
        {
            SubmittedPayloads.Add(JsonSerializer.Serialize(prepared));
            if (FailNextSubmit)
            {
                FailNextSubmit = false;
                throw new HttpRequestException("connection reset");
            }
            Confirmed = true;
            return Task.CompletedTask;
        }

        public Task<AZOAResult<ExchangeWireObservation>> ObserveExchangeAsync(PreparedExchangeWire prepared, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<ExchangeWireObservation>.Success(new(Confirmed ? "Confirmed" : "Unknown",
                prepared.PreparationId, prepared.GroupId, CanonicalJson.Hash(prepared), prepared.Intent.ProtocolIdentity,
                Confirmed ? "100" : null)));

        public Task<AZOAResult<ValueCustodyQuote>> QuoteValueCustodyAsync(ValueCustodyQuoteRequest request, CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            var quote = new ValueCustodyQuote(1, string.Empty, request, "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0",
                now, now.AddMinutes(10), new(request.Sender, request.Sender, "1000", "0", "0"));
            return Task.FromResult(AZOAResult<ValueCustodyQuote>.Success(quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) }));
        }

        public Task<AZOAResult<PreparedValueAcceptance>> PrepareValueAcceptanceAsync(ValueAcceptanceIntent intent, CancellationToken ct)
            => throw new NotSupportedException("Recipient acceptance is outside the exchange flow.");

        public Task SubmitValueAcceptanceAsync(PreparedValueAcceptance prepared, CancellationToken ct)
            => throw new NotSupportedException("Recipient acceptance is outside the exchange flow.");

        public Task<AZOAResult<ChainConfirmation>> ObserveValueAcceptanceAsync(PreparedValueAcceptance prepared, CancellationToken ct)
            => throw new NotSupportedException("Recipient acceptance is outside the exchange flow.");
    }
}
