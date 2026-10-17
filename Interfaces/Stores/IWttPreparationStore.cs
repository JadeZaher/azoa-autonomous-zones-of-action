using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Preserves unsigned blockhash/round/account choices and exactly one signed candidate per WTT generation.</summary>
public interface IWttPreparationStore
{
    Task<WttPreparation?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>Returns the durable winning plan; an incumbent with another immutable binding is a conflict.</summary>
    Task<WttPreparation> ClaimAsync(WttPreparation candidate, CancellationToken ct = default);
    /// <summary>Fills the signed candidate only under the exact unsealed version and winning unsigned plan.</summary>
    Task<WttPreparation?> TrySaveSignedAsync(WttPreparation expected, string signedPreparationJson, CancellationToken ct = default);
    /// <summary>Permanently retires a journal-proven never-authorized generation; signing persistence and replay then refuse it.</summary>
    Task<WttPreparation?> SealAsync(string id, string bindingJson, CancellationToken ct = default);
}
