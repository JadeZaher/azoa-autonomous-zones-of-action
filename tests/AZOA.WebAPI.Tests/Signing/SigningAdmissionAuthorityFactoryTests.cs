using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using ApiKeyRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ApiKey;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class SigningAdmissionAuthorityFactoryTests
{
    [Fact]
    public async Task ScopeExpansionAfterAuthenticationDoesNotWidenOriginalApiCredential()
    {
        var actor = Guid.NewGuid();
        var key = new ApiKeyRow { Id = Guid.NewGuid().ToString("N"), UserId = $"user:{actor:N}", IsActive = true,
            KeyHash = "fixture-hash", Scopes = [AzoaScopes.ActionExecute, AzoaScopes.TokenCreateSign] };
        var service = Factory(actor, key);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, actor.ToString()), new("AuthMethod", "ApiKey"), new("ApiKeyId", key.Id),
            new("scope", AzoaScopes.ActionExecute)], "validated-fixture"));
        var result = await service.CaptureCredentialAsync(principal);
        result.IsError.Should().BeFalse();
        result.Result!.Scopes.Should().Equal(AzoaScopes.ActionExecute);
        result.Result.StoredApiKeyScopes.Should().Equal(key.Scopes);
        result.Result.OwnerAuthority.Should().BeFalse();
        var binding = await service.BindDirectAsync(result.Result, Guid.NewGuid(), "Algorand", "fixture-address", [AzoaScopes.TokenCreateSign]);
        binding.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task NodeOperatorCredentialDoesNotBecomeUnrestrictedOwnerSigningAuthority()
    {
        var actor = AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.UserId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.NameIdentifier, actor.ToString()), new(AzoaClaims.TokenUse, AzoaClaims.TokenUseNodeOperator),
            new(AzoaClaims.AuthTime, now.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("exp", (now + 300).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(AzoaClaims.OperatorRevision, "2"), new(AzoaClaims.OperatorSessionRevision, "3"), new("scope", AzoaScopes.Operator)
        ], "validated-fixture"));
        var service = Factory(actor, null);
        var result = await service.CaptureCredentialAsync(principal);
        result.IsError.Should().BeFalse();
        result.Result!.OwnerAuthority.Should().BeFalse();
        result.Result.OperatorSessionRevision.Should().Be(3);
        var binding = await service.BindDirectAsync(result.Result, Guid.NewGuid(), "Algorand", "fixture-address", [AzoaScopes.TokenCreateSign]);
        binding.IsError.Should().BeTrue();
        SigningAuthorityFence.ForRunAdmission(result.Result, "operator_fixture").Should().NotBeNull();
    }

    private static SigningAdmissionAuthorityFactory Factory(Guid actor, ApiKeyRow? key)
    {
        var users = new Mock<IUserStore>();
        users.Setup(store => store.GetByIdAsync(actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IUser>.Success(new AZOA.WebAPI.Models.User { Id = actor, IsActive = true }));
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(store => store.QuerySingleAsync<ApiKeyRow>(It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(key);
        return new(users.Object, new Mock<IWalletStore>(MockBehavior.Strict).Object, executor.Object);
    }
}
