using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

/// <summary>Holding closure and allowance revocation are separate, mutually exclusive owner consents.</summary>
public sealed class SwapCleanupContractTests
{
    [Theory]
    [InlineData(SwapCleanupContract.CloseCurrentZeroHolding, true, false, "output", false, true)]
    [InlineData(SwapCleanupContract.CloseCurrentZeroHolding, true, true, "output", false, false)]
    [InlineData(SwapCleanupContract.CloseCurrentZeroHolding, false, false, "output", false, false)]
    [InlineData(SwapCleanupContract.CloseCurrentZeroHolding, true, false, "input", false, false)]
    [InlineData(SwapCleanupContract.RevokeCurrentAllowance, false, true, "input", false, true)]
    [InlineData(SwapCleanupContract.RevokeCurrentAllowance, true, true, "input", false, false)]
    [InlineData(SwapCleanupContract.RevokeCurrentAllowance, false, true, "output", false, false)]
    [InlineData(SwapCleanupContract.RevokeCurrentAllowance, false, true, "input", true, false)]
    [InlineData("ApproveSomething", false, true, "input", false, false)]
    public void Each_cleanup_semantic_requires_only_its_own_flag_and_original_asset(string semantics, bool closeFlag, bool revokeFlag,
        string asset, bool nativeInput, bool valid)
    {
        var intent = Intent(nativeInput);
        var consent = new SwapCleanupConsent(1, intent.OperationId, intent.QuoteHash, new string('a', 64), intent.Terms.ActorUserId, null,
            intent.Terms.Owner, asset == "input" ? intent.Terms.InputAsset.Id : intent.Terms.OutputAsset.Id, semantics, "100", closeFlag, revokeFlag);

        SwapCleanupContract.IsValid(intent, consent).Should().Be(valid);
    }

    [Theory]
    [InlineData(true, false, SwapCleanupContract.CloseCurrentZeroHolding)]
    [InlineData(false, true, SwapCleanupContract.RevokeCurrentAllowance)]
    [InlineData(true, true, null)]
    [InlineData(false, false, null)]
    public void Request_flags_derive_exactly_one_affirmative_semantic(bool close, bool revoke, string? expected)
        => SwapCleanupContract.Semantics(close, revoke).Should().Be(expected);

    private static SwapIntent Intent(bool nativeInput)
    {
        var actor = Guid.NewGuid();
        var owner = new DexParticipant(actor, Guid.NewGuid(), "0x1111111111111111111111111111111111111111");
        var terms = new DexQuoteTerms(1, actor, null, owner, owner, owner.Address, new("Ethereum", "Devnet", "eip155:31337"),
            new(nativeInput ? "ETH" : "0x6", "IN", 18, nativeInput), new("0x7", "OUT", 6, false), "1010", "1000", "2000", "1990", 50,
            new(NodeFeeSchedule.InputBaseUnitsV1, 1, "10", 0, "10", "0x9"), new("10000", "0", "1000"), "UniswapV3", "classic-exact-input/1",
            JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, []);
        return new(1, "operation", "quote", CanonicalJson.Hash(terms), terms, "request");
    }
}
