using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Opt-in Holon AssetType registry orchestrator (final-hardening-cutover F5).
/// See <c>Managers/AGENTS.md</c> §holon-type-registry.
/// </summary>
public sealed class HolonTypeRegistryManager : IHolonTypeRegistryManager
{
    private readonly IHolonTypeRegistryStore _store;

    public HolonTypeRegistryManager(IHolonTypeRegistryStore store)
    {
        _store = store;
    }

    public Task<AZOAResult<IEnumerable<HolonType>>> ListAsync(AZOARequest? request = null)
        => _store.ListAsync(default);

    public Task<AZOAResult<HolonType>> GetAsync(string assetType, AZOARequest? request = null)
        => _store.GetByAssetTypeAsync(assetType, default);

    public async Task<AZOAResult<HolonType>> RegisterAsync(HolonTypeRegisterModel model, AZOARequest? request = null)
    {
        if (string.IsNullOrWhiteSpace(model.AssetType))
            return new AZOAResult<HolonType> { IsError = true, Message = "AssetType is required." };

        // Preserve the original CreatedAt on a re-register so the timestamp reflects
        // first registration, not last edit.
        var existing = await _store.GetByAssetTypeAsync(model.AssetType, default);
        var now = DateTimeOffset.UtcNow;

        var type = new HolonType
        {
            Id = model.AssetType,
            AssetType = model.AssetType,
            Description = model.Description ?? string.Empty,
            RequiredMetadataFields = model.RequiredMetadataFields
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            IsActive = model.IsActive,
            CreatedAt = existing is { IsError: false, Result: { } prior } ? prior.CreatedAt : now,
            ModifiedAt = existing is { IsError: false, Result: not null } ? now : null,
        };

        return await _store.UpsertAsync(type, default);
    }

    public async Task<AZOAResult<HolonType>> DeactivateAsync(string assetType, AZOARequest? request = null)
    {
        var existing = await _store.GetByAssetTypeAsync(assetType, default);
        if (existing.IsError || existing.Result == null)
            return new AZOAResult<HolonType> { IsError = true, Message = existing.Message };

        var type = existing.Result;
        type.IsActive = false;
        type.ModifiedAt = DateTimeOffset.UtcNow;
        return await _store.UpsertAsync(type, default);
    }

    public Task<AZOAResult<bool>> DeleteAsync(string assetType, AZOARequest? request = null)
        => _store.DeleteAsync(assetType, default);

    public async Task<AZOAResult<bool>> ValidateAsync(
        string? assetType, IReadOnlyDictionary<string, string>? metadata, AZOARequest? request = null)
    {
        // Opt-in: an absent AssetType is unconstrained.
        if (string.IsNullOrWhiteSpace(assetType))
            return new AZOAResult<bool> { Result = true, Message = "No asset type." };

        var lookup = await _store.GetByAssetTypeAsync(assetType, default);

        // Opt-in: a lookup error / not-found means the type is unregistered → free string.
        // A registry read FAILURE must not block holon creation for unregistered types, so
        // we treat any non-result as "unconstrained" (fail-open by design here — the registry
        // is an additive constraint, not a security gate).
        if (lookup.IsError || lookup.Result == null)
            return new AZOAResult<bool> { Result = true, Message = "Type not registered (unconstrained)." };

        var type = lookup.Result;

        // Inactive registration ⇒ ignored (opt-out without delete).
        if (!type.IsActive)
            return new AZOAResult<bool> { Result = true, Message = "Type inactive (unconstrained)." };

        var required = type.RequiredMetadataFields;
        if (required == null || required.Count == 0)
            return new AZOAResult<bool> { Result = true, Message = "Type registered; no metadata constraints." };

        var missing = required
            .Where(field => metadata == null
                            || !metadata.TryGetValue(field, out var v)
                            || string.IsNullOrWhiteSpace(v))
            .ToList();

        if (missing.Count > 0)
            return new AZOAResult<bool>
            {
                IsError = true,
                Message = $"Asset type '{assetType}' requires metadata field(s): {string.Join(", ", missing)}.",
            };

        return new AZOAResult<bool> { Result = true, Message = "Valid." };
    }
}
