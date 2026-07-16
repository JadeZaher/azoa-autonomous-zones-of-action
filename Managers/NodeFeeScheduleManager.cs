using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;
using SurrealForge.Client;

namespace AZOA.WebAPI.Managers;

public sealed class NodeFeeScheduleManager : INodeFeeScheduleManager
{
    private const int MaxAuditLimit = 100;
    private const long MaxBps = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly INodeFeeScheduleStore _store;

    public NodeFeeScheduleManager(INodeFeeScheduleStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeScheduleResponse>> GetScheduleAsync(CancellationToken ct = default)
    {
        var current = await _store.GetScheduleAsync(ct);
        if (current.IsError)
            return AZOAResult<NodeFeeScheduleResponse>.Failure(current.Message);

        return new AZOAResult<NodeFeeScheduleResponse>
        {
            Result = ToResponse(current.Result ?? DefaultSchedule()),
            Message = "Success",
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeScheduleResponse>> UpdateScheduleAsync(
        NodeFeeScheduleUpdateRequest request,
        Guid actorAvatarId,
        CancellationToken ct = default)
    {
        if (request is null)
            return AZOAResult<NodeFeeScheduleResponse>.Failure("Node fee schedule update is required.");
        if (actorAvatarId == Guid.Empty)
            return AZOAResult<NodeFeeScheduleResponse>.Failure("Actor avatar id is required.");

        var current = await _store.GetScheduleAsync(ct);
        if (current.IsError)
            return AZOAResult<NodeFeeScheduleResponse>.Failure($"Node fee schedule unavailable: {current.Message}");

        var previous = current.Result;
        var baseSchedule = previous ?? DefaultSchedule();
        var errors = new List<string>();
        if (request.ExpectedVersion < 0)
            errors.Add("ExpectedVersion cannot be negative.");
        var now = DateTimeOffset.UtcNow;
        var actorLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(actorAvatarId)) ?? string.Empty;

        var next = new NodeFeeSchedule
        {
            Id = NodeFeeSchedule.LocalId,
            MintFlatBaseUnits = ResolveFlat("Mint.FlatBaseUnits", request.Mint, baseSchedule.MintFlatBaseUnits, errors),
            MintBps = ResolveBps("Mint.Bps", request.Mint, baseSchedule.MintBps, errors),
            TransferFlatBaseUnits = ResolveFlat("Transfer.FlatBaseUnits", request.Transfer, baseSchedule.TransferFlatBaseUnits, errors),
            TransferBps = ResolveBps("Transfer.Bps", request.Transfer, baseSchedule.TransferBps, errors),
            SwapFlatBaseUnits = ResolveFlat("Swap.FlatBaseUnits", request.Swap, baseSchedule.SwapFlatBaseUnits, errors),
            SwapBps = ResolveBps("Swap.Bps", request.Swap, baseSchedule.SwapBps, errors),
            QuestCompleteFlatBaseUnits = ResolveFlat("QuestComplete.FlatBaseUnits", request.QuestComplete, baseSchedule.QuestCompleteFlatBaseUnits, errors),
            QuestCompleteBps = ResolveBps("QuestComplete.Bps", request.QuestComplete, baseSchedule.QuestCompleteBps, errors),
            FederationPublishFlatBaseUnits = ResolveFlat("FederationPublish.FlatBaseUnits", request.FederationPublish, baseSchedule.FederationPublishFlatBaseUnits, errors),
            FederationPublishBps = ResolveBps("FederationPublish.Bps", request.FederationPublish, baseSchedule.FederationPublishBps, errors),
            Version = (previous?.Version ?? 0) + 1,
            UpdatedByAvatarId = actorLink,
            CreatedAt = previous?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        if (errors.Count > 0)
            return AZOAResult<NodeFeeScheduleResponse>.Failure(string.Join(" ", errors));

        if (SameFeeValues(next, baseSchedule))
        {
            return new AZOAResult<NodeFeeScheduleResponse>
            {
                Result = ToResponse(baseSchedule),
                Message = "No fee schedule changes were required.",
            };
        }

        var currentVersion = previous?.Version ?? 0;
        if (request.ExpectedVersion.HasValue && request.ExpectedVersion.Value != currentVersion)
        {
            return AZOAResult<NodeFeeScheduleResponse>.Failure(
                $"Node fee schedule version conflict: expected {request.ExpectedVersion.Value}, current {currentVersion}.");
        }

        var audit = new NodeFeeAudit
        {
            Id = SurrealId.ToSurrealId(Guid.NewGuid()),
            Action = "ScheduleUpdated",
            ActorAvatarId = actorLink,
            PreviousVersion = previous?.Version ?? 0,
            NewVersion = next.Version,
            PreviousScheduleJson = previous is null ? null : JsonSerializer.Serialize(ToResponse(previous), JsonOptions),
            ScheduleJson = JsonSerializer.Serialize(ToResponse(next), JsonOptions),
            Detail = "Node fee schedule updated.",
            OccurredAt = now,
        };

        var saved = await _store.UpdateScheduleWithAuditAsync(next, audit, previous?.Version, ct);
        if (saved.IsError || saved.Result is null)
            return AZOAResult<NodeFeeScheduleResponse>.Failure(saved.Message);

        return new AZOAResult<NodeFeeScheduleResponse>
        {
            Result = ToResponse(saved.Result),
            Message = "Saved.",
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<NodeFeeAuditResponse>>> ListAuditAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit, 1, MaxAuditLimit);
        var result = await _store.ListAuditAsync(clamped, ct);
        if (result.IsError || result.Result is null)
            return AZOAResult<IEnumerable<NodeFeeAuditResponse>>.Failure(result.Message);

        return new AZOAResult<IEnumerable<NodeFeeAuditResponse>>
        {
            Result = result.Result.Select(ToResponse).ToList(),
            Message = "Success",
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeQuoteResponse>> QuoteAsync(
        NodeFeeOperation operation,
        ulong grossAmount,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(operation))
            return AZOAResult<NodeFeeQuoteResponse>.Failure("Node fee operation is not supported.");
        if (grossAmount == 0)
            return AZOAResult<NodeFeeQuoteResponse>.Failure("Gross amount must be a positive integer base-unit amount.");

        var current = await _store.GetScheduleAsync(ct);
        if (current.IsError)
            return AZOAResult<NodeFeeQuoteResponse>.Failure($"Node fee schedule unavailable: {current.Message}");

        var schedule = current.Result ?? DefaultSchedule();
        var entry = EntryFor(schedule, operation);
        if (!ulong.TryParse(entry.FlatBaseUnits, NumberStyles.None, CultureInfo.InvariantCulture, out var flat))
            return AZOAResult<NodeFeeQuoteResponse>.Failure($"Node fee schedule has invalid flat amount for {operation}.");

        var fee128 = (UInt128)flat + (((UInt128)grossAmount * (UInt128)entry.Bps) / MaxBps);
        if (fee128 > ulong.MaxValue)
            return AZOAResult<NodeFeeQuoteResponse>.Failure($"Node fee for {operation} exceeds the unsigned 64-bit provider amount range.");

        var fee = (ulong)fee128;
        if (fee >= grossAmount)
            return AZOAResult<NodeFeeQuoteResponse>.Failure($"Node fee for {operation} must be less than the gross amount.");

        var net = grossAmount - fee;
        return new AZOAResult<NodeFeeQuoteResponse>
        {
            Result = new NodeFeeQuoteResponse
            {
                Operation = operation,
                GrossAmount = grossAmount.ToString(CultureInfo.InvariantCulture),
                FeeAmount = fee.ToString(CultureInfo.InvariantCulture),
                NetAmount = net.ToString(CultureInfo.InvariantCulture),
                FlatBaseUnits = entry.FlatBaseUnits,
                Bps = entry.Bps,
                ScheduleVersion = schedule.Version,
            },
            Message = "Success",
        };
    }

    private static NodeFeeSchedule DefaultSchedule() => new()
    {
        Id = NodeFeeSchedule.LocalId,
        Version = 0,
    };

    private static string ResolveFlat(
        string fieldName,
        NodeFeeScheduleEntryRequest? request,
        string current,
        List<string> errors)
    {
        var value = request?.FlatBaseUnits is null ? current : request.FlatBaseUnits.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{fieldName} is required.");
            return "0";
        }

        if (!value.All(char.IsDigit))
        {
            errors.Add($"{fieldName} must be a non-negative integer base-unit string.");
            return value;
        }

        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            errors.Add($"{fieldName} must fit in an unsigned 64-bit range, max {ulong.MaxValue}.");

        return value;
    }

    private static long ResolveBps(
        string fieldName,
        NodeFeeScheduleEntryRequest? request,
        long current,
        List<string> errors)
    {
        var value = request?.Bps ?? current;
        if (value is < 0 or > MaxBps)
            errors.Add($"{fieldName} must be between 0 and {MaxBps}.");
        return value;
    }

    private static bool SameFeeValues(NodeFeeSchedule left, NodeFeeSchedule right)
        => left.MintFlatBaseUnits == right.MintFlatBaseUnits
           && left.MintBps == right.MintBps
           && left.TransferFlatBaseUnits == right.TransferFlatBaseUnits
           && left.TransferBps == right.TransferBps
           && left.SwapFlatBaseUnits == right.SwapFlatBaseUnits
           && left.SwapBps == right.SwapBps
           && left.QuestCompleteFlatBaseUnits == right.QuestCompleteFlatBaseUnits
           && left.QuestCompleteBps == right.QuestCompleteBps
           && left.FederationPublishFlatBaseUnits == right.FederationPublishFlatBaseUnits
           && left.FederationPublishBps == right.FederationPublishBps;

    private static NodeFeeScheduleEntryResponse EntryFor(NodeFeeSchedule schedule, NodeFeeOperation operation)
        => operation switch
        {
            NodeFeeOperation.Mint => new NodeFeeScheduleEntryResponse
            {
                FlatBaseUnits = schedule.MintFlatBaseUnits,
                Bps = schedule.MintBps,
            },
            NodeFeeOperation.Transfer => new NodeFeeScheduleEntryResponse
            {
                FlatBaseUnits = schedule.TransferFlatBaseUnits,
                Bps = schedule.TransferBps,
            },
            NodeFeeOperation.Swap => new NodeFeeScheduleEntryResponse
            {
                FlatBaseUnits = schedule.SwapFlatBaseUnits,
                Bps = schedule.SwapBps,
            },
            NodeFeeOperation.QuestComplete => new NodeFeeScheduleEntryResponse
            {
                FlatBaseUnits = schedule.QuestCompleteFlatBaseUnits,
                Bps = schedule.QuestCompleteBps,
            },
            NodeFeeOperation.FederationPublish => new NodeFeeScheduleEntryResponse
            {
                FlatBaseUnits = schedule.FederationPublishFlatBaseUnits,
                Bps = schedule.FederationPublishBps,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    private static NodeFeeScheduleResponse ToResponse(NodeFeeSchedule row) => new()
    {
        Mint = EntryFor(row, NodeFeeOperation.Mint),
        Transfer = EntryFor(row, NodeFeeOperation.Transfer),
        Swap = EntryFor(row, NodeFeeOperation.Swap),
        QuestComplete = EntryFor(row, NodeFeeOperation.QuestComplete),
        FederationPublish = EntryFor(row, NodeFeeOperation.FederationPublish),
        Version = row.Version,
        UpdatedByAvatarId = SurrealRecordGuid.ParseOptional(row.UpdatedByAvatarId),
        CreatedAt = row.CreatedAt == default ? null : row.CreatedAt,
        UpdatedAt = row.UpdatedAt == default ? null : row.UpdatedAt,
    };

    private static NodeFeeAuditResponse ToResponse(NodeFeeAudit row) => new()
    {
        Id = SurrealRecordGuid.Parse(row.Id),
        Action = row.Action,
        ActorAvatarId = SurrealRecordGuid.ParseOptional(row.ActorAvatarId) ?? Guid.Empty,
        PreviousVersion = row.PreviousVersion,
        NewVersion = row.NewVersion,
        PreviousScheduleJson = row.PreviousScheduleJson,
        ScheduleJson = row.ScheduleJson,
        Detail = row.Detail,
        OccurredAt = row.OccurredAt,
    };

}
