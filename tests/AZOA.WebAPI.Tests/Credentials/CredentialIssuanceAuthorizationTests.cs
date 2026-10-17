// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Signing;
using SurrealForge.Client;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AZOA.WebAPI.Tests.Credentials;
public sealed class CredentialIssuanceAuthorizationTests
{
    private readonly Mock<INftLifecycleService> _mint = new();
    private readonly Mock<ICredentialManager> _revoke = new();

    private readonly Guid _caller = Guid.NewGuid();
    private readonly Guid _recipient = Guid.NewGuid();
    [Fact]
    public async Task Issue_IsRefused_WhenTheCallerIsNotADappManager()
    {
        var controller = CreateController(dappRole: AzoaDappRoles.User);

        var result = await controller.Issue(_recipient, CredentialModel());

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        VerifyNothingWasIssued();
    }
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
    [Fact]
    public async Task Issue_StampsTheIssuerFromTheCaller_AndTheHolderFromTheRoute()
    {
        Guid capturedIssuer = Guid.Empty;
        Guid capturedRecipient = Guid.Empty;
        UserNFTMintModel? capturedModel = null;
        SigningCredentialWitness? capturedCredential = null;

        _mint.Setup(m => m.MintAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UserNFTMintModel>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid>(),
                It.IsAny<ActionEffectAuthority?>(), It.IsAny<SigningCredentialWitness?>()))
            .Callback<Guid, Guid, UserNFTMintModel, string, CancellationToken, Guid, ActionEffectAuthority?, SigningCredentialWitness?>(
                (issuer, recipient, model, _, _, _, _, credential) =>
                {
                    capturedIssuer = issuer;
                    capturedRecipient = recipient;
                    capturedModel = model;
                    capturedCredential = credential;
                })
            .ReturnsAsync(AZOAResult<NftLifecycleView>.Success(new(Guid.NewGuid().ToString("N"), Guid.NewGuid(), "AwaitingRecipientAcceptance", "Accept", "123", "create-tx", null, "Mint")));

        var controller = CreateController(dappRole: AzoaDappRoles.Manager);

        var result = await controller.Issue(_recipient, CredentialModel());

        result.Result.Should().BeOfType<AcceptedResult>();
        capturedIssuer.Should().Be(_caller,
            "the issuer of record is the authenticated caller; a body-supplied issuer would be an "
            + "authorization bypass, because the issuer is the only input the revoke decision reads");
        capturedRecipient.Should().Be(_recipient);
        capturedIssuer.Should().NotBe(capturedRecipient,
            "issuer == holder is the vacuous credential D41 exists to end");
        capturedModel!.IsSoulbound.Should().BeTrue("this route issues credentials, not tradeable NFTs");
        capturedModel.IsTransferable.Should().BeFalse();
        capturedCredential!.ActorUserId.Should().Be(_caller, "the original issuer authority is captured server-side from the principal");
    }
    [Fact]
    public void TheIssuanceRequestModel_HasNoWritableIssuer()
    {
        CredentialTrackSeams.WritableIssuerPropertyOn(typeof(UserNFTMintModel)).Should().BeNull(
            "a caller-supplied issuer would let anyone nominate themselves the revocation authority "
            + "over a credential they did not issue");
    }

    private static UserNFTMintModel CredentialModel() => new()
    {
        ChainType = "Algorand",
        NFTContractAddress = "CREDENTIALS",
        TokenStandard = "ASA",
        MetadataURI = "ipfs://credential",
        Name = "Membership tier",
    };

    private void VerifyNothingWasIssued() =>
        _mint.Verify(m => m.MintAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UserNFTMintModel>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid>(),
                It.IsAny<ActionEffectAuthority?>(), It.IsAny<SigningCredentialWitness?>()),
            Times.Never,
            "an unauthorized caller must never reach the mint seam at all");

    private CredentialController CreateController(
        string dappRole, string? authMethod = null, params Claim[] extraClaims)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new("UserId", _caller.ToString()),
            new(ClaimTypes.NameIdentifier, _caller.ToString()),
            new("dapp_role", dappRole),
            new(AzoaClaims.TokenUse, AzoaClaims.TokenUseLogin),
            new(AzoaClaims.AuthTime, now.AddMinutes(-1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new("exp", now.AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        };
        if (authMethod is not null)
            claims.Add(new Claim("AuthMethod", authMethod));
        claims.AddRange(extraClaims);

        var users = new Mock<IUserStore>();
        users.Setup(store => store.GetByIdAsync(_caller, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.Success(Mock.Of<IUser>(user => user.IsActive)));
        var signing = new SigningAdmissionAuthorityFactory(users.Object, Mock.Of<IWalletStore>(), Mock.Of<ISurrealExecutor>());
        return new CredentialController(_revoke.Object, _mint.Object, signing)
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
