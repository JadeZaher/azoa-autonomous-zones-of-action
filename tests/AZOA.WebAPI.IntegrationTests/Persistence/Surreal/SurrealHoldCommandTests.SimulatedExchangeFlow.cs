using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Simulated;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>The public exchange seam on the real Simulated provider and its D-7 pin; only gates outside the exchange are faked.</summary>
public sealed partial class SurrealHoldCommandTests
{
    private static readonly string SimReserve = SimulatedBlockchainProvider.SimAddress("Simulated", "reserve");
    private static readonly string SimTreasury = SimulatedBlockchainProvider.SimAddress("Simulated", "treasury");
    private static readonly string SimParticipant = SimulatedBlockchainProvider.SimAddress("Simulated", "participant");
    private static string SimReserveId => PlatformReserveLedger.RecordIdFor("Simulated", "Devnet", "42");

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task SimulatedExchangeSettlesBothLegsTogetherAndServesItsTerminalReceiptByIdempotencyKey(bool held, int legs)
    {
        var flow = await SimulatedFlowAsync(activated: true);
        var request = flow.Request(held ? DateTimeOffset.UtcNow.AddDays(30) : null);

        var first = await flow.Manager.ExchangeAsync(request, flow.Caller("sponsor-1"));
        first.Code.Should().Be(ConsumerSafeErrorCodes.SettlementPending, first.Message);
        var retried = await flow.Manager.ExchangeAsync(request, flow.Caller("sponsor-1"));
        var receipt = await flow.Manager.GetReceiptAsync(new() { ApiKeyId = flow.ApiKeyId, CallerUserId = _actor, ClientIdempotencyKey = "sponsor-1" });

        retried.IsError.Should().BeFalse(retried.Message);
        receipt.Result!.State.Should().Be(ExchangeReceiptState.Completed);
        // ArdaNova's settlement gateway acceptance, verbatim: a non-empty Guid operation id, terminal, no reconciliation.
        Guid.TryParse(receipt.Result.OperationId?.ToString(), out var operationId).Should().BeTrue();
        operationId.Should().NotBe(Guid.Empty);
        receipt.Result.IsTerminal.Should().BeTrue();
        receipt.Result.RequiresReconciliation.Should().BeFalse();
        first.Result!.OperationId.Should().Be(operationId, "the pending answer already names the same durable operation");
        receipt.Result.SourceId.Should().Be(first.Result!.SourceId);
        receipt.Result.ProjectCreditAmount.Should().Be("10");
        receipt.Result.SettlementReference.Should().StartWith(SimulatedBlockchainProvider.SimGroupPrefix);
        receipt.Result.SettlementLegReferences.Should().HaveCount(legs + 1, "every leg plus the pooled fee member")
            .And.OnlyContain(id => id.StartsWith(SimulatedBlockchainProvider.SimTxPrefix));
        (await flow.Balance(SimParticipant, "7")).Should().Be(-10, "the participant's platform leg applied once");
        (await flow.Balance(SimTreasury, "7")).Should().Be(10);
        (await flow.Balance(SimParticipant, "42")).Should().Be(held ? 0 : 400, "a held purchase delivers no units yet");
        (await flow.Balance(SimReserve, "42")).Should().Be(held ? 0 : -400);
        var counters = await SimCounters();
        counters.Held.Should().Be(held ? 400 : 0);
        counters.Issued.Should().Be(held ? 0 : 400);
    }

