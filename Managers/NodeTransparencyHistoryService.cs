using System.Text.Json;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Conformance;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Managers;

/// <summary>Builds a signed checkpoint over the full bounded set of redacted governance audit records.</summary>
public sealed class NodeTransparencyHistoryService : INodeTransparencyHistoryService
{
    private readonly INodeTransparencyStore _store;
    private readonly INodeIdentityKeyService _identityKeys;
    private readonly NodeTransparencyHistoryCheckpointStore _checkpoints;
    private readonly IOptions<NodeTransparencyHistoryOptions> _historyOptions;
    private readonly IOptions<NodeConformanceOptions> _identityOptions;
    private readonly TimeProvider _clock;

    public NodeTransparencyHistoryService(
        INodeTransparencyStore store,
        INodeIdentityKeyService identityKeys,
        NodeTransparencyHistoryCheckpointStore checkpoints,
        IOptions<NodeTransparencyHistoryOptions> historyOptions,
        IOptions<NodeConformanceOptions> identityOptions,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _identityKeys = identityKeys ?? throw new ArgumentNullException(nameof(identityKeys));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _historyOptions = historyOptions ?? throw new ArgumentNullException(nameof(historyOptions));
        _identityOptions = identityOptions ?? throw new ArgumentNullException(nameof(identityOptions));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<NodeTransparencyHistoryAvailability> TryGetAsync(CancellationToken ct = default)
    {
        var history = _historyOptions.Value;
        var identity = _identityOptions.Value;
        if (!history.Enabled
            || history.MaxAuditEntries is < 1 or > 512
            || !identity.Enabled
            || string.IsNullOrWhiteSpace(identity.NodeId)
            || identity.NodeId.Trim().Length > 128
            || string.IsNullOrWhiteSpace(identity.KeyStoragePath))
        {
            return NodeTransparencyHistoryAvailability.Unavailable;
        }

        var limit = history.MaxAuditEntries + 1;
        var governanceTask = _store.ListGovernanceAuditAsync(limit, null, ct);
        var feesTask = _store.ListFeeAuditAsync(limit, null, ct);
        var treasuryTask = _store.ListTreasuryAuditAsync(limit, null, ct);
        await Task.WhenAll(governanceTask, feesTask, treasuryTask);

        var governance = await governanceTask;
        var fees = await feesTask;
        var treasury = await treasuryTask;
        if (governance.IsError || governance.Result is null || governance.Result.Count > history.MaxAuditEntries
            || fees.IsError || fees.Result is null || fees.Result.Count > history.MaxAuditEntries
            || treasury.IsError || treasury.Result is null || treasury.Result.Count > history.MaxAuditEntries
            || governance.Result.Count + fees.Result.Count + treasury.Result.Count > history.MaxAuditEntries)
        {
            return NodeTransparencyHistoryAvailability.Unavailable;
        }

        var entries = new List<NodeTransparencyHistoryEntry>(
            governance.Result.Count + fees.Result.Count + treasury.Result.Count);
        if (!TryAdd(entries, "governance", governance.Result, NodeTransparencyManager.ToPublic)
            || !TryAdd(entries, "fee-schedule", fees.Result, NodeTransparencyManager.ToPublic)
            || !TryAdd(entries, "treasury", treasury.Result, NodeTransparencyManager.ToPublic))
        {
            return NodeTransparencyHistoryAvailability.Unavailable;
        }

        entries.Sort(NodeTransparencyHistoryVerifier.Compare);
        var head = NodeTransparencyHistoryCanonicalizer.ComputeHead(entries);
        var previous = _checkpoints.Get();
        if (!IsExactExtension(previous, entries, head, identity.NodeId.Trim()))
            return NodeTransparencyHistoryAvailability.Unavailable;

        var checkpoint = previous is not null
            && previous.AuditEventCount == entries.Count
            && string.Equals(previous.HeadSha256, head, StringComparison.Ordinal)
            ? previous
            : SignCheckpoint(entries.Count, head, identity.NodeId.Trim());
        if (!ReferenceEquals(checkpoint, previous))
            _checkpoints.Save(checkpoint);

        var document = new NodeTransparencyHistoryDocument(checkpoint, entries);
        return NodeTransparencyHistoryVerifier.TryVerify(document, out _)
            ? new NodeTransparencyHistoryAvailability(true, document)
            : NodeTransparencyHistoryAvailability.Unavailable;
    }

    private NodeTransparencyHistoryCheckpoint SignCheckpoint(int count, string head, string nodeId)
    {
        using var key = _identityKeys.GetCurrent();
        var unsigned = new NodeTransparencyHistoryCheckpoint(
            NodeTransparencyHistoryCanonicalizer.SchemaVersion,
            key.Descriptor with { NodeId = nodeId },
            _clock.GetUtcNow(),
            count,
            head,
            string.Empty);
        return unsigned with
        {
            SignatureBase64 = Convert.ToBase64String(key.Sign(
                NodeTransparencyHistoryCanonicalizer.CheckpointSigningBytes(unsigned))),
        };
    }

    private static bool IsExactExtension(
        NodeTransparencyHistoryCheckpoint? previous,
        IReadOnlyList<NodeTransparencyHistoryEntry> entries,
        string head,
        string nodeId)
    {
        if (previous is null)
            return true;
        if (previous.SchemaVersion != NodeTransparencyHistoryCanonicalizer.SchemaVersion
            || previous.AuditEventCount < 0
            || previous.AuditEventCount > entries.Count
            || !string.Equals(previous.Descriptor.NodeId, nodeId, StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = entries.Take(previous.AuditEventCount).ToArray();
        return NodeTransparencyHistoryVerifier.TryVerify(
                new NodeTransparencyHistoryDocument(previous, prefix),
                out _)
            && string.Equals(previous.HeadSha256,
                NodeTransparencyHistoryCanonicalizer.ComputeHead(prefix),
                StringComparison.Ordinal)
            && (previous.AuditEventCount != entries.Count
                || string.Equals(previous.HeadSha256, head, StringComparison.Ordinal));
    }

    private static bool TryAdd<TSource, TPublic>(
        ICollection<NodeTransparencyHistoryEntry> destination,
        string kind,
        IEnumerable<TSource> source,
        Func<TSource, TPublic?> project)
        where TPublic : class
    {
        foreach (var row in source)
        {
            var value = project(row);
            if (value is null)
                return false;
            var occurredAt = row switch
            {
                NodeGovernanceAudit audit => audit.OccurredAt,
                NodeFeeAudit audit => audit.OccurredAt,
                NodeTreasuryAudit audit => audit.OccurredAt,
                _ => default,
            };
            if (occurredAt == default)
                return false;

            var payload = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            destination.Add(new NodeTransparencyHistoryEntry(
                kind,
                occurredAt,
                payload,
                NodeTransparencyHistoryCanonicalizer.ComputeEntrySha256(kind, occurredAt, payload)));
        }
        return true;
    }
}
