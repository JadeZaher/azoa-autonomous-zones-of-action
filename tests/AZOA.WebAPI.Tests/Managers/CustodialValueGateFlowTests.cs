using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>RV-X R-3: a user's custodial value move passes the risk-hold-wrapped value gate before any claim, row or chain call;
/// a return to one of the owner's own wallets is exempt. The real gate composition runs; the chain is the faked edge.</summary>
public sealed class CustodialValueGateFlowTests
{
    private const string Source = "OWNERSOURCEADDRESS";
    private const string OwnOther = "OWNERSECONDADDRESS";
    private const string Stranger = "STRANGERADDRESS";
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _sourceWallet = Guid.NewGuid();
    private readonly SwitchableValueGate _gate = new();
    private readonly FakeIdempotencyStore _idempotency = new();
    private readonly Mock<IBlockchainProvider> _chain = new();
    private readonly BlockchainOperationManager _manager;

    public CustodialValueGateFlowTests()
    {
        _chain.Setup(provider => provider.ChainType).Returns("Algorand");
        _chain.Setup(provider => provider.TransferAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RailAmount>(),
            It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<string>.Success("tx-transfer"));
        _chain.Setup(provider => provider.SwapAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<string>.Success("tx-swap"));
        var store = new Mock<IBlockchainOperationStore>();
        store.Setup(value => value.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IBlockchainOperation operation, CancellationToken _) => AZOAResult<IBlockchainOperation>.Success(operation));
        var wallets = new Mock<IWalletStore>();
        IWallet[] owned =
        [
            new Wallet { Id = _sourceWallet, UserId = _owner, ChainType = "Algorand", Address = Source, WalletType = WalletType.Platform },
            new Wallet { Id = Guid.NewGuid(), UserId = _owner, ChainType = "Algorand", Address = OwnOther, WalletType = WalletType.Platform },
        ];
        wallets.Setup(value => value.GetByUserAsync(_owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Success(owned));
        wallets.Setup(value => value.GetByIdAsync(_sourceWallet, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IWallet>.Success(owned[0]));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Blockchain:DefaultChain"] = "Algorand", ["Blockchain:DefaultNetwork"] = "Devnet",
            ["Blockchain:Chains:0:ChainType"] = "Algorand", ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
        }).Build();
        var factory = new BlockchainProviderFactory([new BlockchainProviderRegistration("Algorand", () => _chain.Object)], config);
        _manager = new BlockchainOperationManager(store.Object, factory, _idempotency, wallets: wallets.Object,
            valueAccess: new ValueAccessService(_gate));
    }

    public static TheoryData<string> ClosedGates => new() { "reversal pause", "lapsed KYC" };

    [Theory]
    [MemberData(nameof(ClosedGates))]
    public async Task A_closed_gate_refuses_a_transfer_to_another_party_before_any_claim_and_a_retry_runs_once_it_reopens(string closed)
    {
        Close(closed);

        var refused = await _manager.ExecuteAsync(Transfer(Stranger));

        refused.Code.Should().Be(AzoaErrorCodes.Forbidden);
        _idempotency.Keys.Should().BeEmpty("a refusal must not burn the logical operation's claim");
        TransfersSent().Should().Be(0);

        _gate.Paused = false;
        _gate.KycLapsed = false;
        var retried = await _manager.ExecuteAsync(Transfer(Stranger));

        retried.IsError.Should().BeFalse(retried.Message);
        retried.Result!.Status.Should().Be(OperationStatus.Transferred);
        TransfersSent().Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(ClosedGates))]
    public async Task A_return_to_the_owners_own_wallet_still_runs_while_the_gate_is_closed(string closed)
    {
        Close(closed);

        var moved = await _manager.ExecuteAsync(Transfer(OwnOther));

        moved.IsError.Should().BeFalse(moved.Message);
        moved.Result!.Status.Should().Be(OperationStatus.Transferred);
        TransfersSent().Should().Be(1);
    }

    [Fact]
    public async Task A_paused_owner_cannot_swap_custodial_value_out()
    {
        _gate.Paused = true;
        var swap = new BlockchainOperation
        {
            UserId = _owner, WalletId = _sourceWallet, OperationType = "Swap",
            Parameters = new() { ["ChainType"] = "Algorand", ["TokenIn"] = "A", ["TokenOut"] = "B", ["AmountIn"] = "1",
                ["MinAmountOut"] = "1", ["WalletAddress"] = Source },
        };

        (await _manager.ExecuteAsync(swap)).Code.Should().Be(AzoaErrorCodes.Forbidden);
        _chain.Verify(provider => provider.SwapAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void Close(string gate)
    {
        _gate.Paused = gate == "reversal pause";
        _gate.KycLapsed = gate == "lapsed KYC";
    }

    private BlockchainOperation Transfer(string recipient) => new()
    {
        UserId = _owner, WalletId = _sourceWallet, OperationType = "Transfer", SourceHolonId = Guid.Empty, RecipientAddress = recipient,
        Parameters = new() { ["ChainType"] = "Algorand", ["WalletAddress"] = Source, ["SourceTokenId"] = "123", ["Amount"] = "5" },
    };

    private int TransfersSent() => _chain.Invocations.Count(call => call.Method.Name == nameof(IBlockchainProvider.TransferAsync));
}
