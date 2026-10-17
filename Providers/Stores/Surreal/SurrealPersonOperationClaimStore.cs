// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealPersonOperationClaimStore : IPersonOperationClaimStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealPersonOperationClaimStore(ISurrealExecutor executor, ILogger<SurrealPersonOperationClaimStore>? logger = null)
    {
        _executor = executor;
    }

    private static bool ValidDomain(string scope, string operation, string person)
        => !string.IsNullOrWhiteSpace(scope) && !string.IsNullOrWhiteSpace(operation) && !string.IsNullOrWhiteSpace(person);

    private static bool Election(string operationKey) => operationKey.Trim().StartsWith("governance:proposal:", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public async Task<AZOAResult<PersonOperationClaim>> TryClaimAsync(
        PersonOperationClaim candidate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!ValidDomain(candidate.UniquenessScope, candidate.OperationKey, candidate.PersonKey))
            return AZOAResult<PersonOperationClaim>.Failure("A scope, operation and person key are required.");
        if (candidate.Id != PersonOperationClaim.RecordIdFor(candidate.UniquenessScope, candidate.OperationKey, candidate.PersonKey))
            return AZOAResult<PersonOperationClaim>.Failure("Claim identity does not match its immutable domain.");
        if (Election(candidate.OperationKey))
            return AZOAResult<PersonOperationClaim>.Failure("Election claims require an authenticated governance command.");
        if (string.IsNullOrWhiteSpace(candidate.UserId))
            return AZOAResult<PersonOperationClaim>.Failure("A user is required to claim an operation.");

        var body = ToStorage(candidate);
        var stamp = body.ModifiedDate ?? DateTimeOffset.UtcNow;
        var decision = !string.IsNullOrEmpty(body.DecisionOption);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var expected = await _executor.QuerySingleAsync<PersonOperationClaim>(SurrealQuery<PersonOperationClaim>.Key(body.Id), ct);
            var writable = expected is null || string.IsNullOrEmpty(expected.UserId) || expected.UserId == body.UserId || expected.ReleasedAt is not null;
            SurrealQuery mutation;
            if (expected is null)
            {
                body.Version = 1;
                body.ClaimedAt = stamp;
                body.ReleasedAt = null;
                body.ModifiedDate = stamp;
                body.DecisionOption = decision ? body.DecisionOption : null;
                body.DecidedAt = decision ? stamp : null;
                body.DecisionClosesAt = decision ? body.DecisionClosesAt ?? PersonOperationClaim.NeverClosesAt : null;
                mutation = SurrealWriter.Create(body);
            }
            else if (writable)
            {
                var update = PersonOperationClaimFence.Update(expected)
                    .Set(row => row.UserId, body.UserId!).Unset(row => row.ReleasedAt).Set(row => row.ModifiedDate, stamp);
                update = body.TenantId is null ? update.Unset(row => row.TenantId) : update.Set(row => row.TenantId, body.TenantId);
                if (string.IsNullOrEmpty(expected.UserId) || expected.ReleasedAt is not null) update = update.Set(row => row.ClaimedAt, stamp);
                if (decision)
                {
                    update = update.Set(row => row.DecisionOption, body.DecisionOption!).Set(row => row.DecidedAt, stamp);
                    if (expected.DecisionClosesAt is null)
                        update = update.Set(row => row.DecisionClosesAt, body.DecisionClosesAt ?? PersonOperationClaim.NeverClosesAt);
                }
                mutation = update.Build();
            }
            else mutation = SurrealQuery<PersonOperationClaim>.Key(body.Id);
            var committed = await CommitAsync(body, expected, decision && writable, mutation, ct);
            if (committed.Code == PersonOperationClaimFence.Changed || (!committed.IsError && writable && committed.Result is null)) continue;
            if (committed.IsError) return AZOAResult<PersonOperationClaim>.FailureWithCode(committed.Message, committed.Code!);
            if (committed.Result is not null) return AZOAResult<PersonOperationClaim>.Success(FromStorage(committed.Result));
        }
        return AZOAResult<PersonOperationClaim>.FailureWithCode("The claim changed repeatedly; retry the operation.", "Conflict");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<PersonOperationClaim?>> GetAsync(
        string uniquenessScope,
        string operationKey,
        string personKey,
        CancellationToken ct = default)
    {
        if (!ValidDomain(uniquenessScope, operationKey, personKey))
            return AZOAResult<PersonOperationClaim?>.Failure("A scope, operation and person key are required.");
        var recordId = PersonOperationClaim.RecordIdFor(uniquenessScope, operationKey, personKey);
        var query = SurrealQuery<PersonOperationClaim>.Key(recordId);

        var row = await _executor.QuerySingleAsync<PersonOperationClaim>(query, ct);
        return new AZOAResult<PersonOperationClaim?>
        {
            Message = row is null ? "No claim found." : "Success",
            Result = row is null ? null : FromStorage(row),
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<PersonOperationClaim>>> ListLiveByOperationAsync(
        string uniquenessScope,
        string operationKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uniquenessScope) || string.IsNullOrWhiteSpace(operationKey))
            return AZOAResult<IReadOnlyList<PersonOperationClaim>>.Failure(
                "A uniqueness scope and an operation key are required to list claims.");

        var query = SurrealQuery<PersonOperationClaim>.From()
            .Where(row => row.UniquenessScope == uniquenessScope && row.OperationKey == operationKey && row.ReleasedAt == null)
            .Select(row => new { row.UserId, row.ReleasedAt, row.DecisionOption, row.DecidedAt, row.DecisionClosesAt });

        var rows = await _executor.QueryAsync<PersonOperationClaim>(query, ct);

        return AZOAResult<IReadOnlyList<PersonOperationClaim>>.Success(rows, "Success");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> ReleaseAsync(
        string uniquenessScope,
        string operationKey,
        string personKey,
        string userId,
        DateTimeOffset releasedAt,
        CancellationToken ct = default)
    {
        if (!ValidDomain(uniquenessScope, operationKey, personKey))
            return AZOAResult<bool>.Failure("A scope, operation and person key are required.");
        if (Election(operationKey)) return AZOAResult<bool>.Failure("Election decisions cannot be released through generic claim APIs.");
        if (string.IsNullOrWhiteSpace(userId))
            return AZOAResult<bool>.Success(false, "Nothing to release.");

        var identity = new PersonOperationClaim { Id = PersonOperationClaim.RecordIdFor(uniquenessScope, operationKey, personKey),
            UniquenessScope = uniquenessScope, OperationKey = operationKey, PersonKey = personKey };
        var stamp = releasedAt.ToUniversalTime();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var expected = await _executor.QuerySingleAsync<PersonOperationClaim>(SurrealQuery<PersonOperationClaim>.Key(identity.Id), ct);
            var writable = expected is not null && expected.UserId == userId && expected.ReleasedAt is null;
            SurrealQuery mutation = writable ? PersonOperationClaimFence.Update(expected!)
                .Set(row => row.ReleasedAt, stamp).Set(row => row.ModifiedDate, stamp).Build()
                : SurrealQuery<PersonOperationClaim>.Key(identity.Id);
            var committed = await CommitAsync(identity, expected, false, mutation, ct);
            if (committed.Code == PersonOperationClaimFence.Changed || (!committed.IsError && writable && committed.Result is null)) continue;
            if (committed.Code == PersonOperationClaimFence.Closed) return AZOAResult<bool>.Success(false, "The claim decision window is closed.");
            if (committed.IsError) return AZOAResult<bool>.FailureWithCode(committed.Message, committed.Code!);
            return AZOAResult<bool>.Success(writable, writable ? "Released." : "Nothing to release.");
        }
        return AZOAResult<bool>.FailureWithCode("The claim changed repeatedly; retry the operation.", "Conflict");
    }

    private async Task<AZOAResult<PersonOperationClaim?>> CommitAsync(PersonOperationClaim identity, PersonOperationClaim? expected,
        bool decision, SurrealQuery mutation, CancellationToken ct)
    {
        var guard = PersonOperationClaimFence.Create(identity, expected, decision);
        var query = SurrealQuery.Combine([SurrealQuery.Of("BEGIN"), ..guard,
            SurrealGeneratedStatementScope.Apply(mutation, "claim_mutation"), SurrealQuery.Of("COMMIT")]);
        try
        {
            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(query, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);
            return AZOAResult<PersonOperationClaim?>.Success(response.GetValues<PersonOperationClaim>(guard.Count + 1).SingleOrDefault());
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(PersonOperationClaimFence.Changed, StringComparison.Ordinal)
            || SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return AZOAResult<PersonOperationClaim?>.FailureWithCode("The claim changed; retry its current state.", PersonOperationClaimFence.Changed);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(PersonOperationClaimFence.Closed, StringComparison.Ordinal))
        {
            return AZOAResult<PersonOperationClaim?>.FailureWithCode("The claim decision window is closed.", PersonOperationClaimFence.Closed);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(PersonOperationClaimFence.Invalid, StringComparison.Ordinal))
        {
            return AZOAResult<PersonOperationClaim?>.FailureWithCode("The stored claim needs operator investigation.", PersonOperationClaimFence.Invalid);
        }
    }

    private static PersonOperationClaim ToStorage(PersonOperationClaim claim) => new()
    {
        Id = claim.Id,
        Version = claim.Version,
        UniquenessScope = claim.UniquenessScope,
        OperationKey = claim.OperationKey,
        PersonKey = claim.PersonKey,
        UserId = claim.UserId,
        TenantId = string.IsNullOrWhiteSpace(claim.TenantId)
            ? null
            : SurrealLink.ToLink("user", claim.TenantId),
        ClaimedAt = claim.ClaimedAt?.ToUniversalTime(),
        ReleasedAt = claim.ReleasedAt?.ToUniversalTime(),
        DecisionOption = claim.DecisionOption,
        DecidedAt = claim.DecidedAt?.ToUniversalTime(),
        DecisionClosesAt = claim.DecisionClosesAt?.ToUniversalTime(),
        CreatedDate = claim.CreatedDate.ToUniversalTime(),
        ModifiedDate = claim.ModifiedDate?.ToUniversalTime(),
    };

    private static PersonOperationClaim FromStorage(PersonOperationClaim claim) => new()
    {
        Id = claim.Id,
        Version = claim.Version,
        UniquenessScope = claim.UniquenessScope,
        OperationKey = claim.OperationKey,
        PersonKey = claim.PersonKey,
        UserId = claim.UserId,
        TenantId = string.IsNullOrWhiteSpace(claim.TenantId)
            ? null
            : SurrealLink.FromLink(claim.TenantId),
        ClaimedAt = claim.ClaimedAt,
        ReleasedAt = claim.ReleasedAt,
        DecisionOption = claim.DecisionOption,
        DecidedAt = claim.DecidedAt,
        // The never-closes sentinel is a storage detail; the domain says "no close".
        DecisionClosesAt = PersonOperationClaim.IsNeverCloses(claim.DecisionClosesAt)
            ? null
            : claim.DecisionClosesAt,
        CreatedDate = claim.CreatedDate,
        ModifiedDate = claim.ModifiedDate,
    };
}
