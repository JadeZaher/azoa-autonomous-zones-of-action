using System.Collections.Concurrent;
using OASIS.WebAPI.Generated.SurrealDb;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Stores;

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

    public Task<OASISResult<DappSeries>> GetSeriesAsync(Guid id, CancellationToken ct = default)
    {
        if (_series.TryGetValue(id, out var series))
            return Task.FromResult(new OASISResult<DappSeries> { Result = series, Message = "Success" });

        return Task.FromResult(new OASISResult<DappSeries>
        {
            IsError = true,
            Message = $"DappSeries {id} not found.",
        });
    }

    public Task<OASISResult<IEnumerable<DappSeries>>> GetSeriesByAvatarAsync(Guid avatarId, CancellationToken ct = default)
    {
        var avatarKey = avatarId.ToString("N");
        var matches = _series.Values.Where(s => s.AvatarId == avatarKey).ToList();
        return Task.FromResult(new OASISResult<IEnumerable<DappSeries>>
        {
            Result = matches,
            Message = "Success",
        });
    }

    public Task<OASISResult<DappSeries>> UpsertSeriesAsync(DappSeries series, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(series.Id, "N", out var id))
            return Task.FromResult(new OASISResult<DappSeries>
            {
                IsError = true,
                Message = "DappSeries.Id must be a Guid('N') hex string.",
            });

        _series[id] = series;
        return Task.FromResult(new OASISResult<DappSeries> { Result = series, Message = "Upserted." });
    }

    public Task<OASISResult<bool>> DeleteSeriesAsync(Guid id, CancellationToken ct = default)
    {
        var seriesRemoved = _series.TryRemove(id, out _);
        // Cascade-delete the ordered entries belonging to this series.
        foreach (var pair in _entries.ToArray())
        {
            if (Guid.TryParseExact(pair.Value.DappSeriesId, "N", out var entrySeriesId)
                && entrySeriesId == id)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
        return Task.FromResult(new OASISResult<bool>
        {
            Result = seriesRemoved,
            Message = seriesRemoved ? "Deleted." : $"DappSeries {id} not found.",
            IsError = !seriesRemoved,
        });
    }

    public Task<OASISResult<IEnumerable<DappSeriesQuest>>> GetQuestsBySeriesAsync(Guid seriesId, CancellationToken ct = default)
    {
        var seriesKey = seriesId.ToString("N");
        var matches = _entries.Values
            .Where(e => e.DappSeriesId == seriesKey)
            .OrderBy(e => e.Order)
            .ToList();
        return Task.FromResult(new OASISResult<IEnumerable<DappSeriesQuest>>
        {
            Result = matches,
            Message = "Success",
        });
    }

    public Task<OASISResult<DappSeriesQuest>> UpsertSeriesQuestAsync(DappSeriesQuest entry, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(entry.Id, "N", out var id))
            return Task.FromResult(new OASISResult<DappSeriesQuest>
            {
                IsError = true,
                Message = "DappSeriesQuest.Id must be a Guid('N') hex string.",
            });

        _entries[id] = entry;
        return Task.FromResult(new OASISResult<DappSeriesQuest> { Result = entry, Message = "Upserted." });
    }

    public Task<OASISResult<bool>> DeleteSeriesQuestAsync(Guid seriesId, Guid questId, CancellationToken ct = default)
    {
        var seriesKey = seriesId.ToString("N");
        var questKey = questId.ToString("N");

        var match = _entries.Values.FirstOrDefault(e =>
            e.DappSeriesId == seriesKey && e.QuestId == questKey);
        if (match is null)
            return Task.FromResult(new OASISResult<bool>
            {
                IsError = true,
                Message = $"No DappSeriesQuest entry for series {seriesId} + quest {questId}.",
            });

        if (!Guid.TryParseExact(match.Id, "N", out var entryId))
            return Task.FromResult(new OASISResult<bool>
            {
                IsError = true,
                Message = "Stored entry has malformed Id.",
            });

        var removed = _entries.TryRemove(entryId, out _);
        return Task.FromResult(new OASISResult<bool>
        {
            Result = removed,
            Message = removed ? "Deleted." : "Concurrent removal.",
            IsError = !removed,
        });
    }
}
