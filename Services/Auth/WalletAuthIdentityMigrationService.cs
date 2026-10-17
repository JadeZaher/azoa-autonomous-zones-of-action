using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>Migrates canonical authentication identities under a distributed lease.</summary>
public sealed class WalletAuthIdentityMigrationService(ISurrealExecutor executor)
{
    private const string Lost = "WALLET_IDENTITY_MIGRATION_CONFLICT";
    public sealed record Diagnostic(string UserId, string ExpectedIdentity, string Problem);
    public sealed record View(string Status, long Version, IReadOnlyList<Diagnostic> Conflicts);
    private sealed record Audit(Guid Actor, string UserId, string Identity, string Reason, DateTimeOffset At);

    public async Task<View> GetAsync(CancellationToken ct)
    {
        var row = await ReadAsync(ct);
        return new(row?.Status ?? "Pending", row?.Version ?? 0,
            JsonSerializer.Deserialize<Diagnostic[]>(row?.DiagnosticsJson ?? "[]")!);
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        var current = await ReadAsync(ct);
        if (current is null)
        {
            try
            {
                var created = await executor.ExecuteAsync(SurrealWriter.Create(new WalletAuthIdentityMigration { UpdatedAt = DateTimeOffset.UtcNow }), ct);
                created.EnsureAllOk();
            }
            catch (SurrealStatementException ex) when (SurrealIdempotencyLedger.IsUniqueViolation(ex.Message)) { }
            current = await ReadAsync(ct);
        }
        if (current is null || current.Status is "Ready" or "Conflict") return;
        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid().ToString("N");
        var claim = SurrealWriter.UpdateOnly<WalletAuthIdentityMigration>("v1")
            .Where(row => row.Version == current.Version && row.Status != "Ready" && row.Status != "Conflict")
            .Where(row => row.LeaseExpiresAt == null || row.LeaseExpiresAt <= now)
            .Set(row => row.Status, "Migrating").Set(row => row.LeaseToken, token)
            .Set(row => row.LeaseExpiresAt, now.AddMinutes(2)).Set(row => row.Version, current.Version + 1);
        var admission = await executor.ExecuteAsync(claim.Build(), ct);
        admission.EnsureAllOk();
        if (!admission.GetValues<WalletAuthIdentityMigration>(0).Any()) return;

        var page = await executor.QueryAsync<User>(SurrealQuery<User>.From()
            .Where(row => row.AuthWalletAddress != null || row.AuthWalletChainType != null)
            .Where(row => row.AuthIdentityVersion == null || row.AuthIdentityVersion != 1)
            .OrderBy(row => row.Id).Limit(100), ct);
        foreach (var user in page)
        {
            if (!WalletAuthIdentity.TryCreate(user.AuthWalletAddress, user.AuthWalletChainType, out var address, out var chain))
            {
                await SetConflictAsync(token, user, "Invalid stored wallet identity", ct);
                return;
            }
            var update = WalletUserAuthenticationCas.Match(user).Set(row => row.AuthWalletAddress, address)
                .Set(row => row.AuthWalletChainType, chain).Set(row => row.AuthIdentityVersion, 1);
            try
            {
                if (!await CommitUserAsync(token, update.Build(), ct)) return;
            }
            catch (SurrealStatementException ex) when (SurrealIdempotencyLedger.IsUniqueViolation(ex.Message))
            {
                await SetConflictAsync(token, user, "Canonical identity belongs to multiple accounts", ct);
                return;
            }
        }
        // raw: readiness is published only with the leased gate locked and no unversioned bound user.
        var finish = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"), LeaseGuard(token), AssertLease(),
            SurrealQuery.Of("""
                UPDATE wallet_auth_identity_migration:v1 SET status = IF array::len(
                    SELECT id FROM user WHERE (auth_wallet_address != NONE OR auth_wallet_chain_type != NONE)
                    AND (auth_identity_version = NONE OR auth_identity_version != 1) LIMIT 1) = 0
                    { 'Ready' } ELSE { 'Pending' }, lease_token = NONE, lease_expires_at = NONE,
                    version += 1, updated_at = time::now()
                """), SurrealQuery.Of("COMMIT"));
        await ExecuteFencedAsync(finish, ct);
    }

    public async Task<AZOAResult<bool>> ClearConflictAsync(Guid actor, long expectedVersion, string userId,
        string expectedIdentity, string reason, CancellationToken ct)
    {
        if (actor == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            return AZOAResult<bool>.Failure("An operator and bounded audit reason are required.");
        var current = await ReadAsync(ct);
        var diagnostic = JsonSerializer.Deserialize<Diagnostic[]>(current?.DiagnosticsJson ?? "[]")!
            .SingleOrDefault(item => item.UserId == userId && item.ExpectedIdentity == expectedIdentity);
        if (current?.Status != "Conflict" || current.Version != expectedVersion || diagnostic is null)
            return AZOAResult<bool>.Failure("The conflict diagnosis changed; reload before repair.");
        var user = await executor.QuerySingleAsync<User>(SurrealQuery<User>.Key(userId), ct);
        if (user is null || Fingerprint(user) != expectedIdentity)
            return AZOAResult<bool>.Failure("The diagnosed identity changed; no binding was cleared.");
        var audit = JsonSerializer.Deserialize<List<Audit>>(current.AuditJson)!;
        audit.Add(new(actor, userId, expectedIdentity, reason.Trim(), DateTimeOffset.UtcNow));
        var clear = WalletUserAuthenticationCas.Match(user).Unset(row => row.AuthWalletAddress).Unset(row => row.AuthWalletChainType)
            .Set(row => row.AuthIdentityVersion, 1).Build();
        // raw: exact diagnosed binding removal and operator audit/readiness reset form one atomic repair.
        var transaction = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
            SurrealQuery.Of("LET $_migration = (UPDATE wallet_auth_identity_migration:v1 SET status = 'Pending', diagnostics_json = '[]', audit_json = $_audit, version += 1 WHERE status = 'Conflict' AND version = $_expected RETURN AFTER)")
                .WithParam("_audit", JsonSerializer.Serialize(audit)).WithParam("_expected", expectedVersion), AssertLease(),
            ExactMutation(clear), AssertUser(), SurrealQuery.Of("COMMIT"));
        return await ExecuteFencedAsync(transaction, ct) ? AZOAResult<bool>.Success(true)
            : AZOAResult<bool>.Failure("The identity changed concurrently; reload before repair.");
    }

    private Task<WalletAuthIdentityMigration?> ReadAsync(CancellationToken ct)
        => executor.QuerySingleAsync<WalletAuthIdentityMigration>(SurrealQuery<WalletAuthIdentityMigration>.Key("v1"), ct);

    private async Task SetConflictAsync(string token, User user, string problem, CancellationToken ct)
    {
        var diagnostics = new List<Diagnostic> { new(user.Id, Fingerprint(user), problem) };
        if (WalletAuthIdentity.TryCreate(user.AuthWalletAddress, user.AuthWalletChainType, out var address, out var chain))
        {
            for (var offset = 0; ; offset += 100)
            {
                var renewed = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<WalletAuthIdentityMigration>("v1")
                    .Where(row => row.Status == "Migrating" && row.LeaseToken == token && row.LeaseExpiresAt > DateTimeOffset.UtcNow)
                    .Set(row => row.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(2)).Build(), ct);
                renewed.EnsureAllOk();
                if (!renewed.GetValues<WalletAuthIdentityMigration>(0).Any()) return;
                var page = await executor.QueryAsync<User>(SurrealQuery<User>.From()
                    .Where(row => row.AuthWalletAddress != null || row.AuthWalletChainType != null)
                    .OrderBy(row => row.Id).Start(offset).Limit(100), ct);
                foreach (var candidate in page)
                    if (candidate.Id != user.Id && WalletAuthIdentity.TryCreate(candidate.AuthWalletAddress, candidate.AuthWalletChainType,
                            out var candidateAddress, out var candidateChain) && candidateAddress == address && candidateChain == chain)
                        diagnostics.Add(new(candidate.Id, Fingerprint(candidate), problem));
                if (page.Count < 100) break;
            }
        }
        var current = await ReadAsync(ct);
        if (current is null) return;
        var now = DateTimeOffset.UtcNow;
        var response = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<WalletAuthIdentityMigration>("v1")
            .Where(row => row.Version == current.Version && row.Status == "Migrating" && row.LeaseToken == token && row.LeaseExpiresAt > now)
            .Set(row => row.Status, "Conflict").Set(row => row.DiagnosticsJson,
                JsonSerializer.Serialize(diagnostics))
            .Unset(row => row.LeaseToken).Unset(row => row.LeaseExpiresAt).Set(row => row.UpdatedAt, now)
            .Set(row => row.Version, current.Version + 1).Build(), ct);
        response.EnsureAllOk();
    }

    private Task<bool> CommitUserAsync(string token, SurrealQuery update, CancellationToken ct)
        // raw: lease ownership and exact user authority must hold in the same transaction as canonicalization.
        => ExecuteFencedAsync(SurrealQuery.Combine(SurrealQuery.Of("BEGIN"), LeaseGuard(token), AssertLease(),
            ExactMutation(update), AssertUser(), SurrealQuery.Of("COMMIT")), ct);

    private async Task<bool> ExecuteFencedAsync(SurrealQuery transaction, CancellationToken ct)
    {
        try { var result = await executor.ExecuteAsync(transaction, ct); result.EnsureAllOk(); return true; }
        catch (SurrealStatementException ex) when (ex.Message.Contains(Lost, StringComparison.Ordinal)) { return false; }
    }

    private static string Fingerprint(User user) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { user.Id, user.AuthWalletAddress, user.AuthWalletChainType, user.OwnerTenantId, user.AuthNotBefore }))));

    // raw: these guards are used only inside the atomic lease/user or repair transactions above.
    private static SurrealQuery LeaseGuard(string token) => SurrealQuery.Of("LET $_migration = (UPDATE wallet_auth_identity_migration:v1 SET updated_at = time::now() WHERE status = 'Migrating' AND lease_token = $_migration_token AND lease_expires_at > time::now() RETURN AFTER)").WithParam("_migration_token", token);
    private static SurrealQuery AssertLease() => SurrealQuery.Of($"IF array::len($_migration) != 1 {{ THROW '{Lost}' }}");
    private static SurrealQuery ExactMutation(SurrealQuery query) => SurrealQuery.Of($"LET $_identity = ({query.Sql.TrimEnd(';')})").WithParams(query.Params);
    private static SurrealQuery AssertUser() => SurrealQuery.Of($"IF $_identity = NONE OR $_identity = NULL {{ THROW '{Lost}' }}");
}
