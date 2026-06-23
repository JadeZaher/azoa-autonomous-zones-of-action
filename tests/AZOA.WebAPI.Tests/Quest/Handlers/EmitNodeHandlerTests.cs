using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest.Handlers;

/// <summary>
/// Unit tests for <see cref="EmitNodeHandler"/>. Verifies the pure
/// pass-through behaviour: the tenant payload reaches <c>Output</c>
/// unchanged, no manager is involved, and <c>RequiresChainCapability</c>
/// is false (Tier-1).
/// </summary>
public class EmitNodeHandlerTests
{
    // ── helpers matching QuestNodeHandlerTests conventions ──────────────

    private static QuestNode NodeWith(QuestNodeType type, string config) =>
        new() { Id = Guid.NewGuid(), NodeType = type, Config = config };

    private static QuestNodeExecutionContext CtxFor(QuestNode node, Guid avatarId) =>
        new(Guid.NewGuid(), node.Id,
            new QuestEntity { Id = Guid.NewGuid(), AvatarId = avatarId, Nodes = { node } });

    // ── metadata / SPI assertions ────────────────────────────────────────

    [Fact]
    public void EmitNodeHandler_NodeType_IsEmit()
    {
        var handler = new EmitNodeHandler();
        handler.NodeType.Should().Be(QuestNodeType.Emit);
    }

    [Fact]
    public void EmitNodeHandler_RequiresChainCapability_IsFalse()
    {
        IQuestNodeHandler handler = new EmitNodeHandler();
        handler.RequiresChainCapability.Should().BeFalse();
    }

    [Fact]
    public void EmitNodeHandler_HasNoManagerConstructorParameter()
    {
        // The handler must be constructable with zero arguments (no manager DI).
        var ctor = typeof(EmitNodeHandler).GetConstructors();
        ctor.Should().ContainSingle();
        ctor[0].GetParameters().Should().BeEmpty();
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
        var cfgJson = JsonSerializer.Serialize(cfg, QuestNodeJson.Options);

        var node = NodeWith(QuestNodeType.Emit, cfgJson);
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
        var node = NodeWith(QuestNodeType.Emit, """{ "notPayload": 1 }""");
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
        var node = NodeWith(QuestNodeType.Emit, "{}");
        var ctx = CtxFor(node, Guid.NewGuid());

        var handler = new EmitNodeHandler();

        // Act
        var result = await handler.HandleAsync(ctx);

        // Assert — graceful, no throw
        result.IsError.Should().BeFalse();
        result.Output.Should().Be("{}");
    }
}
