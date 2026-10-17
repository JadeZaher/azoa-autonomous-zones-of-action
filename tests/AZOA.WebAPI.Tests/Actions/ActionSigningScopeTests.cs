using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Actions;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionSigningScopeTests
{
    [Theory]
    [InlineData(ActionNodeType.FungibleTokenCreate, AzoaScopes.TokenCreateSign)]
    [InlineData(ActionNodeType.Swap, AzoaScopes.SwapSign)]
    [InlineData(ActionNodeType.Bridge, AzoaScopes.WttSourceSign)]
    [InlineData(ActionNodeType.Back, AzoaScopes.WttSourceSign)]
    [InlineData(ActionNodeType.CredentialRevoke, AzoaScopes.CredentialRevokeSign)]
    public void EconomicNodeRetainsItsDomainScope(ActionNodeType type, string scope)
        => ActionSigningScopes.Required(type, "{}").Should().Equal(scope);

    [Fact]
    public void RuntimeRegistrationIsExplicitlyNonsigningAndUnknownKindsCannotInheritAccountSigning()
    {
        var runtime = JsonSerializer.Serialize(new { deployment = new { artifactId = "a", artifactHash = "b",
            target = new { kind = "PlatformRuntime", chainType = "", network = "" } } });
        ActionSigningScopes.Required(ActionNodeType.ActionGraphBundleDeploy, runtime).Should().BeEmpty();
        ActionSigningScopes.Required((ActionNodeType)int.MaxValue, "{}").Should().BeNull();
        var malformed = JsonSerializer.Serialize(new { deployment = new { artifactId = "a", artifactHash = "b", target = (object?)null } });
        ActionSigningScopes.Required(ActionNodeType.ActionGraphBundleDeploy, malformed).Should().BeNull();
        ActionSigningScopes.Required(ActionNodeType.ActionGraphBundleDeploy, "{}").Should().BeNull();
    }
}
