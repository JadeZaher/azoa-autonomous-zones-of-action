using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Base;

namespace AZOA.WebAPI.Tests.Core;

public sealed class AtomicTransferGroupRequestTests
{
    [Fact]
    public void TryCreate_IsStableAndBindsEveryEconomicAndSignerInput()
    {
        var primary = Effect("recipient", amount: 90);
        var treasury = Effect("treasury", amount: 10);
        var provider = Provider();

        var first = AtomicTransferGroupRequest.TryCreate(
            provider, " Algorand ", ChainNetwork.Devnet, " settlement-1 ", primary, treasury);
        var replay = AtomicTransferGroupRequest.TryCreate(
            provider, "Algorand", ChainNetwork.Devnet, "settlement-1", primary, treasury);
        var changed = AtomicTransferGroupRequest.TryCreate(
            provider, "Algorand", ChainNetwork.Devnet, "settlement-1", primary, treasury with { Amount = 11 });

        first.IsError.Should().BeFalse(first.Message);
        replay.IsError.Should().BeFalse(replay.Message);
        changed.IsError.Should().BeFalse(changed.Message);
        first.Result!.GroupIdentity.Should().Be(replay.Result!.GroupIdentity);
        first.Result.IdempotencyKeyHash.Should().Be(replay.Result.IdempotencyKeyHash);
        first.Result.ChainType.Should().Be("Algorand");
        first.Result.GroupIdentity.Should().NotBe(changed.Result!.GroupIdentity);
        first.Result.GroupIdentity.Should().MatchRegex("^[0-9a-f]{64}$");
        first.Result.IdempotencyKeyHash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Theory]
    [InlineData("different-asset")]
    [InlineData("different-source")]
    [InlineData("different-signer")]
    [InlineData("same-recipient")]
    [InlineData("unresolvable-signer")]
    public void TryCreate_RejectsAGroupThatCouldNotHaveOneAtomicSignerAndFeeMeaning(string violation)
    {
        var primary = Effect("recipient", amount: 90);
        var treasury = Effect("treasury", amount: 10);
        var provider = Provider();
        treasury = violation switch
        {
            "different-asset" => treasury with { AssetId = "other" },
            "different-source" => treasury with { FromAddress = "other-source" },
            "different-signer" => treasury with { SigningContext = SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()) },
            "same-recipient" => treasury with { ToAddress = primary.ToAddress },
            "unresolvable-signer" => treasury with { SigningContext = new SigningContext(Guid.Empty, Guid.Empty, IsPlatform: false) },
            _ => throw new InvalidOperationException(),
        };

        var result = AtomicTransferGroupRequest.TryCreate(
            provider, "Algorand", ChainNetwork.Devnet, "settlement-1", primary, treasury);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeNull();
    }

    [Fact]
    public void TryCreate_BindsToTheResolvedProviderAndRejectsAMismatchedBinding()
    {
        var provider = Provider("CanonicalChain", ChainNetwork.Testnet);
        var primary = Effect("recipient", amount: 90);
        var treasury = Effect("treasury", amount: 10);

        var canonical = AtomicTransferGroupRequest.TryCreate(
            provider, " canonicalchain ", ChainNetwork.Testnet, "settlement-1", primary, treasury);
        var wrongChain = AtomicTransferGroupRequest.TryCreate(
            provider, "UnknownChain", ChainNetwork.Testnet, "settlement-1", primary, treasury);
        var wrongNetwork = AtomicTransferGroupRequest.TryCreate(
            provider, "CanonicalChain", ChainNetwork.Devnet, "settlement-1", primary, treasury);

        canonical.IsError.Should().BeFalse(canonical.Message);
        canonical.Result!.ChainType.Should().Be("CanonicalChain");
        wrongChain.IsError.Should().BeTrue();
        wrongChain.Message.Should().Contain("does not match");
        wrongNetwork.IsError.Should().BeTrue();
        wrongNetwork.Message.Should().Contain("does not match");
    }

    [Fact]
    public void AcceptedResult_CannotRepresentANotSubmittedOrPartialGroup()
    {
        var request = Request();
        var action = () => AtomicTransferGroupSubmission.Accepted(
            request, "primary", "treasury", AtomicTransferGroupSubmissionState.NotSubmitted);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AcceptedResult_UsesOriginatingIdentityAndRejectsDuplicateLegIdentifiers()
    {
        var request = Request();

        var accepted = AtomicTransferGroupSubmission.Accepted(
            request, "primary-transaction", "treasury-transaction", AtomicTransferGroupSubmissionState.Submitted);
        var duplicate = () => AtomicTransferGroupSubmission.Accepted(
            request, "same-transaction", "same-transaction", AtomicTransferGroupSubmissionState.Submitted);
        var blank = () => AtomicTransferGroupSubmission.Accepted(
            request, " ", "treasury-transaction", AtomicTransferGroupSubmissionState.Submitted);

        accepted.GroupIdentity.Should().Be(request.GroupIdentity);
        duplicate.Should().Throw<ArgumentException>().WithMessage("*must be distinct*");
        blank.Should().Throw<ArgumentException>();
    }

    private static AtomicTransferGroupRequest Request()
    {
        var result = AtomicTransferGroupRequest.TryCreate(
            Provider(), "Algorand", ChainNetwork.Devnet, "settlement-1", Effect("recipient", 90), Effect("treasury", 10));
        result.IsError.Should().BeFalse(result.Message);
        return result.Result!;
    }

    private static TestProvider Provider(string chainType = "Algorand", ChainNetwork network = ChainNetwork.Devnet)
    {
        var provider = new TestProvider(chainType);
        provider.Initialize(new BlockchainNetworkConfig(), network);
        return provider;
    }

    private static AtomicTransferEffect Effect(string toAddress, ulong amount) => new(
        AssetId: "asset-1",
        FromAddress: "source",
        ToAddress: toAddress,
        Amount: amount,
        SigningContext: SigningContext.ForUser(
            new Guid("11111111-1111-1111-1111-111111111111"),
            new Guid("22222222-2222-2222-2222-222222222222")));

    private sealed class TestProvider : BaseBlockchainProvider
    {
        private readonly string _chainType;

        public TestProvider(string chainType)
            : base(new ConfigurationBuilder().Build(), NullLogger<TestProvider>.Instance)
        {
            _chainType = chainType;
        }

        public override string ChainType => _chainType;
    }
}
