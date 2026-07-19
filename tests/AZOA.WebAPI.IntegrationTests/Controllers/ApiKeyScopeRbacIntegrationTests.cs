using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Auth;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

public class ApiKeyScopeRbacIntegrationTests : IntegrationTestBase
{
    public ApiKeyScopeRbacIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    // A minimal DappDevelop-gated authoring body (POST api/holon).
    private static HolonCreateModel AuthoringHolon() => new()
    {
        Name = "rbac-probe",
        Description = "probe",
        ProviderName = "SurrealDb",
    };

    // Mints a REAL API key for the given avatar (persisted key row + hash) so a
    // subsequent X-Api-Key request exercises the real ApiKeyAuthenticationHandler.
    // The caller must be authenticated as an avatar whose CURRENT role can self-issue
    // the requested scope.
    private async Task<string> MintRealApiKeyAsync(HttpClient owner, string? scopes)
    {
        var response = await owner.PostAsJsonAsync("api/apikey", new CreateApiKeyRequest
        {
            Name = "rbac-real-key",
            Scopes = scopes,
        }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"key mint should succeed; body: {await response.Content.ReadAsStringAsync()}");

        var result = await response.Content.ReadFromJsonAsync<AZOAResult<CreateApiKeyResponse>>(JsonOptions);
        return result!.Result!.Key;
    }

