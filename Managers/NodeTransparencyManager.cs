using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Managers;

public sealed class NodeTransparencyManager : INodeTransparencyManager
{
    private const int MaxAuditLimit = 100;
    private const int MaxTreasuryDestinations = 100;
    private const int MaxCursorLength = 2048;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly INodeGovernanceManager _governance;
    private readonly INodeFeeScheduleManager _fees;
    private readonly INodeTransparencyStore _store;
    private readonly NodeTransparencyCursorCodec _cursorCodec;
    private readonly INodeTransparencyHistoryService? _history;

    public NodeTransparencyManager(
        INodeGovernanceManager governance,
        INodeFeeScheduleManager fees,
        INodeTransparencyStore store,
        NodeTransparencyCursorCodec cursorCodec,
        INodeTransparencyHistoryService? history = null)
    {
        _governance = governance ?? throw new ArgumentNullException(nameof(governance));
        _fees = fees ?? throw new ArgumentNullException(nameof(fees));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cursorCodec = cursorCodec ?? throw new ArgumentNullException(nameof(cursorCodec));
        _history = history;
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeTransparencySnapshotResponse>> GetSnapshotAsync(
        CancellationToken ct = default)
    {
        var governanceTask = _governance.GetParametersAsync(ct);
        var feesTask = _fees.GetScheduleAsync(ct);
        var treasuryTask = _store.ListTreasuryDestinationsAsync(MaxTreasuryDestinations + 1, ct);
        await Task.WhenAll(governanceTask, feesTask, treasuryTask);

        var governance = await governanceTask;
        var fees = await feesTask;
        var treasury = await treasuryTask;
        if (governance.IsError
            || governance.Result is null
            || governance.Result.Version < 0
            || fees.IsError
            || fees.Result is null
            || fees.Result.Version < 0
            || treasury.IsError
            || treasury.Result is null
            || treasury.Result.Count > MaxTreasuryDestinations)
        {
            return Unavailable<NodeTransparencySnapshotResponse>();
        }

        var publicTreasury = new List<PublicNodeTreasuryDestinationResponse>(treasury.Result.Count);
        foreach (var destination in treasury.Result
                     .OrderBy(row => row.Chain, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(row => row.Network, StringComparer.OrdinalIgnoreCase))
        {
            var mapped = ToPublic(destination);
            if (mapped is null)
                return Unavailable<NodeTransparencySnapshotResponse>();
            publicTreasury.Add(mapped);
        }

        var snapshot = new NodeTransparencySnapshotResponse
        {
            Governance = ToPublic(governance.Result),
            FeeSchedule = ToPublic(fees.Result),
            TreasuryDestinations = publicTreasury,
            LastUpdatedAt = Latest(
                governance.Result.UpdatedAt,
                fees.Result.UpdatedAt,
                publicTreasury.Select(destination => destination.UpdatedAt)),
            CryptographicHistoryProofAvailable = false,
        };
        snapshot.ContentSha256 = NodeTransparencyContentHash.Compute("snapshot-policy", new
        {
            snapshot.Governance,
            snapshot.FeeSchedule,
            snapshot.TreasuryDestinations,
            snapshot.LastUpdatedAt,
        });

        return Success(snapshot);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<NodeTransparencyPageResponse<PublicNodeGovernanceAuditResponse>>> ListGovernanceAuditAsync(
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default)
        => ListAuditAsync(
            NodeTransparencyAuditKind.Governance,
            limit,
            cursor,
            _store.ListGovernanceAuditAsync,
            row => ToPublic(row),
            row => new NodeTransparencyStoreCursor(row.OccurredAt, row.Id),
            ct);

    /// <inheritdoc/>
    public Task<AZOAResult<NodeTransparencyPageResponse<PublicNodeFeeAuditResponse>>> ListFeeAuditAsync(
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default)
        => ListAuditAsync(
            NodeTransparencyAuditKind.FeeSchedule,
            limit,
            cursor,
            _store.ListFeeAuditAsync,
            ToPublic,
            row => new NodeTransparencyStoreCursor(row.OccurredAt, row.Id),
            ct);

    /// <inheritdoc/>
    public Task<AZOAResult<NodeTransparencyPageResponse<PublicNodeTreasuryAuditResponse>>> ListTreasuryAuditAsync(
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default)
        => ListAuditAsync(
            NodeTransparencyAuditKind.Treasury,
            limit,
            cursor,
            _store.ListTreasuryAuditAsync,
            ToPublic,
            row => new NodeTransparencyStoreCursor(row.OccurredAt, row.Id),
            ct);

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeTransparencyHistoryDocument>> GetAuditHistoryCheckpointAsync(
        CancellationToken ct = default)
    {
        if (_history is null)
            return Unavailable<NodeTransparencyHistoryDocument>();

        var availability = await _history.TryGetAsync(ct);
        return availability.IsAvailable && availability.Document is not null
            ? Success(availability.Document)
            : Unavailable<NodeTransparencyHistoryDocument>();
    }

    private async Task<AZOAResult<NodeTransparencyPageResponse<TPublic>>> ListAuditAsync<TRow, TPublic>(
        NodeTransparencyAuditKind kind,
        int requestedLimit,
        string? encodedCursor,
        Func<int, NodeTransparencyStoreCursor?, CancellationToken, Task<AZOAResult<IReadOnlyList<TRow>>>> load,
        Func<TRow, TPublic?> map,
        Func<TRow, NodeTransparencyStoreCursor> cursorFor,
        CancellationToken ct)
        where TPublic : class
    {
        NodeTransparencyStoreCursor? before = null;
        if (encodedCursor is not null
            && (encodedCursor.Length > MaxCursorLength
                || !_cursorCodec.TryDecode(encodedCursor, kind, out before)))
        {
            return InvalidCursor<NodeTransparencyPageResponse<TPublic>>();
        }

        var limit = Math.Clamp(requestedLimit, 1, MaxAuditLimit);
        var loaded = await load(limit + 1, before, ct);
        if (loaded.IsError || loaded.Result is null)
            return Unavailable<NodeTransparencyPageResponse<TPublic>>();

        var hasMore = loaded.Result.Count > limit;
        var rows = loaded.Result.Take(limit).ToList();
        var items = new List<TPublic>(rows.Count);
        foreach (var row in rows)
        {
            var item = map(row);
            if (item is null)
                return Unavailable<NodeTransparencyPageResponse<TPublic>>();
            items.Add(item);
        }

        string? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            var next = cursorFor(rows[^1]);
            if (next.OccurredAt == default || string.IsNullOrWhiteSpace(next.RecordId))
                return Unavailable<NodeTransparencyPageResponse<TPublic>>();
            nextCursor = _cursorCodec.Encode(kind, next);
        }

        var page = new NodeTransparencyPageResponse<TPublic>
        {
            Items = items,
            NextCursor = nextCursor,
            ContentSha256 = NodeTransparencyContentHash.Compute($"audit-page:{kind}", items),
            CryptographicHistoryProofAvailable = false,
        };
        return Success(page);
    }

    private static PublicNodeGovernanceParametersResponse ToPublic(NodeGovernanceParametersResponse source)
        => new()
        {
            AllowedChains = source.AllowedChains,
            AllowedAssetTypes = source.AllowedAssetTypes,
            Version = source.Version,
            UpdatedAt = source.UpdatedAt,
        };

    private static PublicNodeFeeScheduleResponse ToPublic(NodeFeeScheduleResponse source)
    {
        var result = new PublicNodeFeeScheduleResponse
        {
            Mint = Copy(source.Mint),
            Transfer = Copy(source.Transfer),
            Swap = Copy(source.Swap),
            QuestComplete = Copy(source.QuestComplete),
            FederationPublish = Copy(source.FederationPublish),
            Version = source.Version,
            UpdatedAt = source.UpdatedAt,
        };
        return IsValidFeeSchedule(result)
            ? result
            : InvalidStoredData<PublicNodeFeeScheduleResponse>();
    }

    private static PublicNodeTreasuryDestinationResponse? ToPublic(NodeTreasuryDestination source)
    {
        if (source.Version < 1
            || string.IsNullOrWhiteSpace(source.Chain)
            || string.IsNullOrWhiteSpace(source.Address)
            || !TryParseNetwork(source.Network, out var network))
        {
            return InvalidStoredData<PublicNodeTreasuryDestinationResponse>();
        }

        return new PublicNodeTreasuryDestinationResponse
        {
            Chain = source.Chain,
            Network = network,
            Address = source.Address,
            Version = source.Version,
            UpdatedAt = source.UpdatedAt == default ? null : source.UpdatedAt,
        };
    }

    internal static PublicNodeGovernanceAuditResponse? ToPublic(NodeGovernanceAudit source)
    {
        if (!HasValidAuditIdentity(source.Id, source.OccurredAt)
            || !string.Equals(source.Action, "ParametersUpdated", StringComparison.Ordinal)
            || !IsNextVersion(source.PreviousVersion, source.NewVersion))
        {
            return InvalidStoredData<PublicNodeGovernanceAuditResponse>();
        }

        return new PublicNodeGovernanceAuditResponse
        {
            Action = source.Action,
            PreviousVersion = source.PreviousVersion,
            NewVersion = source.NewVersion,
            PreviousAllowedChains = source.PreviousAllowedChains,
            PreviousAllowedAssetTypes = source.PreviousAllowedAssetTypes,
            AllowedChains = source.AllowedChains,
            AllowedAssetTypes = source.AllowedAssetTypes,
            OccurredAt = source.OccurredAt,
        };
    }

    internal static PublicNodeFeeAuditResponse? ToPublic(NodeFeeAudit source)
    {
        var schedule = Deserialize<PublicNodeFeeScheduleResponse>(source.ScheduleJson);
        var previous = source.PreviousScheduleJson is null
            ? null
            : Deserialize<PublicNodeFeeScheduleResponse>(source.PreviousScheduleJson);
        var expectsPrevious = source.PreviousVersion > 0;
        if (!HasValidAuditIdentity(source.Id, source.OccurredAt)
            || !string.Equals(source.Action, "ScheduleUpdated", StringComparison.Ordinal)
            || !IsNextVersion(source.PreviousVersion, source.NewVersion)
            || (expectsPrevious && source.PreviousScheduleJson is null)
            || schedule is null
            || !IsValidFeeSchedule(schedule)
            || schedule.Version != source.NewVersion
            || (source.PreviousScheduleJson is not null
                && (previous is null
                    || !IsValidFeeSchedule(previous)
                    || previous.Version != source.PreviousVersion)))
        {
            return InvalidStoredData<PublicNodeFeeAuditResponse>();
        }

        return new PublicNodeFeeAuditResponse
        {
            Action = source.Action,
            PreviousVersion = source.PreviousVersion,
            NewVersion = source.NewVersion,
            PreviousSchedule = previous,
            Schedule = schedule,
            OccurredAt = source.OccurredAt,
        };
    }

    internal static PublicNodeTreasuryAuditResponse? ToPublic(NodeTreasuryAudit source)
    {
        if (!HasValidAuditIdentity(source.Id, source.OccurredAt)
            || !string.Equals(source.Action, "DestinationUpdated", StringComparison.Ordinal)
            || !IsNextVersion(source.PreviousVersion, source.NewVersion)
            || !TryParseNetwork(source.Network, out var network))
            return InvalidStoredData<PublicNodeTreasuryAuditResponse>();

        var destination = Deserialize<PublicNodeTreasuryDestinationResponse>(source.DestinationJson);
        var previous = source.PreviousDestinationJson is null
            ? null
            : Deserialize<PublicNodeTreasuryDestinationResponse>(source.PreviousDestinationJson);
        var expectsPrevious = source.PreviousVersion > 0;
        if ((expectsPrevious && source.PreviousDestinationJson is null)
            || destination is null
            || !IsValidDestination(destination)
            || destination.Version != source.NewVersion
            || !string.Equals(destination.Chain, source.Chain, StringComparison.OrdinalIgnoreCase)
            || destination.Network != network
            || (source.PreviousDestinationJson is not null
                && (previous is null
                    || !IsValidDestination(previous)
                    || previous.Version != source.PreviousVersion
                    || !string.Equals(previous.Chain, source.Chain, StringComparison.OrdinalIgnoreCase)
                    || previous.Network != network)))
        {
            return InvalidStoredData<PublicNodeTreasuryAuditResponse>();
        }

        return new PublicNodeTreasuryAuditResponse
        {
            Action = source.Action,
            Chain = source.Chain,
            Network = network,
            PreviousVersion = source.PreviousVersion,
            NewVersion = source.NewVersion,
            PreviousDestination = previous,
            Destination = destination,
            OccurredAt = source.OccurredAt,
        };
    }

    private static T? Deserialize<T>(string json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return InvalidStoredData<T>();

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "A stored node-transparency audit snapshot is not valid typed JSON.",
                ex);
        }
    }

