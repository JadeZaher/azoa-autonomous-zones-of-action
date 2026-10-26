using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Helpers;

/// <summary>
/// Pins the hold-record id a tenant derives to address <c>GET /api/hold-commands/liabilities/{id}</c>.
/// ArdaNova's AzoaHoldClient computes the same literal (its AzoaHoldClientContractTests); a change
/// here strands every hold a tenant can no longer address.
/// </summary>
public sealed class HoldCommandIdentityContractTests
{
    [Theory]
    [InlineData("Algorand", ChainNetwork.Devnet, "genesis", "award-bid-1", HoldRecordVectors.DevnetGenesis)]
    [InlineData("Algorand", ChainNetwork.Testnet, "SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=+", "award-bid-1-window",
        HoldRecordVectors.TestnetEscapedProtocol)]
    public void HoldRecordIdIsTheStableTenantAddressableVector(string chain, ChainNetwork network, string protocol,
        string holdId, string expected)
    {
        var owner = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var asset = new HoldAssetIdentity(chain, network, protocol, "42", "reserve");
        var intent = new HoldCommandIntent(1, "open:" + holdId, "Open", holdId, owner, owner, null, null, asset);

        HoldCommandIdentity.Hold(intent).Should().Be(expected);
    }

    private static class HoldRecordVectors
    {
        public const string DevnetGenesis = "94432cfe7b0e3f02c2d5f2250b39468f05b1e45c23092b44d96cf66ba3087e66";
        public const string TestnetEscapedProtocol = "920b8f2030c870373ffcaffb60de513a260b6bbc452cddd4ddc605ec92ee6061";
    }
}
