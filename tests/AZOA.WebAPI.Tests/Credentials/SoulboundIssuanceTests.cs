// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Tests.Credentials;

/// <summary>
/// D41 — soulbound credentials are ISSUED BY one party TO another. These tests drive the
/// real production mint seam (<c>UserNFTService.MintUserNFTAsync</c>) and inspect the
/// ownership row it hands the store.
///
/// <para><b>Why this is where the track starts.</b> D19 authorizes revocation to the
/// credential's issuer, and the only mint path stamps <c>issuer_user_id = userNFT.UserId</c>
/// — the holder. The authorization check is therefore not enforced, it is VACUOUS, and no
/// amount of work on the revoke path can fix data the mint path never writes.</para>
/// </summary>
public sealed class SoulboundIssuanceTests
{
    private const string TokenId = "778899";

    private readonly CredentialRailHarness _rail = new();

    /// <summary>
    /// AC1. The caller is the ISSUER of record and the recipient is the HOLDER, and the two
    /// differ — asserted explicitly, so the vacuous "A issued it to A" case cannot pass this
    /// test quietly. The issuer is also taken from the authenticated caller and never from the
    /// request body: a body-supplied issuer is a straight authorization bypass, because the
    /// issuer is the single input the revoke decision reads.
    /// </summary>
    [Fact]
    public async Task SoulboundMint_RecordsTheCallerAsIssuer_AndTheRecipientAsHolder()
    {
        var attempt = await CredentialTrackSeams.IssueSoulboundAsync(
            _rail.MintService, _rail.IssuerA, _rail.RecipientB,
            CredentialRailHarness.SoulboundModelFor(TokenId));

        attempt.SeamExists.Should().BeTrue(CredentialTrackSeams.MissingSeamMessage);
        attempt.Outcome!.IsError.Should().BeFalse(attempt.Outcome.Message);

        var row = _rail.MintedRows.Should().ContainSingle(
            "one mint writes exactly one ownership row").Subject;

        row.UserId.Should().Be(_rail.RecipientB,
            "user_id on nft_ownership is the HOLDER, and the holder of an issued credential is "
            + "its recipient — CredentialOwnershipRecord.HolderUserId is projected straight from it");

        CredentialTrackSeams.TryReadIssuer(row, out var issuerUserId)
            .Should().BeTrue(CredentialTrackSeams.MissingIssuerMessage);

        issuerUserId.Should().Be(_rail.IssuerA,
            "the issuer of record is the authenticated minting caller");

        issuerUserId.Should().NotBe(row.UserId,
            "AC1: issuer and holder must be different parties. While they are the same, "
            + "'only the issuer may revoke' reduces to 'the holder may revoke their own "
            + "credential' and D19 enforces nothing at all");

        CredentialTrackSeams.WritableIssuerPropertyOn(typeof(UserNFTMintModel)).Should().BeNull(
            "a caller-supplied issuer would let anyone nominate themselves the revocation "
            + "authority over a credential they did not issue");
    }

    /// <summary>
    /// AC3. <c>current_owner</c> is the holder address the revoke path claws the asset back
    /// FROM. No production path writes it today, so <c>CredentialManager</c> refuses every
    /// real revocation at "has no recorded holder address" before authorization is even
    /// reached — the rail is inert regardless of everything else in this track.
    /// </summary>
    [Fact]
    public async Task SoulboundMint_WritesCurrentOwner_FromTheRecipientsWalletOnThatChain()
    {
        var attempt = await CredentialTrackSeams.IssueSoulboundAsync(
            _rail.MintService, _rail.IssuerA, _rail.RecipientB,
            CredentialRailHarness.SoulboundModelFor(TokenId));

        attempt.SeamExists.Should().BeTrue(CredentialTrackSeams.MissingSeamMessage);
        attempt.Outcome!.IsError.Should().BeFalse(attempt.Outcome.Message);

        var row = _rail.MintedRows.Should().ContainSingle().Subject;

        row.CurrentOwner.Should().Be(_rail.RecipientAddress,
            "AC3: current_owner must name the holder's address on the credential's own chain, "
            + "or the revoke precondition is unreachable and no credential is ever revocable");
    }

    /// <summary>
    /// A credential whose holder address cannot be proven must not be minted. Issuing one
    /// anyway re-creates exactly today's defect — a credential that reads as valid and that
    /// no party can ever revoke — which is strictly worse than refusing the mint.
    /// </summary>
    [Fact]
    public async Task SoulboundMint_IsRefused_WhenTheRecipientHasNoWalletOnTheChain()
    {
        _rail.HasNoWallet(_rail.RecipientB);

        var attempt = await CredentialTrackSeams.IssueSoulboundAsync(
            _rail.MintService, _rail.IssuerA, _rail.RecipientB,
            CredentialRailHarness.SoulboundModelFor(TokenId));

        attempt.SeamExists.Should().BeTrue(CredentialTrackSeams.MissingSeamMessage);

        attempt.Outcome!.IsError.Should().BeTrue(
            "a soulbound credential with no provable holder address is permanently unrevocable; "
            + "the mint must fail closed rather than issue one");

        _rail.MintedRows.Should().BeEmpty(
            "the refusal must happen before the ownership row is written, not after");
    }
}
