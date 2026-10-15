using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persistence boundary for <see cref="IActionGraphBundle"/> aggregates.</summary>
public interface IActionGraphBundleStore
{
    /// <summary>Loads a single action graph bundle by id.</summary>
    Task<AZOAResult<IActionGraphBundle>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Loads the action graph bundle matching <paramref name="name"/> (case-insensitive)
    /// AND owned by <paramref name="userId"/>. Returns <c>Result == null</c>
    /// (no error) when no match exists — the manager interprets that as
    /// "create new". This is the IDOR-safe lookup the manager uses on POST.
    /// </summary>
    Task<AZOAResult<IActionGraphBundle>> GetByNameAndUserAsync(string name, Guid userId, CancellationToken ct = default);

    /// <summary>Loads every action graph bundle.</summary>
    Task<AZOAResult<IEnumerable<IActionGraphBundle>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates a action graph bundle.</summary>
    Task<AZOAResult<IActionGraphBundle>> UpsertAsync(IActionGraphBundle odk, CancellationToken ct = default);

    /// <summary>Deletes an action graph bundle by id.</summary>
    Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
}
