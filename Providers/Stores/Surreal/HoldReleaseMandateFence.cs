using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

internal static class HoldReleaseMandateFence
{
    /// <summary>Composes exact persisted hold-mandate authority with current revocation fences; never accepts a free-standing expired credential.</summary>
    internal static IReadOnlyList<SurrealQuery>? Create(HoldReleaseMandate stored, string prefix)
    {
        if (stored.Status != "Active" || stored.Version is < 0 or long.MaxValue || stored.NotBefore >= stored.ExpiresAt
            || string.IsNullOrWhiteSpace(stored.Id) || string.IsNullOrWhiteSpace(stored.HoldRecordId)
            || stored.TermsHash != CanonicalJson.HashCanonical(stored.TermsJson)) return null;
        var terms = JsonSerializer.Deserialize<HoldReleaseMandateTerms>(stored.TermsJson);
        if (terms is null || terms.Version != 1 || terms.HoldRecordId != stored.HoldRecordId
            || terms.NotBefore != stored.NotBefore || terms.ExpiresAt != stored.ExpiresAt) return null;
        var witness = JsonSerializer.Deserialize<SigningConsentWitness>(stored.CurrentAuthorityJson);
        if (witness is null || witness.Version != 1 || witness.Credential.ActorUserId.ToString("N") != stored.ActorUserId
            || witness.Credential.TenantId?.ToString("N") != stored.TenantId
            || !witness.RequiredScopes.SequenceEqual([AzoaScopes.TransferSign])) return null;
        var current = SigningAuthorityFence.Credential(witness.Credential, prefix, false);
        if (current is null || !SigningAuthorityFence.Grants(current, witness.Credential, witness.RequiredScopes, witness.Grants, prefix)) return null;
        if (stored.WorkflowSourceJson is not null)
        {
            var workflow = HoldReleaseWorkflowFence.Create(stored);
            if (workflow is null) return null;
            current.AddRange(workflow);
        }
        if (stored.ExchangeSourceJson is not null)
        {
            var exchange = HoldReleaseExchangeFence.Create(stored);
            if (exchange is null) return null;
            current.AddRange(exchange);
        }
        var result = new List<SurrealQuery>
        {
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldReleaseMandate>(stored.Id)
                .Where(row => row.Version == stored.Version && row.Status == "Active" && row.ActorUserId == stored.ActorUserId
                    && row.TenantId == stored.TenantId && row.HoldRecordId == stored.HoldRecordId
                    && row.TermsHash == stored.TermsHash && row.TermsJson == stored.TermsJson
                    && row.CurrentAuthorityJson == stored.CurrentAuthorityJson && row.CurrentAuthorizationId == stored.CurrentAuthorizationId
                    && row.WorkflowSourceJson == stored.WorkflowSourceJson
                    && row.ExchangeSourceJson == stored.ExchangeSourceJson
                    && row.NotBefore == stored.NotBefore && row.ExpiresAt == stored.ExpiresAt)
                .Set(row => row.Version, stored.Version).Build(), prefix + "_mandate", "_hold_release_mandate"),
            // raw: only the exact live persisted mandate permits delayed execution beyond its original JWT lifetime.
            SurrealQuery.Of("""
                IF $_hold_release_mandate = NONE OR $_hold_release_mandate = []
                    OR $_hold_release_mandate.not_before > time::now() OR $_hold_release_mandate.expires_at <= time::now()
                    { THROW 'SIGNING_AUTHORITY_CHANGED' }
                """),
        };
        result.AddRange(current);
        return result;
    }
}
