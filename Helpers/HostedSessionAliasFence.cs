using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Helpers;

/// <summary>Reserves every trusted callback alias in the session attachment transaction.</summary>
public static class HostedSessionAliasFence
{
    public const string ConflictMarker = "HOSTED_ALIAS_ALREADY_OWNED";

    public static IReadOnlyList<SurrealQuery> Reserve(HostedSession session, string providerSessionId, string? correlationId)
    {
        var aliases = new[] { providerSessionId, correlationId }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal)
            .Select(value => new
            {
                id = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                    new[] { session.FlowKind, session.ProviderKey, value }))).ToLowerInvariant(),
                value,
            }).ToArray();
        var statements = new List<SurrealQuery>();
        for (var index = 0; index < aliases.Length; index++)
        {
            var alias = aliases[index];
            var suffix = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // raw: aliases and their owning session are reserved atomically; see AGENTS.md.
            statements.Add(SurrealQuery.Of("""
                LET $_collision = (SELECT VALUE id FROM hosted_session
                    WHERE id != type::record('hosted_session', $_owner)
                        AND flow_kind = $_flow AND provider_key = $_provider
                        AND (provider_session_id = $_alias OR provider_correlation_id = $_alias) LIMIT 1)
                """.Replace("$_alias", "$_alias" + suffix)).WithParam("_owner", session.Id).WithParam("_flow", session.FlowKind)
                .WithParam("_provider", session.ProviderKey).WithParam("_alias" + suffix, alias.value));
            statements.Add(SurrealQuery.Of("IF array::len($_collision) != 0 { THROW 'HOSTED_ALIAS_ALREADY_OWNED' }"));
            statements.Add(SurrealQuery.Of("""
                LET $_reserved = (UPSERT type::record('hosted_session_provider_alias', $_alias_key)
                    SET hosted_session_id = $_owner, flow_kind = $_flow, provider_key = $_provider,
                        provider_session_id = $_alias
                    WHERE hosted_session_id = NONE OR hosted_session_id = $_owner RETURN AFTER)
                """.Replace("$_alias_key", "$_key" + suffix).Replace("$_alias", "$_alias" + suffix)).WithParam("_key" + suffix, alias.id).WithParam("_owner", session.Id)
                .WithParam("_flow", session.FlowKind).WithParam("_provider", session.ProviderKey)
                .WithParam("_alias" + suffix, alias.value));
            statements.Add(SurrealQuery.Of("IF array::len($_reserved) != 1 { THROW 'HOSTED_ALIAS_ALREADY_OWNED' }"));
        }
        return statements;
    }
}
