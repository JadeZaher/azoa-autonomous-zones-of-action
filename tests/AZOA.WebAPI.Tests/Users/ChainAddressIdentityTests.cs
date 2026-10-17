using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Users;

public sealed class ChainAddressIdentityTests
{
    [Theory]
    [InlineData("Ethereum", "0x52908400098527886E0F7030069857D2E4169EE7", "0x52908400098527886e0f7030069857d2e4169ee7", true)]
    [InlineData("Ethereum", "0x123", "0x123", false)]
    [InlineData("Ethereum", "0x52908400098527886E0F7030069857D2E4169Ee7", "0x52908400098527886e0f7030069857d2e4169ee7", false)]
    [InlineData("Solana", "AbcDef", "abcdef", false)]
    [InlineData("Solana", "AbcDef", "AbcDef", true)]
    public void ComparisonRespectsChainEncoding(string chain, string left, string right, bool expected) =>
        ChainAddressIdentity.Equivalent(chain, left, right).Should().Be(expected);

    [Fact]
    public async Task ExistenceWithoutMatchingOwnerCannotAuthorizeAccessOrTransfer()
    {
        var h = new NftLifecycleHarness();
        var actor = Guid.NewGuid();
        h.AddWallet(actor);
        var minted = await h.Service.MintAsync(actor, actor, NftLifecycleHarness.MintRequest(), "ownership", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        minted.Result!.State.Should().Be("Completed");
        h.Module.Setup(module => module.ReadNftOwnershipAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync(AZOAResult<NftOwnershipObservation>.Success(new(true, "another-owner", false)));

        (await h.Service.IsCurrentOwnerAsync(actor, minted.Result.NftId)).Should().BeFalse();
        var move = await h.Service.MoveAsync(actor, minted.Result.NftId, "recipient", false, "transfer", credential: AZOA.WebAPI.Tests.TestSupport.NftLifecycleHarness.Credential(actor));
        move.IsError.Should().BeTrue();
        h.Prepared.Should().ContainSingle();
    }
}
