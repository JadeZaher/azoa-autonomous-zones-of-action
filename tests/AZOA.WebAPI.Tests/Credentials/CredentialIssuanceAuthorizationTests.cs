// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AZOA.WebAPI.Tests.Credentials;

/// <summary>
/// D41 Phase 0 made into a control: WHO MAY ISSUE a credential to somebody else.
///
/// <para>The answer recorded on <see cref="CredentialController.Issue"/> and in the track plan is
/// ROLE-GATED — the caller must hold <c>dapp:manager</c>. These tests exist because a decision
/// stated only in a doc-comment is prose, not a control; delete the gate and they fail. Open
/// issuance is a griefing primitive: a credential is soulbound and revocable ONLY by its issuer,
/// so an unsolicited one can never be removed by the person carrying it.</para>
///
/// <para>They also pin the other half of the rule the whole rail hangs on: the issuer is stamped
/// from the AUTHENTICATED CALLER and the recipient from the route, never from the request body.
/// The issuer is the single input the D19 revoke decision reads, so a body-supplied one would let
/// any caller nominate itself the revocation authority over a credential it did not issue.</para>
/// </summary>
public sealed class CredentialIssuanceAuthorizationTests
{
    private readonly Mock<IUserNFTService> _mint = new();
    private readonly Mock<ICredentialManager> _revoke = new();

    private readonly Guid _caller = Guid.NewGuid();
    private readonly Guid _recipient = Guid.NewGuid();

    /// <summary>
    /// An ordinary authenticated user is not an issuer. <c>dapp_role</c> is absent, which
    /// <c>AzoaDappRoles.Normalize</c> reads as <c>dapp:user</c>, and a user cannot give themselves
    /// the manager role — so this is a real restriction rather than a check every JWT passes.
    /// </summary>
    [Fact]
    public async Task Issue_IsRefused_WhenTheCallerIsNotADappManager()
    {
        var controller = CreateController(dappRole: AzoaDappRoles.User);

        var result = await controller.Issue(_recipient, CredentialModel());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        VerifyNothingWasIssued();
    }

    /// <summary>A manager whose credential is a SCOPED key must still carry the mint capability.</summary>
    [Fact]
    public async Task Issue_IsRefused_WhenAScopedKeyLacksTheMintCapability()
    {
        var controller = CreateController(
            dappRole: AzoaDappRoles.Manager,
            authMethod: "ApiKey",
            new Claim("scope", AzoaScopes.KycRead));

        var result = await controller.Issue(_recipient, CredentialModel());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        VerifyNothingWasIssued();
    }

    /// <summary>
    /// The permitted case, and the shape it hands the mint seam: issuer = the authenticated
    /// caller, holder = the route recipient, soulbound and non-transferable by construction.
    /// </summary>
    [Fact]
    public async Task Issue_StampsTheIssuerFromTheCaller_AndTheHolderFromTheRoute()
    {
        Guid capturedIssuer = Guid.Empty;
        Guid capturedRecipient = Guid.Empty;
        UserNFTMintModel? capturedModel = null;

        _mint.Setup(m => m.MintUserNFTAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UserNFTMintModel>(), It.IsAny<AZOARequest?>()))
            .Callback<Guid, Guid, UserNFTMintModel, AZOARequest?>(
                (issuer, recipient, model, _) =>
                {
                    capturedIssuer = issuer;
                    capturedRecipient = recipient;
                    capturedModel = model;
                })
            .ReturnsAsync(new AZOAResult<IUserNFT> { Result = Mock.Of<IUserNFT>() });

        var controller = CreateController(dappRole: AzoaDappRoles.Manager);

        var result = await controller.Issue(_recipient, CredentialModel());

        result.Result.Should().BeOfType<OkObjectResult>();
        capturedIssuer.Should().Be(_caller,
            "the issuer of record is the authenticated caller; a body-supplied issuer would be an "
            + "authorization bypass, because the issuer is the only input the revoke decision reads");
        capturedRecipient.Should().Be(_recipient);
        capturedIssuer.Should().NotBe(capturedRecipient,
            "issuer == holder is the vacuous credential D41 exists to end");
        capturedModel!.IsSoulbound.Should().BeTrue("this route issues credentials, not tradeable NFTs");
        capturedModel.IsTransferable.Should().BeFalse();
    }

    /// <summary>
    /// The body carries no issuer at all, so there is nothing for the controller to prefer over the
    /// authenticated caller even by mistake. Stated as a test because the guarantee above is only as
    /// strong as the absence of that field.
    /// </summary>
    [Fact]
    public void TheIssuanceRequestModel_HasNoWritableIssuer()
    {
        CredentialTrackSeams.WritableIssuerPropertyOn(typeof(UserNFTMintModel)).Should().BeNull(
            "a caller-supplied issuer would let anyone nominate themselves the revocation authority "
            + "over a credential they did not issue");
    }

    // ── Arrangement ───────────────────────────────────────────────────────────

    private static UserNFTMintModel CredentialModel() => new()
    {
        ChainType = "Algorand",
        TokenId = "778899",
        NFTContractAddress = "CREDENTIALS",
        TokenStandard = "ASA",
        MetadataURI = "ipfs://credential",
        Name = "Membership tier",
    };

    private void VerifyNothingWasIssued() =>
        _mint.Verify(m => m.MintUserNFTAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UserNFTMintModel>(), It.IsAny<AZOARequest?>()),
            Times.Never,
            "an unauthorized caller must never reach the mint seam at all");

    private CredentialController CreateController(
        string dappRole, string? authMethod = null, params Claim[] extraClaims)
    {
        var claims = new List<Claim>
        {
            new("UserId", _caller.ToString()),
            new("dapp_role", dappRole),
        };
        if (authMethod is not null)
            claims.Add(new Claim("AuthMethod", authMethod));
        claims.AddRange(extraClaims);

        return new CredentialController(_revoke.Object, _mint.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
                },
            },
        };
    }
}
