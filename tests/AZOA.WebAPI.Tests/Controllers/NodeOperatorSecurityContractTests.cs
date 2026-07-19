using FluentAssertions;

namespace AZOA.WebAPI.Tests.Controllers;

public sealed class NodeOperatorSecurityContractTests
{
    [Fact]
    public void OperatorJwt_RemainsShortLivedDedicatedAndRevisionBound()
    {
        var source = ReadRepositoryFile("Managers", "NodeOperatorManager.cs");

        source.Should().Contain("Math.Clamp(_options.SessionMinutes, 5, 30)")
            .And.Contain("AzoaClaims.TokenUseNodeOperator")
            .And.Contain("AzoaClaims.OperatorRevision")
            .And.Contain("AzoaClaims.AuthTime")
            .And.Contain("JwtRegisteredClaimNames.Jti")
            .And.Contain("AzoaScopes.Operator")
            .And.Contain("AzoaScopes.NodeGovern")
            .And.Contain("NodeOperatorIdentity.AvatarId")
            .And.Contain("NodeOperatorIdentity.ReservedEmail");
    }

    [Fact]
    public void OperatorSeed_RemainsBoundAndRequiresMonotonicCredentialRotation()
    {
        var source = ReadRepositoryFile("Services", "Admin", "SeedAdminHostedService.cs");

        source.Should().Contain("CreateIfAbsentAsync")
            .And.Contain("NodeOperatorIdentity.AvatarId")
            .And.Contain("NodeOperatorIdentity.ReservedEmail")
            .And.Contain("credential revision rollback was refused")
            .And.Contain("credentials differ without a revision increase")
            .And.Contain("RotateCredentialsAsync")
            .And.NotContain("Password = _operator.Password");
    }

    [Fact]
    public void OperatorBff_KeepsTokensOutOfBrowserStorageAndSeparatesSignOutFromGlobalRevocation()
    {
        var bff = ReadRepositoryFile("frontend", "src", "lib", "operator-bff.ts");
        var sessionRoute = ReadRepositoryFile("frontend", "src", "app", "api", "operator", "session", "route.ts");
        var proxyRoute = ReadRepositoryFile("frontend", "src", "app", "api", "operator", "[...path]", "route.ts");
        var overview = ReadRepositoryFile("frontend", "src", "app", "operator", "(console)", "page.tsx");
        var shell = ReadRepositoryFile("frontend", "src", "components", "operator", "operator-shell.tsx");
        var operatorTree = string.Join(
            '\n',
            Directory.EnumerateFiles(
                    Path.Combine(FindRepositoryRoot(), "frontend", "src", "app", "operator"),
                    "*.*",
                    SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".ts", StringComparison.Ordinal)
                    || path.EndsWith(".tsx", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        bff.Should().Contain("httpOnly: true")
            .And.MatchRegex("sameSite:\\s*[\\\"']strict[\\\"']")
            .And.MatchRegex("path:\\s*[\\\"']/api/operator[\\\"']")
            .And.MatchRegex("process\\.env\\.NODE_ENV\\s*===\\s*[\\\"']production[\\\"']")
            .And.MatchRegex("claims\\.token_use\\s*!==\\s*[\\\"']node_operator[\\\"']")
            .And.Contain("claims.exp! > nowSeconds + 30 * 60 + 60")
            .And.Contain("session.accessToken.length > 4_096");
        sessionRoute.Should().NotContain("session/revoke");
        proxyRoute.Should().Contain("SESSION_REVOKE_PATH")
            .And.Contain("Object.keys(body).length === 0")
            .And.Contain("requireSameOriginMutation(request)");
        overview.Should().Contain("Revoke all operator sessions")
            .And.Contain("Yes, revoke every session");
        shell.Should().Contain("End operator session");
        operatorTree.Should().NotContain("localStorage")
            .And.NotContain("sessionStorage");
    }

    [Fact]
    public void ProviderFrontendContract_ExposesReadinessMetadataButNoSecretValues()
    {
        var contracts = ReadRepositoryFile("frontend", "src", "lib", "operator-contracts.ts");
        var providers = ReadRepositoryFile(
            "frontend", "src", "app", "operator", "(console)", "providers", "page.tsx");

        contracts.Should().Contain("trustRevision: number")
            .And.Contain("requiredConfigurationKeys: string[]")
            .And.Contain("missingConfigurationKeys: string[]");
        providers.Should().Contain("Values are write-only host secrets")
            .And.Contain("profile.missingConfigurationKeys.includes(key)")
            .And.NotContain("VeriffApiKey")
            .And.NotContain("WebhookSecret");
    }

    [Fact]
    public void OperatorTenantPage_UsesPersistedArrayScopeMembership()
    {
        var source = ReadRepositoryFile(
            "Providers", "Stores", "Surreal", "SurrealAvatarStore.cs");

        source.Should().Contain("scopes CONTAINS $_scope")
            .And.NotContain("string::split(scopes, ',')");
    }

    [Fact]
    public void OperatorKycAudit_IsBoundedFilteredAndSecretFree()
    {
        var controller = ReadRepositoryFile("Controllers", "NodeOperatorController.cs");
        var manager = ReadRepositoryFile("Managers", "KycControlPlaneManager.cs");
        var store = ReadRepositoryFile(
            "Providers", "Stores", "Surreal", "SurrealKycControlStore.cs");
        var response = ReadRepositoryFile("Models", "Responses", "NodeOperatorResponses.cs");

        controller.Should().Contain("[HttpGet(\"kyc/audit\")]")
            .And.Contain("ListAuditAsync");
        manager.Should().Contain("profile.trust-change")
            .And.Contain("profile.metadata-change")
            .And.Contain("tenant.provider-selection")
            .And.Contain("limit is < 1 or > 100");
        manager.Should().Contain("WebEncoders.Base64UrlEncode")
            .And.Contain("WebEncoders.Base64UrlDecode")
            .And.Contain("EncodeAuditCursor")
            .And.Contain("TryDecodeAuditCursor")
            .And.Contain("v2:{ticks}:{recordId}")
            .And.NotContain("Convert.ToBase64String(Encoding.UTF8.GetBytes($\"v1:{offset}\"))");
        store.Should().Contain("ListAuditPageAsync")
            .And.Contain(".WithParam(\"_tenant\"")
            .And.Contain(".WithParam(\"_provider\"")
            .And.Contain(".WithParam(\"_action\"")
            .And.Contain("occurred_at < $_before_occurred_at")
            .And.Contain("id < type::record($_table, $_before_id)")
            .And.NotContain("ORDER BY occurred_at DESC, id DESC START $_offset")
            .And.Contain("Math.Clamp(limit, 1, 101)");
        response.Should().Contain("class KycControlAuditResponse")
            .And.Contain("PreviousTrustRevision")
            .And.Contain("ActorAvatarId");
    }

    private static string ReadRepositoryFile(params string[] path)
        => File.ReadAllText(Path.Combine(new[] { FindRepositoryRoot() }.Concat(path).ToArray()));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AZOA.WebAPI.csproj")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the AZOA repository root.");
    }
}
