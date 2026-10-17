using System.Security.Claims;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Services.Webhooks;
using FluentAssertions;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Webhooks;

public sealed class WebhookRegistrationManagementTests
{
    [Fact]
    public async Task Own_tenant_read_returns_only_explicit_metadata()
    {
        var actor = Guid.NewGuid();
        var registrations = new Mock<IWebhookRegistrationStore>(MockBehavior.Strict);
        registrations.Setup(store => store.GetByTenantAsync(actor, It.IsAny<CancellationToken>())).ReturnsAsync(
            AZOAResult<WebhookRegistration>.Success(new() { Id = Guid.NewGuid(), TenantId = actor, Url = "https://receiver.example",
                Secret = "must-not-return-plaintext", EncryptedSecret = "must-not-return-ciphertext", Version = 3 }));
        var result = await Service(actor, registrations).GetAsync(Login(actor), null, default);
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.TenantId.Should().Be(actor);
        var json = JsonSerializer.Serialize(result);
        json.Should().NotContain("must-not-return").And.NotContain("EncryptedSecret");
        registrations.VerifyAll();
    }

    [Fact]
    public async Task Ordinary_login_cannot_choose_operator_target_tenant()
    {
        var actor = Guid.NewGuid();
        var registrations = new Mock<IWebhookRegistrationStore>(MockBehavior.Strict);
        var result = await Service(actor, registrations).GetAsync(Login(actor), Guid.NewGuid(), default);
        result.IsError.Should().BeTrue();
        registrations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Child_cannot_manage_receivers_even_with_tenant_provision_scope()
    {
        var actor = Guid.NewGuid();
        var registrations = new Mock<IWebhookRegistrationStore>(MockBehavior.Strict);
        var claims = Login(actor).Claims.Where(claim => claim.Type != AzoaClaims.TokenUse && claim.Type != AzoaClaims.AuthTime).ToList();
        claims.Add(new(AzoaClaims.TokenUse, AzoaClaims.TokenUseChild));
        claims.Add(new("nbf", DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        claims.Add(new("scope", AzoaScopes.TenantProvision));
        claims.Add(new("act_as_tenant", Guid.NewGuid().ToString()));
        var result = await Service(actor, registrations).GetAsync(new(new ClaimsIdentity(claims, "fixture")), null, default);
        result.IsError.Should().BeTrue();
        registrations.VerifyNoOtherCalls();
    }

    private static WebhookRegistrationManagement Service(Guid actor, Mock<IWebhookRegistrationStore> registrations)
    {
        var users = new Mock<IUserStore>();
        users.Setup(store => store.GetByIdAsync(actor, It.IsAny<CancellationToken>())).ReturnsAsync(
            AZOAResult<IUser>.Success(new User { Id = actor, IsActive = true }));
        var factory = new SigningAdmissionAuthorityFactory(users.Object, new Mock<IWalletStore>(MockBehavior.Strict).Object,
            new Mock<ISurrealExecutor>(MockBehavior.Strict).Object);
        return new(factory, registrations.Object);
    }

    private static ClaimsPrincipal Login(Guid actor)
    {
        var now = DateTimeOffset.UtcNow;
        return new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.ToString()),
            new(AzoaClaims.TokenUse, AzoaClaims.TokenUseLogin),
            new(AzoaClaims.AuthTime, now.AddSeconds(-5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("exp", now.AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))], "fixture"));
    }
}
