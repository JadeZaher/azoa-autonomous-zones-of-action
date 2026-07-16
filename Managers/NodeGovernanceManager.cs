using Microsoft.Extensions.Options;
using SurrealForge.Client;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Managers;

public sealed class NodeGovernanceManager : INodeGovernanceManager
{
    private const int MaxListItems = 64;
    private const int MaxTokenLength = 128;
    private const int MaxAuditLimit = 100;

    private readonly INodeGovernanceStore _store;
    private readonly NodeGovernanceOptions _options;

    public NodeGovernanceManager(INodeGovernanceStore store, IOptions<NodeGovernanceOptions>? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options?.Value ?? new NodeGovernanceOptions();
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeGovernanceParametersResponse>> GetParametersAsync(CancellationToken ct = default)
    {
        var current = await _store.GetParametersAsync(ct);
        if (current.IsError)
            return AZOAResult<NodeGovernanceParametersResponse>.Failure(current.Message);

        return new AZOAResult<NodeGovernanceParametersResponse>
        {
            Result = current.Result is null ? ToDefaultResponse() : ToResponse(current.Result),
            Message = "Success",
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeGovernanceParametersResponse>> UpdateParametersAsync(
        NodeGovernanceParametersUpdateRequest request,
        Guid actorAvatarId,
        CancellationToken ct = default)
    {
        if (request is null)
            return AZOAResult<NodeGovernanceParametersResponse>.Failure("Node governance parameters are required.");
        if (actorAvatarId == Guid.Empty)
            return AZOAResult<NodeGovernanceParametersResponse>.Failure("Actor avatar id is required.");

        var errors = new List<string>();
        if (request.ExpectedVersion < 0)
            errors.Add("ExpectedVersion cannot be negative.");
        var allowedChains = NormalizeList(request.AllowedChains, "AllowedChains", errors);
        var allowedAssetTypes = NormalizeList(request.AllowedAssetTypes, "AllowedAssetTypes", errors);
        if (errors.Count > 0)
            return AZOAResult<NodeGovernanceParametersResponse>.Failure(string.Join(" ", errors));

        var current = await _store.GetParametersAsync(ct);
        if (current.IsError)
            return AZOAResult<NodeGovernanceParametersResponse>.Failure($"Node governance parameters unavailable: {current.Message}");

        var now = DateTimeOffset.UtcNow;
        var previous = current.Result;
        var currentVersion = previous?.Version ?? 0;
        if (request.ExpectedVersion.HasValue && request.ExpectedVersion.Value != currentVersion)
        {
            return AZOAResult<NodeGovernanceParametersResponse>.Failure(
                $"Node governance parameters version conflict: expected {request.ExpectedVersion.Value}, current {currentVersion}.");
        }

        var nextVersion = (previous?.Version ?? 0) + 1;
        var actorLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(actorAvatarId)) ?? string.Empty;

        var parameters = new NodeGovernanceParameters
        {
            Id = NodeGovernanceParameters.LocalId,
            AllowedChains = allowedChains,
            AllowedAssetTypes = allowedAssetTypes,
            Version = nextVersion,
            UpdatedByAvatarId = actorLink,
            CreatedAt = previous?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        var audit = new NodeGovernanceAudit
        {
            Id = SurrealId.ToSurrealId(Guid.NewGuid()),
            Action = "ParametersUpdated",
            ActorAvatarId = actorLink,
            PreviousVersion = previous?.Version ?? 0,
            NewVersion = nextVersion,
            PreviousAllowedChains = previous?.AllowedChains,
            PreviousAllowedAssetTypes = previous?.AllowedAssetTypes,
            AllowedChains = allowedChains,
            AllowedAssetTypes = allowedAssetTypes,
            Detail = "Node governance parameters updated.",
            OccurredAt = now,
        };

        var saved = await _store.UpdateParametersWithAuditAsync(parameters, audit, previous?.Version, ct);
        if (saved.IsError || saved.Result is null)
            return AZOAResult<NodeGovernanceParametersResponse>.Failure(saved.Message);

        return new AZOAResult<NodeGovernanceParametersResponse>
        {
            Result = ToResponse(saved.Result),
            Message = "Saved.",
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<NodeGovernanceAuditResponse>>> ListAuditAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit, 1, MaxAuditLimit);
        var result = await _store.ListAuditAsync(clamped, ct);
        if (result.IsError || result.Result is null)
            return AZOAResult<IEnumerable<NodeGovernanceAuditResponse>>.Failure(result.Message);

        return new AZOAResult<IEnumerable<NodeGovernanceAuditResponse>>
        {
            Result = result.Result.Select(ToResponse).ToList(),
            Message = "Success",
        };
    }

    private NodeGovernanceParametersResponse ToDefaultResponse()
    {
        var errors = new List<string>();
        return new NodeGovernanceParametersResponse
        {
            AllowedChains = NormalizeList(_options.AllowedChains, "AllowedChains", errors),
            AllowedAssetTypes = NormalizeList(_options.AllowedAssetTypes, "AllowedAssetTypes", errors),
            Version = 0,
        };
    }

    private static IReadOnlyList<string>? NormalizeList(
        IReadOnlyList<string>? values,
        string fieldName,
        List<string> errors)
    {
        if (values is null)
            return null;

        if (values.Count > MaxListItems)
            errors.Add($"{fieldName} may contain at most {MaxListItems} entries.");

        var normalized = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{fieldName} cannot contain blank entries.");
                continue;
            }

            var trimmed = value.Trim();
            if (trimmed.Length > MaxTokenLength)
            {
                errors.Add($"{fieldName} entry '{trimmed[..Math.Min(trimmed.Length, 32)]}' exceeds {MaxTokenLength} characters.");
                continue;
            }

            if (!normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                normalized.Add(trimmed);
        }

        return normalized
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static NodeGovernanceParametersResponse ToResponse(NodeGovernanceParameters row) => new()
    {
        AllowedChains = row.AllowedChains,
        AllowedAssetTypes = row.AllowedAssetTypes,
        Version = row.Version,
        UpdatedByAvatarId = SurrealRecordGuid.ParseOptional(row.UpdatedByAvatarId),
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static NodeGovernanceAuditResponse ToResponse(NodeGovernanceAudit row) => new()
    {
        Id = SurrealRecordGuid.Parse(row.Id),
        Action = row.Action,
        ActorAvatarId = SurrealRecordGuid.ParseOptional(row.ActorAvatarId) ?? Guid.Empty,
        PreviousVersion = row.PreviousVersion,
        NewVersion = row.NewVersion,
        PreviousAllowedChains = row.PreviousAllowedChains,
        PreviousAllowedAssetTypes = row.PreviousAllowedAssetTypes,
        AllowedChains = row.AllowedChains,
        AllowedAssetTypes = row.AllowedAssetTypes,
        Detail = row.Detail,
        OccurredAt = row.OccurredAt,
    };

}
