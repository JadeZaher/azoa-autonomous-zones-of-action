namespace AZOA.WebAPI.Models.Deployments;

/// <summary>Exact live resource witnesses validated atomically with a new workflow admission.</summary>
public sealed record DeploymentAuthorityWitness(IReadOnlyList<DeploymentBundleWitness> Bundles,
    IReadOnlyList<DeploymentHolonWitness> Holons);
public sealed record DeploymentBundleWitness(Guid BundleId, Guid? OwnerUserId, bool IsPublic,
    string? ExpectedDeploymentConfig);
public sealed record DeploymentHolonWitness(Guid Id, string Name, string Description, Guid? ParentHolonId,
    Guid? UserId, string ProviderName, string? ChainId, string? AssetType, string? TokenId,
    Dictionary<string, string> Metadata, IReadOnlyList<Guid> PeerHolonIds, DateTime CreatedDate,
    DateTime? ModifiedDate, bool IsActive, Guid? SourceHolonId, Guid? OriginUserId, bool IsPublic);
