using System.Security.Claims;
using System.Globalization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using ApiKeyRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ApiKey;
using GrantRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;

namespace AZOA.WebAPI.Services.Signing;

/// <summary>Captures authenticated credential and exact wallet/grant witnesses for transactional authorization.</summary>
public sealed class SigningAdmissionAuthorityFactory(IUserStore users, IWalletStore wallets, ISurrealExecutor executor)
{
    public async Task<AZOAResult<SigningConsentWitness>> BindConsentAsync(SigningCredentialWitness credential,
        IReadOnlyList<string> requiredScopes, CancellationToken ct = default)
    {
        var selected = await SelectGrantsAsync(credential, requiredScopes, ct);
        return selected.IsError || selected.Result is null ? AZOAResult<SigningConsentWitness>.Failure(selected.Message)
            : AZOAResult<SigningConsentWitness>.Success(new(1, credential, requiredScopes.ToArray(), selected.Result));
    }

    public async Task<AZOAResult<SigningCredentialWitness>> CaptureCredentialAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (principal.Identity?.IsAuthenticated != true || !AzoaClaims.TryGetSubjectId(principal, out var actor) || actor == Guid.Empty)
            return AZOAResult<SigningCredentialWitness>.Failure("An authenticated signing principal is required.");
        var user = await users.GetByIdAsync(actor, ct);
        if (user.IsError || user.Result is not { IsActive: true }) return AZOAResult<SigningCredentialWitness>.Failure("The signing user is unavailable.");
        var watermark = Utc(user.Result.AuthNotBefore);
        var tenant = principal.GetActingTenantId();
        if (principal.FindFirst("AuthMethod")?.Value == "ApiKey")
        {
            if (tenant is not null || !Guid.TryParse(principal.FindFirst("ApiKeyId")?.Value, out var id))
                return AZOAResult<SigningCredentialWitness>.Failure("API-key authority cannot assert another tenant.");
            var key = await executor.QuerySingleAsync<ApiKeyRow>(SurrealQuery<ApiKeyRow>.Key(id.ToString("N")), ct);
            if (key is null || SurrealRecordGuid.Parse(key.UserId) != actor || !key.IsActive || key.RevokedAt is not null
                || key.ExpiresAt <= DateTimeOffset.UtcNow || principal.FindFirst("ScopesRestricted")?.Value == "true")
                return AZOAResult<SigningCredentialWitness>.Failure("The original API key is no longer authorized.");
            var currentScopes = key.Scopes.Where(AzoaScopes.IsApiKeyIssuableScope).Distinct(StringComparer.Ordinal).ToArray();
            var authenticatedScopes = principal.GetScopes().Where(AzoaScopes.IsApiKeyIssuableScope).ToArray();
            var ownerAuthority = key.Scopes.Count == 0 && authenticatedScopes.Length == 0;
            var scopes = (key.Scopes.Count == 0 ? authenticatedScopes : authenticatedScopes.Length == 0 ? currentScopes
                : currentScopes.Intersect(authenticatedScopes, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
            if (!ownerAuthority && scopes.Length == 0) return AZOAResult<SigningCredentialWitness>.Failure("The API key has no allowed original scopes.");
            return AZOAResult<SigningCredentialWitness>.Success(new(1, "ApiKey", actor, null, watermark, null, key.ExpiresAt,
                id, key.KeyHash, scopes, ownerAuthority, key.Scopes.ToArray()));
        }
        var kind = principal.FindFirst(AzoaClaims.TokenUse)?.Value;
        if (!ValidatedTokenTime.TryRead(principal, out var issued, out var expires) || issued > DateTimeOffset.UtcNow
            || expires <= DateTimeOffset.UtcNow || AuthWatermark.IsTokenStale(issued.UtcDateTime, user.Result.AuthNotBefore)
            || (kind == AzoaClaims.TokenUseChild) != (tenant is not null)
            || kind is not (AzoaClaims.TokenUseLogin or AzoaClaims.TokenUseChild or AzoaClaims.TokenUseNodeOperator))
            return AZOAResult<SigningCredentialWitness>.Failure("The JWT signing authority is incomplete, expired or revoked.");
        long? operatorRevision = null, operatorSession = null;
        if (kind == AzoaClaims.TokenUseNodeOperator)
        {
            if (!long.TryParse(principal.FindFirst(AzoaClaims.OperatorRevision)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
                || !long.TryParse(principal.FindFirst(AzoaClaims.OperatorSessionRevision)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var session)
                || actor != AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.UserId)
                return AZOAResult<SigningCredentialWitness>.Failure("Operator authority requires its exact credential revisions.");
            operatorRevision = revision;
            operatorSession = session;
        }
        return AZOAResult<SigningCredentialWitness>.Success(new(1, "Jwt", actor, tenant, watermark, issued, expires,
            null, null, principal.GetScopes().Order(StringComparer.Ordinal).ToArray(), kind == AzoaClaims.TokenUseLogin,
            TokenUse: kind, OperatorCredentialRevision: operatorRevision, OperatorSessionRevision: operatorSession));
    }

    public async Task<AZOAResult<SigningAdmissionAuthority>> BindDirectAsync(SigningCredentialWitness credential, Guid walletId,
        string chain, string address, IReadOnlyList<string> requiredScopes, CancellationToken ct = default)
    {
        var grants = await SelectGrantsAsync(credential, requiredScopes, ct);
        if (grants.IsError || grants.Result is null) return AZOAResult<SigningAdmissionAuthority>.Failure(grants.Message);
        var wallet = await WalletAsync(credential.ActorUserId, walletId, chain, address, ct);
        return wallet is null ? AZOAResult<SigningAdmissionAuthority>.Failure("The signing wallet binding is unavailable.")
            : AZOAResult<SigningAdmissionAuthority>.Success(new(1, credential.Kind == "Jwt" ? "DirectJwt" : "DirectApiKey",
                credential, wallet, requiredScopes.ToArray(), grants.Result));
    }

    public async Task<AZOAResult<WorkflowSigningWitness>> SelectWorkflowAsync(SigningCredentialWitness credential,
        ActionEffectAuthority action, IReadOnlyList<string> requiredScopes, CancellationToken ct = default)
    {
        if (action.ActorUserId != credential.ActorUserId || action.ActingTenantId != credential.TenantId)
            return AZOAResult<WorkflowSigningWitness>.Failure("Workflow credential and actor differ.");
        var scopes = requiredScopes.Append(AzoaScopes.ActionExecute).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var selected = await SelectGrantsAsync(credential, scopes, ct);
        return selected.IsError || selected.Result is null ? AZOAResult<WorkflowSigningWitness>.Failure(selected.Message)
            : AZOAResult<WorkflowSigningWitness>.Success(new(1, credential, action, scopes, selected.Result));
    }

    public async Task<AZOAResult<SigningAdmissionAuthority>> BindWorkflowAsync(WorkflowSigningWitness witness, Guid walletId,
        string chain, string address, CancellationToken ct = default)
    {
        var wallet = await WalletAsync(witness.Credential.ActorUserId, walletId, chain, address, ct);
        return wallet is null ? AZOAResult<SigningAdmissionAuthority>.Failure("The original workflow wallet binding is unavailable.")
            : AZOAResult<SigningAdmissionAuthority>.Success(new(1, "Workflow", witness.Credential, wallet,
                witness.RequiredScopes, witness.Grants, witness.Action, witness.AuthorizationId));
    }

    private async Task<SigningWalletWitness?> WalletAsync(Guid actor, Guid id, string chain, string address, CancellationToken ct)
    {
        var row = await wallets.GetByIdAsync(id, ct);
        return row.IsError || row.Result is null || row.Result.UserId != actor || !row.Result.ChainType.Equals(chain, StringComparison.OrdinalIgnoreCase)
            || !ChainAddressIdentity.Equivalent(chain, row.Result.Address, address) ? null
            : new(id, actor, row.Result.Address, row.Result.ChainType);
    }

    private async Task<AZOAResult<IReadOnlyList<SigningGrantWitness>>> SelectGrantsAsync(SigningCredentialWitness credential,
        IReadOnlyList<string> scopes, CancellationToken ct)
    {
        if (credential.Version != 1 || scopes.Count == 0 || scopes.Any(string.IsNullOrWhiteSpace)
            || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Count
            || (!credential.OwnerAuthority && scopes.Any(scope => !credential.Scopes.Contains(scope, StringComparer.Ordinal))))
            return AZOAResult<IReadOnlyList<SigningGrantWitness>>.Failure("Required scopes exceed the original credential authorization.");
        if (credential.TenantId is null) return AZOAResult<IReadOnlyList<SigningGrantWitness>>.Success([]);
        var actor = SurrealLink.ToLink("user", credential.ActorUserId.ToString("N"))!;
        var tenant = SurrealLink.ToLink("user", credential.TenantId.Value.ToString("N"))!;
        var now = DateTimeOffset.UtcNow;
        var grants = await executor.QueryAsync<GrantRow>(SurrealQuery<GrantRow>.From()
            .Where(row => row.GrantorUserId == actor && row.TenantId == tenant && row.Origin == "UserExplicit"
                && row.RevokedAt == null && (row.ExpiresAt == null || row.ExpiresAt > now)).OrderBy(row => row.Id), ct);
        var selected = new List<SigningGrantWitness>();
        foreach (var scope in scopes)
        {
            var grant = grants.FirstOrDefault(row => row.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(scope, StringComparer.Ordinal));
            if (grant is null) return AZOAResult<IReadOnlyList<SigningGrantWitness>>.Failure($"An explicit live grant for '{scope}' is required.");
            selected.Add(new(SurrealRecordGuid.Parse(grant.Id), scope, grant.Scopes, grant.GrantedAt, grant.ExpiresAt));
        }
        return AZOAResult<IReadOnlyList<SigningGrantWitness>>.Success(selected);
    }

    private static DateTimeOffset? Utc(DateTime? value) => value is { } date ? new(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null;
}
