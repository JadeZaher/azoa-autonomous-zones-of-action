using FluentAssertions;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Tests.Core;

/// <summary>
/// security-review S6: the operation-type ↔ signing-scope consistency map. The signing
/// scope a tenant-driven op declares is a constant chosen by each op-builder; if a builder
/// MISLABELS a value-moving op (stamps a transfer with nft:mint because it reused a Mint
/// code path), the consent gate would check the WRONG capability. These cases assert the
/// single source of truth fails closed on every mismatch BEFORE a key decrypt.
/// </summary>
public class ScopeOperationMapTests
{
    [Theory]
    [InlineData("Transfer", AzoaScopes.TransferSign)]              // transfer signs under transfer:sign
    [InlineData("Mint", AzoaScopes.NftMint)]                       // mint signs under nft:mint
    [InlineData("Mint", AzoaScopes.GrantSign)]                     // mint-to-actor signs under grant:sign
    [InlineData("Swap", AzoaScopes.SwapSign)]                      // swap signs under swap:sign
    [InlineData("Exchange", AzoaScopes.SwapSign)]                  // exchange (swap alias) signs under swap:sign
    [InlineData("fungible_token_create", AzoaScopes.TokenCreateSign)] // ASA create signs under token:create:sign
    public void ValidScopeForOperation_IsAccepted(string operationType, string scope)
        => AzoaScopes.IsScopeValidForOperation(operationType, scope).Should().BeTrue();

    [Fact]
    public void TransferDeclaringMintScope_IsRejected()
    {
        // THE ATTACK: a value transfer MISLABELLED with the nft:mint scope. A user who
        // granted only nft:mint must NOT be made to sign a transfer — the map rejects the
        // mismatch so the wrong consent capability is never checked.
        AzoaScopes.IsScopeValidForOperation("Transfer", AzoaScopes.NftMint).Should().BeFalse();
    }

    [Fact]
    public void MintDeclaringTransferScope_IsRejected()
    {
        // ATTACK BLOCKED: a Mint op cannot launder itself under transfer:sign capability.
        AzoaScopes.IsScopeValidForOperation("Mint", AzoaScopes.TransferSign).Should().BeFalse();
    }

    [Fact]
    public void SwapDeclaringTransferScope_IsRejected()
    {
        // ATTACK BLOCKED: a Swap op cannot present itself as a transfer:sign capability.
        AzoaScopes.IsScopeValidForOperation("Swap", AzoaScopes.TransferSign).Should().BeFalse();
    }

    [Theory]
    [InlineData("Mint", null)]                         // a sign must name a concrete scope
    [InlineData("Mint", "")]                           // blank scope is never valid
    [InlineData(null, AzoaScopes.NftMint)]             // no operation type => deny
    [InlineData("UnknownOp", AzoaScopes.NftMint)]      // operation absent from the map => deny
    public void NullBlankOrUnknown_IsRejected(string? operationType, string? scope)
        => AzoaScopes.IsScopeValidForOperation(operationType, scope).Should().BeFalse();
}
