using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Command = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentLifecycleCommandReceipt;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealConsentLifecycleStore
{
    public async Task<ConsentCommandWithdrawal?> WithdrawAsync(ConsentLifecycleCommand input,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (credential.Kind != "Jwt" || credential.TokenUse != AzoaClaims.TokenUseLogin
            || credential.TenantId is not null || !credential.OwnerAuthority) return null;
        var guards = SigningAuthorityFence.ForRunAdmission(credential, "consent_withdraw");
        if (guards is null) return null;
        var command = ConsentLifecycleIdentity.Normalize(input);
        var id = ConsentLifecycleIdentity.CommandId(credential.ActorUserId, command.RequestId);
        var hash = CanonicalJson.Hash(command);
        var prior = await executor.QuerySingleAsync<Command>(SurrealQuery<Command>.Key(id), ct);
        if (prior is not null) return Withdrawal(prior, command.RequestId, hash);
        var tombstone = new Command { Id = id, GrantorId = credential.ActorUserId.ToString("N"),
            TenantId = command.TenantId.ToString("N"), IntentHash = hash, Withdrawn = true,
            AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(credential)), CreatedAt = DateTimeOffset.UtcNow };
        // raw: current owner authority and command tombstone compete atomically with grant admission.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(tombstone), "consent_withdraw_command"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct); response.EnsureAllOk(); return response;
            }, ct);
            return new(command.RequestId, true, null);
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await executor.QuerySingleAsync<Command>(SurrealQuery<Command>.Key(id), ct);
            return prior is null ? null : Withdrawal(prior, command.RequestId, hash);
        }
    }

    private static ConsentCommandWithdrawal? Withdrawal(Command prior, Guid requestId, string hash)
        => prior.IntentHash != hash ? null : prior.Withdrawn ? new(requestId, true, null)
            : new(requestId, false, Replay(prior, hash));
}
