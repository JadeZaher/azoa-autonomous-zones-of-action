// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.Credentials;

/// <summary>
/// Revocation refusals on the NFT-lifecycle revoke path: every case must refuse before a clawback is
/// prepared and before a revoke lifecycle is admitted (re-covers the cases the pre-lifecycle
/// <c>CredentialRevokeAuthorizationTests</c> held).
/// </summary>
public sealed class CredentialRevokeRefusalTests
{
    private const string TokenId = "445566";
    private const string RequestKey = "revoke-refusal";

    private readonly CredentialRailHarness _rail = new();

    public static TheoryData<string> AuthorityShapes => new() { "unrecorded-issuer", "non-soulbound", "missing-holder-address" };

    [Theory]
    [MemberData(nameof(AuthorityShapes))]
    public async Task A_credential_whose_authority_cannot_support_a_clawback_is_refused_before_any_effect(string shape)
    {
        var nftId = await IssuedNftIdAsync();
        _rail.Rail.Store.MutateAuthority(nftId, authority =>
        {
            switch (shape)
            {
                case "unrecorded-issuer": authority.IssuerUserId = string.Empty; break;
                case "non-soulbound": authority.Soulbound = false; break;
                case "missing-holder-address": authority.OwnerAddress = string.Empty; break;
            }
        });
        var before = await _rail.Rail.Store.GetAuthorityAsync(nftId);

        var result = await _rail.RevokeAsync(TokenId, _rail.IssuerA, RequestKey);

        result.IsError.Should().BeTrue(shape);
        await AssertNothingHappenedAsync(nftId, before!);
    }

    [Theory]
    [InlineData("third-party", false)]
    [InlineData("holder-of-unrecorded-issuer", true)]
    public async Task A_caller_other_than_the_recorded_issuer_is_refused_and_issuance_is_never_inferred(string caller, bool eraseIssuer)
    {
        var nftId = await IssuedNftIdAsync();
        if (eraseIssuer) _rail.Rail.Store.MutateAuthority(nftId, authority => authority.IssuerUserId = string.Empty);
        var before = await _rail.Rail.Store.GetAuthorityAsync(nftId);
        var callerId = caller == "third-party" ? Guid.NewGuid() : _rail.RecipientB;

        var result = await _rail.RevokeAsync(TokenId, callerId, RequestKey);

        result.IsError.Should().BeTrue(caller);
        _rail.VerifyNoClawback();
        (await _rail.Rail.Store.GetAsync(NftLifecycleIdentity.ForRequest(callerId, "Revoke", RequestKey)))
            .Should().BeNull("a refused revocation admits no lifecycle");
        var after = await _rail.Rail.Store.GetAuthorityAsync(nftId);
        after!.Version.Should().Be(before!.Version);
        after.HolderUserId.Should().Be(_rail.RecipientB.ToString("N"));
    }

    [Fact]
    public async Task An_issuer_without_a_wallet_on_the_credential_chain_is_refused_before_any_effect()
    {
        var nftId = await IssuedNftIdAsync();
        var before = await _rail.Rail.Store.GetAuthorityAsync(nftId);
        _rail.HasNoWallet(_rail.IssuerA);

        var result = await _rail.RevokeAsync(TokenId, _rail.IssuerA, RequestKey);

        result.IsError.Should().BeTrue("the clawback destination is the issuer's own wallet on that chain");
        await AssertNothingHappenedAsync(nftId, before!);
    }

    [Fact]
    public async Task A_credential_lookup_failure_fails_closed()
    {
        var nftId = await IssuedNftIdAsync();
        var before = await _rail.Rail.Store.GetAuthorityAsync(nftId);
        _rail.Rail.Store.AuthorityLookupFault = new TimeoutException("store unavailable");

        var revoke = () => _rail.RevokeAsync(TokenId, _rail.IssuerA, RequestKey);

        await revoke.Should().ThrowAsync<TimeoutException>("an unreadable authority is never treated as permission");
        _rail.Rail.Store.AuthorityLookupFault = null;
        await AssertNothingHappenedAsync(nftId, before!);
    }

    private async Task<string> IssuedNftIdAsync()
    {
        var issued = await _rail.IssueAsync(TokenId);
        issued.Outcome!.IsError.Should().BeFalse(issued.Outcome.Message);
        return issued.Record!.Id.ToString("N");
    }

    private async Task AssertNothingHappenedAsync(string nftId, NftAssetAuthority before)
    {
        _rail.VerifyNoClawback();
        (await _rail.Rail.Store.GetAsync(NftLifecycleIdentity.ForRequest(_rail.IssuerA, "Revoke", RequestKey)))
            .Should().BeNull("a refused revocation admits no lifecycle");
        var after = await _rail.Rail.Store.GetAuthorityAsync(nftId);
        after!.Version.Should().Be(before.Version);
        after.Revoked.Should().BeFalse();
        after.HolderUserId.Should().Be(_rail.RecipientB.ToString("N"));
        after.ActiveLifecycleId.Should().BeNull();
    }
}
