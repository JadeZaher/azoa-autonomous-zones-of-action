using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Actions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealActionLocalMutationStore
{
    private static void AppendWitnesses(List<SurrealQuery> statements, ActionLocalMutationPlan plan)
    {
        var index = 0;
        foreach (var (id, row) in plan.HolonReads.OrderBy(pair => pair.Key))
        {
            Dictionary<string, object?>? fields = null;
            if (row is not null)
            {
                var stored = SurrealHolonStore.ToPoco(row);
                fields = new()
                {
                    ["name"] = stored.Name, ["description"] = stored.Description,
                    ["parent_holon_id"] = stored.ParentHolonId, ["user_id"] = stored.UserId,
                    ["provider_name"] = stored.ProviderName, ["chain_id"] = stored.ChainId,
                    ["asset_type"] = stored.AssetType, ["token_id"] = stored.TokenId,
                    ["metadata"] = stored.Metadata, ["peer_holon_ids"] = stored.PeerHolonIds,
                    ["created_date"] = stored.CreatedDate, ["modified_date"] = stored.ModifiedDate,
                    ["is_active"] = stored.IsActive, ["source_holon_id"] = stored.SourceHolonId,
                    ["origin_user_id"] = stored.OriginUserId, ["is_public"] = stored.IsPublic,
                    ["transfer_reservation_key"] = null
                };
            }
            statements.AddRange(RowWitness("holon", "is_active", id, fields, index++));
        }
        foreach (var (id, row) in plan.WalletReads.OrderBy(pair => pair.Key))
        {
            Dictionary<string, object?>? fields = null;
            if (row is not null)
            {
                var stored = SurrealWalletStore.ToPoco(row);
                fields = new()
                {
                    ["user_id"] = stored.UserId, ["chain_type"] = stored.ChainType, ["address"] = stored.Address,
                    ["public_key"] = stored.PublicKey, ["label"] = stored.Label, ["is_default"] = stored.IsDefault,
                    ["wallet_type"] = stored.WalletType.ToString(), ["encrypted_private_key"] = stored.EncryptedPrivateKey,
                    ["encrypted_seed_phrase"] = stored.EncryptedSeedPhrase, ["created_date"] = stored.CreatedDate
                };
            }
            statements.AddRange(RowWitness("wallet", "is_default", id, fields, index++));
        }
        foreach (var witness in plan.HolonQueries)
        {
            var filters = new Dictionary<string, object?>();
            var query = witness.Query;
            if (query?.UserId is { } user) filters["user_id"] = "user:" + user.ToString("N");
            if (query?.ParentHolonId is { } parent) filters["parent_holon_id"] = "holon:" + parent.ToString("N");
            if (!string.IsNullOrEmpty(query?.ProviderName)) filters["provider_name"] = query.ProviderName;
            if (!string.IsNullOrEmpty(query?.ChainId)) filters["chain_id"] = query.ChainId;
            if (!string.IsNullOrEmpty(query?.AssetType)) filters["asset_type"] = query.AssetType;
            if (query?.IsActive is { } active) filters["is_active"] = active;
            statements.Add(QueryWitness("holon", filters, query?.Name, witness.Ids, index++));
        }
        foreach (var witness in plan.WalletQueries)
        {
            var filters = new Dictionary<string, object?>();
            if (witness.Owner is { } owner) filters["user_id"] = "user:" + owner.ToString("N");
            statements.Add(QueryWitness("wallet", filters, null, witness.Ids, index++));
        }
    }

    // raw: the exact original row is locked and compared before the caller's atomic writes.
    private static IReadOnlyList<SurrealQuery> RowWitness(string table, string lockColumn, Guid id,
        IReadOnlyDictionary<string, object?>? fields, int index)
    {
        var name = Name("row_witness", index);
        var scope = SurrealIdentifierTemplate.Token(name);
        var tableToken = SurrealIdentifierTemplate.Token(table);
        if (fields is null)
            return [SurrealIdentifierTemplate.Of($"IF (SELECT * FROM ONLY type::record('{tableToken}', ${scope}_id)) != NONE {{ THROW 'ACTION_LOCAL_WITNESS_CHANGED' }}")
                .WithParam(name + "_id", id.ToString("N"))];
        var parameters = new Dictionary<string, object?>();
        var conditions = Conditions(fields, name, parameters);
        var lockToken = SurrealIdentifierTemplate.Token(lockColumn);
        var statement = SurrealIdentifierTemplate.Of($"LET ${scope} = (UPDATE ONLY type::record('{tableToken}', ${scope}_id) SET {lockToken} = {lockToken} WHERE {SurrealIdentifierTemplate.And(conditions)} RETURN AFTER)")
            .WithParam(name + "_id", id.ToString("N"));
        foreach (var (key, value) in parameters) statement = statement.WithParam(key, value!);
        return [statement, SurrealIdentifierTemplate.Of($"IF ${scope} = NONE {{ THROW 'ACTION_LOCAL_WITNESS_CHANGED' }}")];
    }

    // raw: a table mutation lock protects this query-membership comparison from insert/delete phantoms.
    private static SurrealQuery QueryWitness(string table, IReadOnlyDictionary<string, object?> fields,
        string? nameFilter, IReadOnlyList<Guid> ids, int index)
    {
        var name = Name("query_witness", index);
        var scope = SurrealIdentifierTemplate.Token(name);
        var parameters = new Dictionary<string, object?>();
        var conditions = Conditions(fields, name, parameters);
        if (!string.IsNullOrEmpty(nameFilter))
        {
            conditions.Add(SurrealIdentifierTemplate.Fragment($"string::contains(name, <string>${scope}_name)"));
            parameters[name + "_name"] = nameFilter;
        }
        var predicate = conditions.Count == 0 ? SurrealTemplateFragment.Empty
            : SurrealIdentifierTemplate.Fragment($" WHERE {SurrealIdentifierTemplate.And(conditions)}");
        var statement = SurrealIdentifierTemplate.Of($"IF array::sort((SELECT VALUE <string>record::id(id) FROM {SurrealIdentifierTemplate.Token(table)}{predicate})) != ${scope}_ids {{ THROW 'ACTION_LOCAL_WITNESS_CHANGED' }}")
            .WithParam(name + "_ids", ids.Select(id => id.ToString("N")).Order(StringComparer.Ordinal).ToArray());
        foreach (var (key, value) in parameters) statement = statement.WithParam(key, value!);
        return statement;
    }

    private static List<SurrealTemplateFragment> Conditions(IReadOnlyDictionary<string, object?> fields, string prefix,
        IDictionary<string, object?> parameters)
    {
        var conditions = new List<SurrealTemplateFragment>();
        foreach (var (column, value) in fields)
        {
            var columnToken = SurrealIdentifierTemplate.Token(column);
            if (value is null) conditions.Add(SurrealIdentifierTemplate.Fragment($"{columnToken} = NONE"));
            else
            {
                var parameter = prefix + "_" + column;
                var parameterToken = SurrealIdentifierTemplate.Token(parameter);
                conditions.Add(value is string ? SurrealIdentifierTemplate.Fragment($"<string>{columnToken} = <string>${parameterToken}")
                    : SurrealIdentifierTemplate.Fragment($"{columnToken} = ${parameterToken}"));

                parameters[parameter] = value is JsonElement element ? element.Clone() : value;
            }
        }
        return conditions;
    }

    private static void AppendHolonNulls(List<SurrealQuery> statements, AZOA.WebAPI.Models.Holon row, int index)
    {
        var update = SurrealWriter.UpdateOnly<SurrealHolonStore.HolonPoco>(row.Id.ToString("N"));
        var needed = false;
        if (row.ParentHolonId is null) { update = update.Unset(item => item.ParentHolonId); needed = true; }
        if (row.ChainId is null) { update = update.Unset(item => item.ChainId); needed = true; }
        if (row.AssetType is null) { update = update.Unset(item => item.AssetType); needed = true; }
        if (row.TokenId is null) { update = update.Unset(item => item.TokenId); needed = true; }
        if (row.ModifiedDate is null) { update = update.Unset(item => item.ModifiedDate); needed = true; }
        if (row.SourceHolonId is null) { update = update.Unset(item => item.SourceHolonId); needed = true; }
        if (row.OriginUserId is null) { update = update.Unset(item => item.OriginUserId); needed = true; }
        if (needed) statements.Add(SurrealGeneratedStatementScope.Apply(update.Build(), Name("holon_nulls", index)));
    }

    private static void AppendWalletNulls(List<SurrealQuery> statements, AZOA.WebAPI.Models.Wallet row, int index)
    {
        var update = SurrealWriter.UpdateOnly<AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet>(row.Id.ToString("N"));
        var needed = false;
        if (row.PublicKey is null) { update = update.Unset(item => item.PublicKey); needed = true; }
        if (row.Label is null) { update = update.Unset(item => item.Label); needed = true; }
        if (row.EncryptedPrivateKey is null) { update = update.Unset(item => item.EncryptedPrivateKey); needed = true; }
        if (row.EncryptedSeedPhrase is null) { update = update.Unset(item => item.EncryptedSeedPhrase); needed = true; }
        if (needed) statements.Add(SurrealGeneratedStatementScope.Apply(update.Build(), Name("wallet_nulls", index)));
    }
}
