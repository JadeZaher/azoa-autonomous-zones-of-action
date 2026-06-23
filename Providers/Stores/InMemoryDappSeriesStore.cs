using System.Collections.Concurrent;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores;

/// <summary>
/// Thread-safe in-memory <see cref="IDappSeriesStore"/>. Singleton-scoped via
/// <c>Program.cs</c>. The Surreal-backed adapter lands with
/// <c>surrealdb-migration</c> wave-2 alongside the rest of the value-table
/// adapters; until then this is the default and acceptable because
/// dapp-composition is pre-launch greenfield (per the
/// <c>greenfield-prelaunch-no-compat</c> project memory).
/// </summary>
public sealed class InMemoryDappSeriesStore : IDappSeriesStore
{
    private readonly ConcurrentDictionary<Guid, DappSeries> _series = new();
    private readonly ConcurrentDictionary<Guid, DappSeriesQuest> _entries = new();

    public Task<AZOAResult<DappSeries>> GetSeriesAsync(Guid id, CancellationToken ct = default)
    {
        if (_series.TryGetValue(id, out var series))
            return Task.FromResult(new AZOAResult<DappSeries> { Result = series, Message = "Success" });

        return Task.FromResult(new AZOAResult<DappSeries>
        {
            IsError = true,
            Message = $"DappSeries {id} not found.",
        });
    }

    public Task<AZOAResult<IEnumerable<DappSeries>>> GetSeriesByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        var matches = _series.Values.Where(s => s.AvatarIdGuid == avatarId).ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<DappSeries>>
        {
            Result = matches,
            Message = "Success",
        });
    }

    public Task<AZOAResult<DappSeries>> UpsertSeriesAsync(DappSeries series, CancellationToken ct = default)
    {
        // Defensive: malformed Ids would throw inside the IdGuid accessor;
        // catch and surface as a regular store-level error rather than an
        // unhandled exception out of an InMemory call.
        Guid id;
        try { id = series.IdGuid; }
        catch (FormatException) { return Task.FromResult(StoreFail<DappSeries>("DappSeries.Id must be a Guid('N') hex string.")); }

        _series[id] = series;
        return Task.FromResult(new AZOAResult<DappSeries> { Result = series, Message = "Upserted." });
    }

    public Task<AZOAResult<bool>> DeleteSeriesAsync(Guid id, CancellationToken ct = default)
    {
        var seriesRemoved = _series.TryRemove(id, out _);
        // Cascade-delete the ordered entries belonging to this series.
        foreach (var pair in _entries.ToArray())
        {
            if (pair.Value.DappSeriesIdGuid == id)
                _entries.TryRemove(pair.Key, out _);
        }
        return Task.FromResult(new AZOAResult<bool>
        {
            Result = seriesRemoved,
            Message = seriesRemoved ? "Deleted." : $"DappSeries {id} not found.",
            IsError = !seriesRemoved,
        });
    }

    public Task<AZOAResult<IEnumerable<DappSeriesQuest>>> GetQuestsBySeriesAsync(Guid seriesId, CancellationToken ct = default)
    {
        var matches = _entries.Values
            .Where(e => e.DappSeriesIdGuid == seriesId)
            .OrderBy(e => e.Order)
            .ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<DappSeriesQuest>>
        {
            Result = matches,
            Message = "Success",
        });
    }

    public Task<AZOAResult<DappSeriesQuest>> UpsertSeriesQuestAsync(DappSeriesQuest entry, CancellationToken ct = default)
    {
        Guid id;
        try { id = entry.IdGuid; }
        catch (FormatException) { return Task.FromResult(StoreFail<DappSeriesQuest>("DappSeriesQuest.Id must be a Guid('N') hex string.")); }

        _entries[id] = entry;
        return Task.FromResult(new AZOAResult<DappSeriesQuest> { Result = entry, Message = "Upserted." });
    }

    public Task<AZOAResult<bool>> DeleteSeriesQuestAsync(Guid seriesId, Guid questId, CancellationToken ct = default)
    {
        var match = _entries.Values.FirstOrDefault(e =>
            e.DappSeriesIdGuid == seriesId && e.QuestIdGuid == questId);
        if (match is null)
            return Task.FromResult(StoreFail<bool>($"No DappSeriesQuest entry for series {seriesId} + quest {questId}."));

        var removed = _entries.TryRemove(match.IdGuid, out _);
        return Task.FromResult(new AZOAResult<bool>
        {
            Result = removed,
            Message = removed ? "Deleted." : "Concurrent removal.",
            IsError = !removed,
        });
    }

    private static AZOAResult<T> StoreFail<T>(string message) => new() { IsError = true, Message = message };
}
