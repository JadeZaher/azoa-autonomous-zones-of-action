using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client.Query;
using Handoff = AZOA.WebAPI.Persistence.SurrealDb.Models.MemberHandoff;
using Invite = AZOA.WebAPI.Persistence.SurrealDb.Models.WalletAuthClaimToken;
using Member = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Atomic handoff admission, invitation retirement and decision serialization; see AGENTS.md §member-handoff.</summary>
public sealed class SurrealMemberHandoffStore(ISurrealExecutor executor)
{
    public Task<Handoff?> GetAsync(Guid id, CancellationToken ct)
        => executor.QuerySingleAsync<Handoff>(SurrealQuery<Handoff>.Key(id.ToString("N")), ct);

    public async Task<IReadOnlyList<Handoff>> ListAsync(CancellationToken ct)
        => await executor.QueryAsync<Handoff>(SurrealQuery<Handoff>.From().OrderByDescending(row => row.UpdatedAt).Limit(50), ct);

    public async Task SaveDeliveryAsync(Handoff row, Handoff? previous, Invite? invite,
        Guid memberId, string? expectedOwner, DateTimeOffset? expectedWatermark,
        SigningConsentWitness authority, CancellationToken ct)
    {
        var guards = SigningAuthorityFence.ForConsent(authority, [AZOA.WebAPI.Core.AzoaScopes.TenantProvision], "handoff_authority")
            ?? throw new InvalidOperationException("HANDOFF_AUTHORITY_CHANGED");
        var memberRead = SurrealQuery<Member>.Key(memberId.ToString("N"));
        // raw: current authority, immutable member binding, retirement and protected delivery commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealGeneratedStatementScope.AssignRecord(memberRead, "handoff_member", "_handoff_member"));
        statements.Add(SurrealQuery.Of("""
            IF $_handoff_member = NONE OR $_handoff_member.is_active != true
                OR $_handoff_member.external_user_id != $_handoff_external
                OR (!$_handoff_watermark_present AND $_handoff_member.auth_not_before != NONE)
                OR ($_handoff_watermark_present AND $_handoff_member.auth_not_before != $_handoff_watermark)
                { THROW 'HANDOFF_AUTHORITY_CHANGED' }
            """)
            .WithParam("_handoff_external", row.ExternalSubject)
            .WithParam("_handoff_watermark_present", expectedWatermark.HasValue)
            .WithParam("_handoff_watermark", expectedWatermark ?? DateTimeOffset.UnixEpoch));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Member>(memberId.ToString("N"))
            .Where(item => item.IsActive && item.ExternalUserId == row.ExternalSubject
                && item.OwnerTenantId == expectedOwner && item.AuthNotBefore == expectedWatermark)
            .Set(item => item.IsActive, true).Build(), "handoff_member_lock", "_handoff_member_lock"));
        statements.Add(SurrealQuery.Of("IF $_handoff_member_lock = NONE OR $_handoff_member_lock = [] { THROW 'HANDOFF_AUTHORITY_CHANGED' }"));
        statements.Add(SurrealQuery.Of("UPDATE wallet_auth_claim_token SET consumed_at = $_handoff_now WHERE target_user_id = type::record('user', $_handoff_target) AND tenant_id = type::record('user', $_handoff_tenant) AND consumed_at = NONE")
            .WithParam("_handoff_now", row.UpdatedAt).WithParam("_handoff_target", memberId.ToString("N"))
            .WithParam("_handoff_tenant", authority.Credential.ActorUserId.ToString("N")));
        if (previous is null)
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(row), "handoff_create"));
        else
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Handoff>(SurrealRecordGuid.BareId(row.Id))
                .Where(item => item.Version == previous.Version && item.Status == "pending" && item.Descriptor == previous.Descriptor)
                .Set(item => item.Version, row.Version).Set(item => item.TokenDigest, row.TokenDigest)
                .Set(item => item.ProtectedDelivery, row.ProtectedDelivery).Set(item => item.UpdatedAt, row.UpdatedAt).Build(), "handoff_resend", "_handoff_resend"));
            statements.Add(SurrealQuery.Of("IF $_handoff_resend = NONE OR $_handoff_resend = [] { THROW 'HANDOFF_CHANGED' }"));
        }
        if (invite is not null) statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(invite), "handoff_invite"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var result = await executor.ExecuteAsync(SurrealQuery.Combine(statements.ToArray()), ct);
        result.EnsureAllOk();
    }

    public async Task<bool> TransitionAsync(Handoff row, string from, string to, CancellationToken ct)
    {
        var result = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<Handoff>(SurrealRecordGuid.BareId(row.Id))
            .Where(item => item.Version == row.Version && item.Status == from)
            .Set(item => item.Status, to).Set(item => item.Version, row.Version + 1)
            .Set(item => item.UpdatedAt, DateTimeOffset.UtcNow).Build(), ct);
        result.EnsureAllOk();
        return result.GetValues<Handoff>(0).Any();
    }
}
