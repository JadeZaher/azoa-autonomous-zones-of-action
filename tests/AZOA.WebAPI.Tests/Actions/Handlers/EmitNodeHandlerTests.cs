using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

/// <summary>
/// Unit tests for <see cref="EmitNodeHandler"/>. Verifies the pure
/// pass-through behaviour: the tenant payload reaches <c>Output</c>
/// unchanged, no manager is involved, and <c>RequiresChainCapability</c>
/// is false (Tier-1).
/// </summary>
public class EmitNodeHandlerTests
{
    // ── helpers matching ActionNodeHandlerTests conventions ──────────────

    private static ActionNode NodeWith(ActionNodeType type, string config) =>
        new() { Id = Guid.NewGuid(), NodeType = type, Config = config };

    private static ActionNodeExecutionContext CtxFor(ActionNode node, Guid userId) =>
        new(Guid.NewGuid(), node.Id,
            new ActionEntity { Id = Guid.NewGuid(), UserId = userId, Nodes = { node } },
            actingUserId: userId);

    // ── metadata / SPI assertions ────────────────────────────────────────

    [Fact]
    public void EmitNodeHandler_NodeType_IsEmit()
    {
        var handler = new EmitNodeHandler();
        handler.NodeType.Should().Be(ActionNodeType.Emit);
    }

    [Fact]
    public void EmitNodeHandler_RequiresChainCapability_IsFalse()
    {
        IActionNodeHandler handler = new EmitNodeHandler();
        handler.RequiresChainCapability.Should().BeFalse();
    }

    [Fact]
    public void EmitNodeHandler_IsConstructableWithNoArguments()
    {
        // final-hardening F3: the handler now takes an OPTIONAL IActionWebhookEmitter
        // (nullable, default null) so it can enqueue a best-effort action.emit webhook.
        // The pure-passthrough contract is preserved — it must still be constructable
        // with zero arguments, and the single constructor's only parameter is optional.
        var ctors = typeof(EmitNodeHandler).GetConstructors();
        ctors.Should().ContainSingle();
        var parameters = ctors[0].GetParameters();
        parameters.Should().ContainSingle();
        parameters[0].IsOptional.Should().BeTrue();

        // Zero-arg construction still works (the pure path — no emitter, no manager).
        var act = () => new EmitNodeHandler();
        act.Should().NotThrow();
    }

    // ── payload round-trip ───────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_TenantPayload_RoundTripsToOutputUnchanged()
    {
        // Arrange — a tenant-shaped ArdaNova payout payload
        var payloadObj = new { freelancerPayout = "500", currency = "USD" };
        var payloadJson = JsonSerializer.Serialize(payloadObj);
        var cfg = new EmitNodeConfig
        {
            Payload = JsonDocument.Parse(payloadJson).RootElement
        };
        var cfgJson = JsonSerializer.Serialize(cfg, ActionNodeJson.Options);

        var node = NodeWith(ActionNodeType.Emit, cfgJson);
        var ctx = CtxFor(node, Guid.NewGuid());

        var handler = new EmitNodeHandler();

        // Act
        var result = await handler.HandleAsync(ctx);

        // Assert — success, output matches input payload exactly
        result.IsError.Should().BeFalse();
        result.Output.Should().NotBeNull();

        using var doc = JsonDocument.Parse(result.Output!);
        doc.RootElement.GetProperty("freelancerPayout").GetString().Should().Be("500");
        doc.RootElement.GetProperty("currency").GetString().Should().Be("USD");
    }

    [Fact]
    public async Task HandleAsync_UndefinedPayload_EmitsEmptyObject()
    {
        // Arrange — the wire form of "no payload": config JSON with no
        // "payload" key. Deserializing leaves EmitNodeConfig.Payload at its
        // default (ValueKind == Undefined). (A default JsonElement cannot be
        // re-serialized, so the config is supplied as its JSON wire form
        // rather than by serializing a default EmitNodeConfig.)
        var node = NodeWith(ActionNodeType.Emit, """{ "notPayload": 1 }""");
        var ctx = CtxFor(node, Guid.NewGuid());

        var handler = new EmitNodeHandler();

        // Act
        var result = await handler.HandleAsync(ctx);

        // Assert — graceful empty object, no exception
        result.IsError.Should().BeFalse();
        result.Output.Should().Be("{}");
    }

    [Fact]
    public async Task HandleAsync_NullConfig_EmitsEmptyObject()
    {
        // Arrange — node config is an empty JSON object (no Payload key)
        var node = NodeWith(ActionNodeType.Emit, "{}");
        var ctx = CtxFor(node, Guid.NewGuid());

        var handler = new EmitNodeHandler();

        // Act
        var result = await handler.HandleAsync(ctx);

        // Assert — graceful, no throw
        result.IsError.Should().BeFalse();
        result.Output.Should().Be("{}");
    }
}