    private static NodeFeeScheduleEntryResponse Copy(NodeFeeScheduleEntryResponse source)
        => new() { FlatBaseUnits = source.FlatBaseUnits, Bps = source.Bps };

    private static bool IsValidFeeSchedule(PublicNodeFeeScheduleResponse schedule)
        => schedule.Version >= 0
            && IsValidFeeEntry(schedule.Mint)
            && IsValidFeeEntry(schedule.Transfer)
            && IsValidFeeEntry(schedule.Swap)
            && IsValidFeeEntry(schedule.QuestComplete)
            && IsValidFeeEntry(schedule.FederationPublish);

    private static bool IsValidFeeEntry(NodeFeeScheduleEntryResponse? entry)
        => entry is not null
            && ulong.TryParse(
                entry.FlatBaseUnits,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _)
            && entry.Bps is >= 0 and <= 10_000;

    private static bool IsValidDestination(PublicNodeTreasuryDestinationResponse destination)
        => destination.Version >= 1
            && !string.IsNullOrWhiteSpace(destination.Chain)
            && !string.IsNullOrWhiteSpace(destination.Address)
            && Enum.IsDefined(destination.Network);

    private static bool TryParseNetwork(string raw, out ChainNetwork network)
        => Enum.TryParse(raw, true, out network) && Enum.IsDefined(network);

    private static bool IsNextVersion(long previous, long next)
        => previous >= 0 && previous < long.MaxValue && next == previous + 1;

    private static bool HasValidAuditIdentity(string id, DateTimeOffset occurredAt)
        => !string.IsNullOrWhiteSpace(id) && occurredAt != default;

    private static T InvalidStoredData<T>()
        => throw new InvalidDataException("Stored node-transparency data failed structural validation.");

    private static DateTimeOffset? Latest(
        DateTimeOffset? governance,
        DateTimeOffset? fees,
        IEnumerable<DateTimeOffset?> treasury)
    {
        var candidates = treasury
            .Append(governance)
            .Append(fees)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        return candidates.Count == 0 ? null : candidates.Max();
    }

    private static AZOAResult<T> Success<T>(T value)
        => new() { Result = value, Message = "Success" };

    private static AZOAResult<T> InvalidCursor<T>()
        => new() { IsError = true, Message = NodeTransparencyMessages.InvalidCursor };

    private static AZOAResult<T> Unavailable<T>()
        => new() { IsError = true, Message = NodeTransparencyMessages.Unavailable };
}