    [Fact]
    public async Task ScopeDiscovery_AsPlainDappUser_ExcludesDappAuthoringScopes()
    {
        using var plainUser = Factory.CreateAuthenticatedClient(AzoaDappRoles.User);

        var response = await plainUser.GetAsync("api/apikey/scopes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<List<ApiKeyScopeInfo>>>(JsonOptions);
        result!.Result.Should().NotContain(s => s.Scope == AzoaScopes.DappDevelop);
        result.Result.Should().NotContain(s => s.Scope == AzoaScopes.DappManage);
        result.Result.Should().Contain(s => s.Scope == AzoaScopes.WalletManage);
    }

    [Fact]
    public async Task ScopeDiscovery_AsDeveloper_ExposesDevelopButNotManage()
    {
        using var developer = Factory.CreateAuthenticatedClient(AzoaDappRoles.Developer);

        var response = await developer.GetAsync("api/apikey/scopes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<List<ApiKeyScopeInfo>>>(JsonOptions);
        result!.Result.Should().Contain(s => s.Scope == AzoaScopes.DappDevelop && s.IsSelfIssuable);
        result.Result.Should().NotContain(s => s.Scope == AzoaScopes.DappManage);
    }

    [Fact]
    public async Task ScopeDiscovery_AsManager_ExposesDevelopAndManage()
    {
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await manager.GetAsync("api/apikey/scopes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<AZOAResult<List<ApiKeyScopeInfo>>>(JsonOptions);
        result!.Result.Should().Contain(s => s.Scope == AzoaScopes.DappDevelop && s.IsSelfIssuable);
        result.Result.Should().Contain(s => s.Scope == AzoaScopes.DappManage && s.IsSelfIssuable);
        result.Result.Should().NotContain(s => s.Scope == AzoaScopes.NodeGovern);
    }

    [Fact]
    public async Task Create_AsManager_WithNodeGovernScope_ShouldReturnBadRequest()
    {
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await manager.PostAsJsonAsync("api/apikey", new CreateApiKeyRequest
        {
            Name = "bad-node-govern-key",
            Scopes = AzoaScopes.NodeGovern,
        }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(AzoaScopes.NodeGovern);
    }

    [Fact]
    public async Task NodeGovernPolicy_IsJwtOnlyAndDistinctFromOperator()
    {
        using var scope = Factory.Services.CreateScope();
        var authz = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var apiKeyWithLiteralScope = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("AuthMethod", "ApiKey"),
            new Claim("scope", AzoaScopes.NodeGovern),
        }, "ApiKey"));
        var jwtWithOperatorOnly = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(AzoaClaims.TokenUse, AzoaClaims.TokenUseNodeOperator),
            new Claim("scope", AzoaScopes.Operator),
            new Claim("role", "Admin"),
        }, "Bearer"));
        var jwtWithNodeGovern = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(AzoaClaims.TokenUse, AzoaClaims.TokenUseNodeOperator),
            new Claim("scope", AzoaScopes.NodeGovern),
        }, "Bearer"));

        (await authz.AuthorizeAsync(apiKeyWithLiteralScope, resource: null, "NodeGovern"))
            .Succeeded.Should().BeFalse("API keys must never satisfy node governance");
        (await authz.AuthorizeAsync(jwtWithOperatorOnly, resource: null, "NodeGovern"))
            .Succeeded.Should().BeFalse("economic governance remains a separate capability");
        (await authz.AuthorizeAsync(jwtWithNodeGovern, resource: null, "NodeGovern"))
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Create_AsPlainDappUser_WithDappDevelopScope_ShouldReturnBadRequest()
    {
        using var plainUser = Factory.CreateAuthenticatedClient(AzoaDappRoles.User);

        var response = await plainUser.PostAsJsonAsync("api/apikey", new CreateApiKeyRequest
        {
            Name = "bad-dev-key",
            Scopes = AzoaScopes.DappDevelop,
        }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(AzoaScopes.DappDevelop);
    }

    [Fact]
    public async Task Create_AsDeveloper_WithDappManageScope_ShouldReturnBadRequest()
    {
        using var developer = Factory.CreateAuthenticatedClient(AzoaDappRoles.Developer);

        var response = await developer.PostAsJsonAsync("api/apikey", new CreateApiKeyRequest
        {
            Name = "bad-manager-key",
            Scopes = AzoaScopes.DappManage,
        }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(AzoaScopes.DappManage);
    }

    // ── FIX 2: antagonistic RBAC scenarios (hit the REAL ApiKey scheme) ─────────

    // KEYSTONE (AC3): a scoped dapp:develop key minted while the owner was a developer
    // must be DENIED after the owner's role is revoked to dapp:user — even though the
    // key still carries the stale scope. This exercises the real handler's re-read of
    // the owner's CURRENT DappRole.
    [Fact]
    public async Task ApiKey_WithStaleDappDevelopScope_AfterRoleRevocation_Returns403()
    {
        var avatar = await SeedAvatarAsync();
        using var operatorClient = Factory.CreateOperatorClient();

        // Promote to developer, then mint a scoped key as that developer.
        (await AssignRoleAsync(operatorClient, avatar.Id, AzoaDappRoles.Developer))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using var developer = Factory.CreateAuthenticatedClientForAvatar(avatar.Id, AzoaDappRoles.Developer);
        var rawKey = await MintRealApiKeyAsync(developer, AzoaScopes.DappDevelop);

        // Revoke the role back to plain user.
        (await AssignRoleAsync(operatorClient, avatar.Id, AzoaDappRoles.User))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The stale-scope key can no longer reach a DappDevelop write surface.
        using var apiKeyClient = Factory.CreateApiKeyClient(rawKey);
        var response = await apiKeyClient.PostAsJsonAsync("api/holon", AuthoringHolon(), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // AC2: new keys require explicit scopes, while a pre-hardening persisted empty-scope
    // key remains compatible. Its owner still needs the current developer role.
    [Fact]
    public async Task ApiKey_EmptyCsvLegacyKey_AgainstDemotedAvatar_Returns403()
    {
        var avatar = await SeedAvatarAsync();
        using var operatorClient = Factory.CreateOperatorClient();

        (await AssignRoleAsync(operatorClient, avatar.Id, AzoaDappRoles.Developer))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var rawKey = await SeedLegacyApiKeyAsync(avatar.Id);

        (await AssignRoleAsync(operatorClient, avatar.Id, AzoaDappRoles.User))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var apiKeyClient = Factory.CreateApiKeyClient(rawKey);
        var response = await apiKeyClient.PostAsJsonAsync("api/holon", AuthoringHolon(), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Enforcement boundary #3: a DApp manager is NOT an operator. A manager-role
    // principal calling an operator:admin-gated surface must be denied.
    [Fact]
    public async Task ManagerRole_CallingOperatorGatedEndpoint_Returns403()
    {
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await manager.GetAsync("api/admin/backfill/list");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Role-assignment authz: ordinary user and developer cannot assign roles.
    [Fact]
    public async Task AssignRole_AsPlainUser_Returns403()
    {
        var target = await SeedAvatarAsync();
        using var plainUser = Factory.CreateAuthenticatedClient(AzoaDappRoles.User);

        var response = await AssignRoleAsync(plainUser, target.Id, AzoaDappRoles.Developer);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AssignRole_AsDeveloper_Returns403()
    {
        var target = await SeedAvatarAsync();
        using var developer = Factory.CreateAuthenticatedClient(AzoaDappRoles.Developer);

        var response = await AssignRoleAsync(developer, target.Id, AzoaDappRoles.Developer);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // A manager may grant developer/user but NEVER manager (that needs an operator).
    [Fact]
    public async Task AssignRole_AsManager_GrantingManager_Returns403()
    {
        var target = await SeedAvatarAsync();
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await AssignRoleAsync(manager, target.Id, AzoaDappRoles.Manager);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AssignRole_AsManager_GrantingDeveloper_Succeeds()
    {
        var target = await SeedAvatarAsync();
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await AssignRoleAsync(manager, target.Id, AzoaDappRoles.Developer);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // The CRITICAL invariant: the endpoint can never assign operator:admin. An invalid
    // role token is rejected (not silently normalized to a privileged value).
    [Fact]
    public async Task AssignRole_AsOperator_WithOperatorAdminValue_Returns403()
    {
        var target = await SeedAvatarAsync();
        using var operatorClient = Factory.CreateOperatorClient();

        var response = await AssignRoleAsync(operatorClient, target.Id, AzoaScopes.Operator);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // The endpoint actually works: operator promotes an avatar to developer, and that
    // avatar can then reach a DappDevelop endpoint via a freshly-issued principal.
    [Fact]
    public async Task AssignRole_AsOperator_ToDeveloper_ThenAvatarCanAuthor()
    {
        var avatar = await SeedAvatarAsync();
        using var operatorClient = Factory.CreateOperatorClient();

        (await AssignRoleAsync(operatorClient, avatar.Id, AzoaDappRoles.Developer))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Fresh principal reflecting the new developer role reaches the authoring surface.
        using var developer = Factory.CreateAuthenticatedClientForAvatar(avatar.Id, AzoaDappRoles.Developer);
        var response = await developer.PostAsJsonAsync("api/holon", AuthoringHolon(), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Operator may assign manager (the bootstrap path that closes the first-manager gap).
    [Fact]
    public async Task AssignRole_AsOperator_ToManager_Succeeds()
    {
        var target = await SeedAvatarAsync();
        using var operatorClient = Factory.CreateOperatorClient();

        var response = await AssignRoleAsync(operatorClient, target.Id, AzoaDappRoles.Manager);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private Task<HttpResponseMessage> AssignRoleAsync(HttpClient client, Guid avatarId, string role)
        => client.PutAsJsonAsync($"api/avatar/{avatarId}/dapp-role",
            new AvatarRoleAssignmentModel { Role = role }, JsonOptions);

    private async Task<string> SeedLegacyApiKeyAsync(Guid avatarId)
    {
        var rawKey = ApiKeyAuthenticationHandler.GenerateRawKey();
        var legacyKey = new ApiKey
        {
            AvatarId = avatarId,
            Name = "legacy-empty-scope-key",
            KeyHash = ApiKeyAuthenticationHandler.HashKey(rawKey),
            KeyPrefix = rawKey[..16],
            Scopes = null,
        };

        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();
        await store.CreateAsync(legacyKey, CancellationToken.None);
        return rawKey;
    }
}