    [Fact]
    public async Task SimulatedPreviewRunsEveryGateAndDrawsNoPartition()
    {
        var flow = await SimulatedFlowAsync(activated: true);
        var request = flow.Request(DateTimeOffset.UtcNow.AddDays(30));
        request.PreviewOnly = true;

        var preview = await flow.Manager.ExchangeAsync(request, flow.Caller("sponsor-1"));

        preview.IsError.Should().BeFalse(preview.Message);
        preview.Result!.ReceiptReference.Should().BeEmpty();
        preview.Result.SourceId.Should().BeNull();
        preview.Result.OperationId.Should().BeNull("a quote persists no operation");
        (await _executor.QueryAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.From())).Should().BeEmpty();
        (await SimCounters()).Held.Should().Be(0);
        (await flow.Balance(SimParticipant, "7")).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SimulatedExchangeWithoutItsActivationPinStaysRailUnavailable(bool preview)
    {
        var flow = await SimulatedFlowAsync(activated: false);
        var request = flow.Request();
        request.PreviewOnly = preview;

        var result = await flow.Manager.ExchangeAsync(request, flow.Caller("sponsor-1"));

        result.Code.Should().Be(ConsumerSafeErrorCodes.RailUnavailable);
        (await _executor.QueryAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.From())).Should().BeEmpty();
        (await SimCounters()).Held.Should().Be(0);
    }

    private async Task<SimulatedExchangeFlow> SimulatedFlowAsync(bool activated)
    {
        var participant = Guid.NewGuid(); var wallet = Guid.NewGuid(); var apiKey = Guid.NewGuid(); var grant = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var keyHash = apiKey.ToString("N") + apiKey.ToString("N");
        await Save(new User { Id = participant.ToString("N"), Username = participant.ToString("N"),
            Email = $"{participant:N}@example.test", PasswordHash = "fixture-no-login", IsActive = true });
        await Save(new ApiKey { Id = apiKey.ToString("N"), UserId = $"user:{_actor:N}", IsActive = true,
            KeyHash = keyHash, KeyPrefix = "fixture", CreatedDate = now, Scopes = [AzoaScopes.SwapSign] });
        await Save(new Wallet { Id = wallet.ToString("N"), UserId = $"user:{participant:N}", ChainType = "Simulated", Address = SimParticipant });
        await Save(new ConsentGrant { Id = grant.ToString("N"), GrantorUserId = $"user:{participant:N}", TenantId = $"user:{_actor:N}",
            Origin = "UserExplicit", Scopes = AzoaScopes.SwapSign, GrantedAt = now });
        await Save(new PlatformReserveLedger { Id = SimReserveId, Chain = "Simulated", Network = "Devnet",
            ProtocolIdentity = "sim:devnet", AssetId = "42", ReserveAddress = SimReserve, UnitName = "UNIT", TotalSupply = "1000",
            TenantId = "user:" + _actor.ToString("N"),
            Partitions = ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>
                { ["contributors"] = ReservePartitionCounters.Declaring(1000) }) });
        await Save(new ProjectTreasuryLedger { Id = ProjectTreasuryLedger.RecordIdFor("Simulated", "Devnet", _actor.ToString("N"), "project"),
            Chain = "Simulated", Network = "Devnet", TenantId = "user:" + _actor.ToString("N"), ProjectReference = "project",
            TreasuryAddress = SimTreasury, DerivationVersion = "fixture", Status = "Open",
            Assets = ProjectTreasuryAssetCodec.Encode(new Dictionary<string, ProjectTreasuryAssetCounters>()) });
        var credential = new SigningCredentialWitness(1, "ApiKey", _actor, null, null, null, null, apiKey,
            keyHash, [AzoaScopes.SwapSign], false, [AzoaScopes.SwapSign]);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Blockchain:Mode"] = "Simulated", ["Blockchain:DefaultNetwork"] = "Devnet",
            [ChainModuleActivation.Key(ChainModuleActivation.Exchange, "Simulated", ChainNetwork.Devnet)] = activated ? "true" : null,
        }).Build();
        var simulated = new SimulatedBlockchainProvider(config, NullLogger<SimulatedBlockchainProvider>.Instance);
        var providers = new BlockchainProviderFactory([new BlockchainProviderRegistration("Simulated", () => simulated)], config);
        var users = new SurrealUserStore(_executor);
        var wallets = new SurrealWalletStore(_executor);
        var treasury = Fake<IProjectTreasury>("ResolveAsync", Task.FromResult(AZOAResult<ProjectTreasuryAccount>.Success(
            new("Simulated", _actor, "project", SimTreasury, "fixture", ProjectTreasuryStatus.Open))));
        var platformUnit = new PlatformTokenState(
            new("Simulated", "Devnet", "7", "Platform", "PLAT", 0, new BigInteger(1_000_000), SimReserve, now),
            new(new BigInteger(1_000_000), BigInteger.Zero, BigInteger.Zero, new BigInteger(1_000_000)));
        var options = new ExchangeOptions { MaximumGroupNetworkFeeBaseUnits = "10000", MaximumRecipientFundingBaseUnits = "0" };
        var settlement = new ExchangeHoldSourceService(_store, providers, options);
        var signing = new SigningAdmissionAuthorityFactory(users, wallets, _executor);
        var manager = new ReserveExchangeManager(
            Fake<IValueAccessService>("RequireValueAccessAsync", Task.FromResult(AZOAResult<bool>.Success(true))),
            Fake<IPlatformReserve>("GetAccountAsync", Task.FromResult(AZOAResult<ReserveAccount>.Success(new("Simulated", SimReserve, true)))),
            new SurrealPlatformReserveLedgerStore(_executor), treasury,
            Fake<IPlatformTokenGenesis>("GetStateAsync", Task.FromResult(AZOAResult<PlatformTokenState>.Success(platformUnit))),
            wallets,
            Fake<IExchangeRatePolicy>("EvaluateAsync", Task.FromResult(AZOAResult<ExchangeRateDecision>.Success(new(null, "policy-v1")))),
            Fake<IExchangeDeclaredRateWriter>("RecordAsync", null),
            _store, settlement, new ExchangeAuthorityFactory(signing, users, wallets, _executor), signing, providers, options);
        return new(manager, simulated, apiKey, participant, credential);
    }

    private async Task<ReservePartitionCounters> SimCounters()
    {
        var row = await _executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(SimReserveId));
        return ReservePartitionCodec.Decode(row!.Partitions, out _)!["contributors"];
    }

    private sealed record SimulatedExchangeFlow(ReserveExchangeManager Manager, SimulatedBlockchainProvider Chain, Guid ApiKeyId,
        Guid Participant, SigningCredentialWitness Credential)
    {
        public ExchangeRequest Request(DateTimeOffset? window = null) => new()
        {
            ChainType = "Simulated", Direction = ExchangeDirection.Buy, ProjectReference = "project", ProjectAssetId = "42",
            PlatformAmount = "10", ProjectUnitAmount = "400", ReservePartition = "contributors", ReleaseNotBefore = window,
        };

        public ExchangeCallerContext Caller(string key) => new()
        {
            ApiKeyId = ApiKeyId, CallerUserId = Credential.ActorUserId, ActingTenantId = Credential.ActorUserId,
            TargetUserId = Participant, ClientIdempotencyKey = key, Credential = Credential,
        };

        public async Task<long> Balance(string address, string tokenId)
            => long.Parse((await Chain.GetBalanceAsync(address, tokenId)).Result!);
    }
}
