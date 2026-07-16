using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using SurrealForge.Client;

namespace AZOA.WebAPI.Managers;

public sealed class NodeTreasuryManager : INodeTreasuryManager
{
    private const int MaxAuditLimit = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly INodeTreasuryStore _store;
    private readonly IBlockchainProviderFactory _providerFactory;

    public NodeTreasuryManager(
        INodeTreasuryStore store,
        IBlockchainProviderFactory providerFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeTreasuryDestinationResponse>> GetDestinationAsync(
        string chain,
        ChainNetwork network,
        CancellationToken ct = default)
    {
        var providerResult = ResolveProvider(chain, network);
        if (providerResult.IsError || providerResult.Result is null)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(providerResult.Message);

        var canonicalChain = providerResult.Result.ChainType;
        var current = await _store.GetDestinationAsync(canonicalChain, network, ct);
        if (current.IsError)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(
                $"Node treasury destination unavailable: {current.Message}");
        if (current.Result is null)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(
                $"Node treasury destination is not configured for {canonicalChain}/{network}.");

        var validation = await ValidateAddressAsync(providerResult.Result, current.Result.Address, ct);
        if (validation.IsError)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(validation.Message);

        return new AZOAResult<NodeTreasuryDestinationResponse>
        {
            Result = ToResponse(current.Result),
            Message = "Success",
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeTreasuryDestinationResponse>> UpdateDestinationAsync(
        NodeTreasuryDestinationUpdateRequest request,
        Guid actorAvatarId,
        CancellationToken ct = default)
    {
        if (request is null)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure("Node treasury destination update is required.");
        if (actorAvatarId == Guid.Empty)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure("Actor avatar id is required.");
        if (string.IsNullOrWhiteSpace(request.Chain))
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure("Chain is required.");
        if (!request.Network.HasValue || !Enum.IsDefined(request.Network.Value))
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure("A valid network is required.");
        if (string.IsNullOrWhiteSpace(request.Address))
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure("Treasury address is required.");
        if (request.ExpectedVersion < 0)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure("ExpectedVersion cannot be negative.");

        var network = request.Network.Value;
        var providerResult = ResolveProvider(request.Chain, network);
        if (providerResult.IsError || providerResult.Result is null)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(providerResult.Message);

        var provider = providerResult.Result;
        var address = request.Address.Trim();
        var validation = await ValidateAddressAsync(provider, address, ct);
        if (validation.IsError)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(validation.Message);

        var current = await _store.GetDestinationAsync(provider.ChainType, network, ct);
        if (current.IsError)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(
                $"Node treasury destination unavailable: {current.Message}");

        var previous = current.Result;
        if (previous is not null && string.Equals(previous.Address, address, StringComparison.Ordinal))
        {
            return new AZOAResult<NodeTreasuryDestinationResponse>
            {
                Result = ToResponse(previous),
                Message = "No treasury destination changes were required.",
            };
        }

        var currentVersion = previous?.Version ?? 0;
        if (request.ExpectedVersion.HasValue && request.ExpectedVersion.Value != currentVersion)
        {
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(
                $"Node treasury destination version conflict: expected " +
                $"{request.ExpectedVersion.Value}, current {currentVersion}.");
        }

        var now = DateTimeOffset.UtcNow;
        var actorLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(actorAvatarId))
            ?? string.Empty;
        var networkName = network.ToString();
        var next = new NodeTreasuryDestination
        {
            Id = NodeTreasuryDestination.RecordIdFor(provider.ChainType, networkName),
            Chain = provider.ChainType,
            Network = networkName,
            Address = address,
            Version = currentVersion + 1,
            UpdatedByAvatarId = actorLink,
            CreatedAt = previous?.CreatedAt ?? now,
            UpdatedAt = now,
        };
        var audit = new NodeTreasuryAudit
        {
            Id = SurrealId.ToSurrealId(Guid.NewGuid()),
            Action = "DestinationUpdated",
            ActorAvatarId = actorLink,
            Chain = next.Chain,
            Network = next.Network,
            PreviousVersion = currentVersion,
            NewVersion = next.Version,
            PreviousDestinationJson = previous is null
                ? null
                : JsonSerializer.Serialize(ToResponse(previous), JsonOptions),
            DestinationJson = JsonSerializer.Serialize(ToResponse(next), JsonOptions),
            Detail = "Node treasury destination updated.",
            OccurredAt = now,
        };

        var saved = await _store.UpdateDestinationWithAuditAsync(
            next,
            audit,
            previous?.Version,
            ct);
        if (saved.IsError
            && saved.Message.Contains("version conflict", StringComparison.OrdinalIgnoreCase))
        {
            var latest = await _store.GetDestinationAsync(provider.ChainType, network, ct);
            if (!latest.IsError
                && latest.Result is not null
                && string.Equals(latest.Result.Address, address, StringComparison.Ordinal))
            {
                return new AZOAResult<NodeTreasuryDestinationResponse>
                {
                    Result = ToResponse(latest.Result),
                    Message = "No treasury destination changes were required.",
                };
            }
        }
        if (saved.IsError || saved.Result is null)
            return AZOAResult<NodeTreasuryDestinationResponse>.Failure(saved.Message);

        return new AZOAResult<NodeTreasuryDestinationResponse>
        {
            Result = ToResponse(saved.Result),
            Message = "Saved.",
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<NodeTreasuryAuditResponse>>> ListAuditAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        var result = await _store.ListAuditAsync(Math.Clamp(limit, 1, MaxAuditLimit), ct);
        if (result.IsError || result.Result is null)
            return AZOAResult<IEnumerable<NodeTreasuryAuditResponse>>.Failure(result.Message);

        return new AZOAResult<IEnumerable<NodeTreasuryAuditResponse>>
        {
            Result = result.Result.Select(ToResponse).ToList(),
            Message = "Success",
        };
    }

    private AZOAResult<IBlockchainProvider> ResolveProvider(string chain, ChainNetwork network)
    {
        if (string.IsNullOrWhiteSpace(chain))
            return AZOAResult<IBlockchainProvider>.Failure("Chain is required.");

        try
        {
            var requestedChain = chain.Trim();
            var provider = _providerFactory.GetProvider(requestedChain, network);
            if (!string.Equals(provider.ChainType, requestedChain, StringComparison.OrdinalIgnoreCase))
            {
                return AZOAResult<IBlockchainProvider>.Failure(
                    $"Configured provider '{provider.ChainType}' does not match requested chain " +
                    $"'{requestedChain}'; treasury destination denied.");
            }
            if (provider.ActiveNetwork != network)
            {
                return AZOAResult<IBlockchainProvider>.Failure(
                    $"Configured provider for {requestedChain} is active on {provider.ActiveNetwork}, " +
                    $"not {network}; treasury destination denied.");
            }

            return new AZOAResult<IBlockchainProvider> { Result = provider, Message = "Success" };
        }
        catch (BlockchainProviderNotFoundException)
        {
            return AZOAResult<IBlockchainProvider>.Failure(
                $"No configured blockchain provider is available for {chain.Trim()}/{network}.");
        }
    }

    private static async Task<AZOAResult<bool>> ValidateAddressAsync(
        IBlockchainProvider provider,
        string address,
        CancellationToken ct)
    {
        var validation = await provider.ValidateAddressAsync(address, ct);
        if (validation.IsError || validation.Result != true)
        {
            return AZOAResult<bool>.Failure(
                $"Treasury address validation failed for {provider.ChainType}/{provider.ActiveNetwork}: " +
                $"{validation.Message}");
        }

        return new AZOAResult<bool> { Result = true, Message = "Success" };
    }

    private static NodeTreasuryDestinationResponse ToResponse(NodeTreasuryDestination row)
        => new()
        {
            Chain = row.Chain,
            Network = ParseNetwork(row.Network),
            Address = row.Address,
            Version = row.Version,
            UpdatedByAvatarId = SurrealRecordGuid.ParseOptional(row.UpdatedByAvatarId),
            CreatedAt = row.CreatedAt == default ? null : row.CreatedAt,
            UpdatedAt = row.UpdatedAt == default ? null : row.UpdatedAt,
        };

    private static NodeTreasuryAuditResponse ToResponse(NodeTreasuryAudit row)
        => new()
        {
            Id = SurrealRecordGuid.Parse(row.Id),
            Action = row.Action,
            ActorAvatarId = SurrealRecordGuid.ParseOptional(row.ActorAvatarId) ?? Guid.Empty,
            Chain = row.Chain,
            Network = ParseNetwork(row.Network),
            PreviousVersion = row.PreviousVersion,
            NewVersion = row.NewVersion,
            PreviousDestinationJson = row.PreviousDestinationJson,
            DestinationJson = row.DestinationJson,
            Detail = row.Detail,
            OccurredAt = row.OccurredAt,
        };

    private static ChainNetwork ParseNetwork(string raw)
        => Enum.TryParse<ChainNetwork>(raw, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException($"Invalid treasury network '{raw}'.");

}
