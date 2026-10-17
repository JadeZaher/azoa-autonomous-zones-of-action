using AZOA.WebAPI.Models.Deployments;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Locks exact deployment visibility and resource content inside workflow admission.</summary>
public static class DeploymentAuthorityFence
{
    public const string FailureMarker = "ACTION_ADMISSION_DEPLOYMENT_CHANGED";

    // raw: resource and bundle witnesses commit atomically with the run and first saga; see AGENTS.md.
    public static IReadOnlyList<SurrealQuery> Create(DeploymentAuthorityWitness witness, Guid actor)
    {
        var result = new List<SurrealQuery>();
        if (witness.Holons.Count > 0) result.Add(LocalEntityMutationFence.Holons());
        var index = 0;
        foreach (var bundle in witness.Bundles)
        {
            var scope = "_deployment_bundle_" + index++;
            var config = bundle.ExpectedDeploymentConfig is null ? "" : $" AND deployment_config = ${scope}_config";
            var owner = bundle.OwnerUserId is null ? "user_id = NONE" : $"user_id = type::record('user', ${scope}_owner)";
            var query = SurrealQuery.Of($"""
                LET ${scope} = (UPDATE type::record('action_graph_bundle', ${scope}_id) SET is_active = is_active
                    WHERE is_active = true AND {owner}
                      AND is_public = ${scope}_public AND (user_id = type::record('user', ${scope}_actor) OR is_public = true){config} RETURN AFTER)
                """).WithParam(scope + "_id", bundle.BundleId.ToString("N"))
                .WithParam(scope + "_public", bundle.IsPublic).WithParam(scope + "_actor", actor.ToString("N"));
            if (bundle.OwnerUserId is { } ownerId) query = query.WithParam(scope + "_owner", ownerId.ToString("N"));
            if (bundle.ExpectedDeploymentConfig is not null) query = query.WithParam(scope + "_config", bundle.ExpectedDeploymentConfig);
            result.Add(query);
            result.Add(SurrealQuery.Of($"IF array::len(${scope}) != 1 {{ THROW '{FailureMarker}' }}"));
        }
        index = 0;
        foreach (var holon in witness.Holons)
        {
            var scope = "_deployment_holon_" + index++;
            var values = new Dictionary<string, object?>
            {
                ["name"] = holon.Name, ["description"] = holon.Description, ["provider_name"] = holon.ProviderName,
                ["chain_id"] = holon.ChainId, ["asset_type"] = holon.AssetType, ["token_id"] = holon.TokenId,
                ["metadata"] = holon.Metadata, ["peer_holon_ids"] = holon.PeerHolonIds.Select(id => id.ToString("N")).ToArray(),
                ["created_date"] = Utc(holon.CreatedDate), ["modified_date"] = holon.ModifiedDate is { } modified ? Utc(modified) : null,
                ["is_active"] = holon.IsActive, ["is_public"] = holon.IsPublic,
            };
            var links = new Dictionary<string, (string Table, Guid? Id)>
            {
                ["parent_holon_id"] = ("holon", holon.ParentHolonId), ["user_id"] = ("user", holon.UserId),
                ["source_holon_id"] = ("holon", holon.SourceHolonId), ["origin_user_id"] = ("user", holon.OriginUserId),
            };
            var predicates = values.Select(pair => pair.Value is null ? $"{pair.Key} = NONE" : $"{pair.Key} = ${scope}_{pair.Key}")
                .Concat(links.Select(pair => pair.Value.Id is null ? $"{pair.Key} = NONE" : $"{pair.Key} = type::record('{pair.Value.Table}', ${scope}_{pair.Key})"));
            var query = SurrealQuery.Of($"""
                LET ${scope} = (UPDATE type::record('holon', ${scope}_id) SET is_active = is_active
                    WHERE {string.Join(" AND ", predicates)}
                      AND (user_id = type::record('user', ${scope}_actor) OR is_public = true) RETURN AFTER)
                """).WithParam(scope + "_id", holon.Id.ToString("N")).WithParam(scope + "_actor", actor.ToString("N"));
            foreach (var pair in values.Where(pair => pair.Value is not null)) query = query.WithParam(scope + "_" + pair.Key, pair.Value);
            foreach (var pair in links.Where(pair => pair.Value.Id is not null)) query = query.WithParam(scope + "_" + pair.Key, pair.Value.Id!.Value.ToString("N"));
            result.Add(query);
            result.Add(SurrealQuery.Of($"IF array::len(${scope}) != 1 {{ THROW '{FailureMarker}' }}"));
        }
        return result;
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
