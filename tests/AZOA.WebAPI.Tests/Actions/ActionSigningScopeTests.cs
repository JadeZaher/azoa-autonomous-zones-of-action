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
    [InlineData(ActionNodeType.Bridge, AzoaScopes.TransferSign)]
    [InlineData(ActionNodeType.Back, AzoaScopes.TransferSign)]
    [InlineData(ActionNodeType.CredentialRevoke, AzoaScopes.CredentialRevokeSign)]
    [InlineData(ActionNodeType.Hold, AzoaScopes.TransferSign)]
    [InlineData(ActionNodeType.Release, AzoaScopes.TransferSign)]
    [InlineData(ActionNodeType.HoldRefund, AzoaScopes.TransferSign)]
    [InlineData(ActionNodeType.ReleaseIntoHold, AzoaScopes.TransferSign)]
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

    public static TheoryData<ActionNodeType, string> BridgeRoutes => new()
    {
        { ActionNodeType.Bridge, "wtt" }, { ActionNodeType.Back, "wtt" }, { ActionNodeType.Bridge, "legacy" }, { ActionNodeType.Back, "legacy" },
        { ActionNodeType.Bridge, "malformed" }, { ActionNodeType.Back, "malformed" },
    };

    /// <summary>X-1: the engine's handler, the admission store fence and participant re-authorization all read
    /// <see cref="ActionSigningScopes.Required"/>; it must equal the registry-selected handler and what WTT demands.</summary>
    [Theory]
    [MemberData(nameof(BridgeRoutes))]
    public void Bridge_and_back_scopes_follow_the_route_the_registry_selects(ActionNodeType type, string route)
    {
        var config = route switch
        {
            "wtt" => JsonSerializer.Serialize(new
            {
                wtt = new AZOA.WebAPI.Models.Wtt.WttActionConfig(new("Algorand", "Devnet", Guid.NewGuid()), "31566704", false, "1000000",
                    new("Solana", "Devnet", Guid.NewGuid()), "recipient", new("1000", "100000", "1000"), new("1000", "5000000", "1000")),
            }),
            // R-4: a malformed wtt body still selects the WTT route, so the fence and the handler agree and the WTT refusal wins.
            "malformed" => JsonSerializer.Serialize(new { wtt = "not-a-wtt-object" }),
            _ => JsonSerializer.Serialize(new { amount = "1" }),
        };
        var wtt = route != "legacy";
        var node = new ActionNode { Id = Guid.NewGuid(), NodeType = type, Config = config };
        var registry = new AZOA.WebAPI.Services.Actions.ActionNodeHandlerRegistry([new LegacyHandler(ActionNodeType.Bridge), new LegacyHandler(ActionNodeType.Back)],
            wttRoutes: [new AZOA.WebAPI.Services.Actions.WttActionResolver(null!, null!, null!, null!, null!)]);

        registry.TryGetFor(node, out var handler).Should().BeTrue();
        var required = ActionSigningScopes.Required(type, config);

        required.Should().Equal(handler.ResolveSigningScopes(node));
        required.Should().Equal(wtt ? AZOA.WebAPI.Providers.Stores.Surreal.WttSigningAuthorityFence.WorkflowScopes : [AzoaScopes.TransferSign],
            "a WTT node must select exactly the payer scopes WttLifecycleService requires, or re-authorization can never satisfy it");
    }

    private sealed class LegacyHandler(ActionNodeType type) : AZOA.WebAPI.Interfaces.ActionExecution.IActionNodeHandler
    {
        public ActionNodeType NodeType => type;

        public Task<ActionNodeHandlerResult> HandleAsync(
            ActionNodeExecutionContext context, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
