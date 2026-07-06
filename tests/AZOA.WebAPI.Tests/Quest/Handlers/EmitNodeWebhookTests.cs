using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest.Handlers;

/// <summary>
/// final-hardening F3: the GENERIC quest.emit webhook path. Verifies the
/// <see cref="EmitNodeHandler"/> enqueues a best-effort webhook event ONLY when the run
/// carries an acting tenant AND an emitter is wired, that the pure-passthrough output is
/// unchanged either way, and that the <see cref="QuestWebhookEmitter"/> builds a
/// well-formed event and swallows store faults (best-effort — never fails the node).
/// </summary>
public class EmitNodeWebhookTests
{
    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class CapturingEmitter : IQuestWebhookEmitter
    {
        public int Calls;
        public Guid TenantId;
        public string? EventType;
        public Guid RunId, NodeId, QuestId;
        public string? PayloadJson;

        public Task EmitAsync(Guid tenantId, string eventType, Guid runId, Guid nodeId,
            Guid questId, string payloadJson, CancellationToken ct = default)
        {
            Calls++;
            TenantId = tenantId; EventType = eventType; RunId = runId; NodeId = nodeId;
            QuestId = questId; PayloadJson = payloadJson;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutbox : IQuestWebhookOutboxStore
    {
        public QuestWebhookEvent? Enqueued;
        public bool FailEnqueue;

        public Task<AZOAResult<QuestWebhookEvent>> EnqueueAsync(QuestWebhookEvent evt, CancellationToken ct = default)
        {
            if (FailEnqueue)
                return Task.FromResult(new AZOAResult<QuestWebhookEvent> { IsError = true, Message = "boom" });
            Enqueued = evt;
            return Task.FromResult(new AZOAResult<QuestWebhookEvent> { Result = evt, Message = "ok" });
        }

        public Task<AZOAResult<IReadOnlyList<QuestWebhookEvent>>> ListDueAsync(DateTime now, int limit, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<IReadOnlyList<QuestWebhookEvent>> { Result = Array.Empty<QuestWebhookEvent>() });
        public Task<AZOAResult<bool>> MarkDeliveredAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<bool> { Result = true });
        public Task<AZOAResult<bool>> RescheduleAsync(Guid id, int attemptCount, DateTime nextAttemptAt, string lastError, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<bool> { Result = true });
        public Task<AZOAResult<bool>> DeadLetterAsync(Guid id, string lastError, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<bool> { Result = true });
    }

    private sealed class ThrowingOutbox : IQuestWebhookOutboxStore
    {
        public Task<AZOAResult<QuestWebhookEvent>> EnqueueAsync(QuestWebhookEvent evt, CancellationToken ct = default)
            => throw new InvalidOperationException("store exploded");
        public Task<AZOAResult<IReadOnlyList<QuestWebhookEvent>>> ListDueAsync(DateTime now, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AZOAResult<bool>> MarkDeliveredAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AZOAResult<bool>> RescheduleAsync(Guid id, int attemptCount, DateTime nextAttemptAt, string lastError, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AZOAResult<bool>> DeadLetterAsync(Guid id, string lastError, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static QuestNode EmitNodeWith(JsonElement payload, string? eventType = null)
    {
        var cfg = new EmitNodeConfig { Payload = payload, EventType = eventType };
        var cfgJson = JsonSerializer.Serialize(cfg, QuestNodeJson.Options);
        return new QuestNode { Id = Guid.NewGuid(), NodeType = QuestNodeType.Emit, Config = cfgJson };
    }

    private static QuestNodeExecutionContext CtxFor(QuestNode node, Guid? actingTenantId)
    {
        var quest = new QuestEntity { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), Nodes = { node } };
        return new QuestNodeExecutionContext(Guid.NewGuid(), node.Id, quest,
            upstreamExecutions: null, actingTenantId: actingTenantId);
    }

    private static JsonElement Payload(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    // ── handler → emitter wiring ──────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_TenantPresentAndEmitterWired_EnqueuesEvent()
    {
        var emitter = new CapturingEmitter();
        var tenantId = Guid.NewGuid();
        var node = EmitNodeWith(Payload(new { payout = "500" }), eventType: "fractionalization.settled");
        var ctx = CtxFor(node, tenantId);

        var handler = new EmitNodeHandler(emitter);
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        emitter.Calls.Should().Be(1);
        emitter.TenantId.Should().Be(tenantId);
        emitter.EventType.Should().Be("fractionalization.settled");
        emitter.RunId.Should().Be(ctx.RunId);
        emitter.NodeId.Should().Be(node.Id);
        emitter.QuestId.Should().Be(ctx.Quest.Id);

        // The webhook payload IS the node output (round-trips the tenant payload).
        using var doc = JsonDocument.Parse(emitter.PayloadJson!);
        doc.RootElement.GetProperty("payout").GetString().Should().Be("500");
    }

    [Fact]
    public async Task HandleAsync_NoActingTenant_DoesNotEnqueue_ButStillEmitsOutput()
    {
        var emitter = new CapturingEmitter();
        var node = EmitNodeWith(Payload(new { x = 1 }));
        var ctx = CtxFor(node, actingTenantId: null); // user-driven run — no tenant

        var handler = new EmitNodeHandler(emitter);
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        result.Output.Should().NotBeNull();
        emitter.Calls.Should().Be(0); // best-effort webhook is tenant-scoped only
    }

    [Fact]
    public async Task HandleAsync_NoEmitterWired_IsPurePassThrough()
    {
        var node = EmitNodeWith(Payload(new { x = 1 }));
        var ctx = CtxFor(node, actingTenantId: Guid.NewGuid());

        var handler = new EmitNodeHandler(); // zero-arg: pure path preserved
        var result = await handler.HandleAsync(ctx);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Output!);
        doc.RootElement.GetProperty("x").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_DefaultEventName_WhenConfigOmitsIt()
    {
        var emitter = new CapturingEmitter();
        var node = EmitNodeWith(Payload(new { x = 1 })); // no EventType in config
        var ctx = CtxFor(node, Guid.NewGuid());

        await new EmitNodeHandler(emitter).HandleAsync(ctx);

        emitter.EventType.Should().Be("quest.emit");
    }

    // ── emitter behaviour ──────────────────────────────────────────────────────

    [Fact]
    public async Task Emitter_BuildsWellFormedEvent_OnOutbox()
    {
        var outbox = new RecordingOutbox();
        var emitter = new QuestWebhookEmitter(outbox, NullLogger<QuestWebhookEmitter>.Instance);

        var tenantId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var questId = Guid.NewGuid();

        await emitter.EmitAsync(tenantId, "my.event", runId, nodeId, questId, "{\"a\":1}");

        outbox.Enqueued.Should().NotBeNull();
        var evt = outbox.Enqueued!;
        evt.TenantId.Should().Be(tenantId);
        evt.EventType.Should().Be("my.event");
        evt.RunId.Should().Be(runId);
        evt.NodeId.Should().Be(nodeId);
        evt.QuestId.Should().Be(questId);
        evt.PayloadJson.Should().Be("{\"a\":1}");
        evt.Status.Should().Be(QuestWebhookDeliveryStatus.Pending);
        evt.AttemptCount.Should().Be(0);
        evt.IdempotencyId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Emitter_DefaultsBlankEventNameAndPayload()
    {
        var outbox = new RecordingOutbox();
        var emitter = new QuestWebhookEmitter(outbox, NullLogger<QuestWebhookEmitter>.Instance);

        await emitter.EmitAsync(Guid.NewGuid(), "  ", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "");

        outbox.Enqueued!.EventType.Should().Be("quest.emit");
        outbox.Enqueued!.PayloadJson.Should().Be("{}");
    }

    [Fact]
    public async Task Emitter_StoreError_IsSwallowed_NeverThrows()
    {
        var emitter = new QuestWebhookEmitter(
            new RecordingOutbox { FailEnqueue = true }, NullLogger<QuestWebhookEmitter>.Instance);

        var act = async () => await emitter.EmitAsync(
            Guid.NewGuid(), "e", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{}");

        await act.Should().NotThrowAsync(); // best-effort — a failed enqueue is logged, not thrown
    }

    [Fact]
    public async Task Emitter_StoreThrows_IsSwallowed_NeverThrows()
    {
        var emitter = new QuestWebhookEmitter(new ThrowingOutbox(), NullLogger<QuestWebhookEmitter>.Instance);

        var act = async () => await emitter.EmitAsync(
            Guid.NewGuid(), "e", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{}");

        await act.Should().NotThrowAsync(); // a store EXCEPTION must not bubble out of the Emit node
    }
}
