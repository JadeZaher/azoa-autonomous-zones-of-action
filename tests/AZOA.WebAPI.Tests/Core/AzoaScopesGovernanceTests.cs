using FluentAssertions;
using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Tests.Core;

public sealed class AzoaScopesGovernanceTests
{
    [Fact]
    public void NodeGovern_IsJwtOnly_NotApiKeyIssuableOrSelfIssuable()
    {
        AzoaScopes.NodeGovern.Should().Be("node:govern");
        AzoaScopes.IsApiKeyIssuableScope(AzoaScopes.NodeGovern).Should().BeFalse();
        AzoaScopes.IsIssuableByAvatar(AzoaScopes.NodeGovern).Should().BeFalse();
        AzoaScopes.IssuableCapabilityScopes.Should().NotContain(AzoaScopes.NodeGovern);
        AzoaScopes.IssuableScopeCatalog().Should().NotContain(s => s.Scope == AzoaScopes.NodeGovern);
    }

    [Fact]
    public void NodeGovern_IsNotAValueSigningScope()
    {
        AzoaScopes.ValueSigningScopes.Should().NotContain(AzoaScopes.NodeGovern);
        AzoaScopes.IsScopeValidForOperation("Mint", AzoaScopes.NodeGovern).Should().BeFalse();
        AzoaScopes.IsScopeValidForOperation("Transfer", AzoaScopes.NodeGovern).Should().BeFalse();
    }
}
