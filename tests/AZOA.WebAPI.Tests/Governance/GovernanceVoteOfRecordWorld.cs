// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using KycIdentity = AZOA.WebAPI.Persistence.SurrealDb.Models.KycIdentity;
using PersonOperationClaim = AZOA.WebAPI.Persistence.SurrealDb.Models.PersonOperationClaim;

namespace AZOA.WebAPI.Tests.Governance;

/// <summary>Clock the whole world shares, so a test can step past a proposal's close.</summary>
internal sealed class SteppableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>The node-operator-only person-to-user binding, keyed exactly as the gate writes it.</summary>
internal sealed class InMemoryKycIdentityStore : IKycIdentityStore
{
    private readonly Dictionary<(string TenantScope, string UserKey), KycIdentity> _bindings = new();

    /// <summary>
    /// Registers a verified human: one user, one person key, inside one tenant scope.
    /// <paramref name="globalPersonKey"/> is the SAME human seen node-wide, so two
    /// bindings in two tenant scopes that share it are one person across the node.
    /// </summary>
    public void Bind(
        string tenantScope,
        Guid userId,
        string personKey,
        string? globalPersonKey = null,
        DateTimeOffset? erasedAt = null) =>
        _bindings[(tenantScope, userId.ToString("N"))] = new KycIdentity
        {
            TenantScope = tenantScope,
            PersonKey = personKey,
            // Same derivation the tally's roll and the gate both assume.
            GlobalPersonKey = globalPersonKey ?? "global:" + personKey,
            ErasedAt = erasedAt,
        };

    public Task<AZOAResult<KycIdentity?>> GetByUserAsync(string tenantScope, string userId, CancellationToken ct = default)
        => Task.FromResult(new AZOAResult<KycIdentity?>
        {
            Result = _bindings.TryGetValue((tenantScope, userId), out var identity) ? identity : null,
        });

    public Task<AZOAResult<KycIdentity>> BindVerifiedUserAsync(
        KycIdentity candidate, KycSubmission expected, HostedSession lease, CancellationToken ct = default)
        => BindUserAsync(candidate, ct);

    public Task<AZOAResult<KycIdentity>> BindUserAsync(KycIdentity candidate, CancellationToken ct = default)
        => throw new NotSupportedException("Bindings are seeded directly in these tests.");

    public Task<AZOAResult<KycIdentity>> GetByPersonKeyAsync(string tenantScope, string personKey, CancellationToken ct = default)
        => throw new NotSupportedException("Unused by the governance surface.");

    public Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetByGlobalPersonKeyAsync(string globalPersonKey, CancellationToken ct = default)
        => throw new NotSupportedException("Unused by the governance surface.");

    public Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetErasureDueAsync(DateTimeOffset asOf, int limit, CancellationToken ct = default)
        => throw new NotSupportedException("Unused by the governance surface.");

    public Task<AZOAResult<KycIdentity>> EraseAsync(string recordId, string reason, DateTimeOffset erasedAt, CancellationToken ct = default)
        => throw new NotSupportedException("Unused by the governance surface.");
}

/// <summary>
/// The per-operation Sybil ledger. A CONCRETE fake rather than a Moq mock on purpose:
/// when D39 adds the read the tally needs (the option carried on the claim, and a
/// list-by-operation), a mock would auto-implement it and quietly answer "nothing",
/// turning a missing implementation into a green test.
/// </summary>
internal sealed class InMemoryPersonOperationClaimLedger : IPersonOperationClaimStore
{
    private readonly List<PersonOperationClaim> _rows = new();

    public IReadOnlyList<PersonOperationClaim> Rows => _rows;

    /// <summary>
    /// When set, every list-by-operation read fails. The freeze must refuse a rewrite it
    /// cannot prove is unclaimed, so this is the difference between a control and a
    /// control-shaped comment.
    /// </summary>
    public string? ListFailure { get; set; }

    /// <summary>
    /// Winner decided INSIDE the one write and read back by the caller — never by a
    /// preceding read, which two concurrent claimants would both pass.
    /// </summary>
    public Task<AZOAResult<PersonOperationClaim>> TryClaimAsync(PersonOperationClaim candidate, CancellationToken ct = default)
    {
        var existing = _rows.FirstOrDefault(r => string.Equals(r.Id, candidate.Id, StringComparison.Ordinal));
        if (existing is null)
        {
            _rows.Add(candidate);
            return Task.FromResult(new AZOAResult<PersonOperationClaim> { Result = candidate });
        }

                var ours = string.Equals(existing.UserId, candidate.UserId, StringComparison.Ordinal);
        var unheld = string.IsNullOrEmpty(existing.UserId) || existing.ReleasedAt is not null;

        if (unheld)
        {
            existing.UserId = candidate.UserId;
            existing.TenantId = candidate.TenantId;
            existing.ClaimedAt = candidate.ClaimedAt;
            existing.ReleasedAt = null;
        }

        // D40: last write wins, IN PLACE, while the window is open. The close is
        // pinned write-once so re-opening a proposal cannot re-open a decided row.
        if ((unheld || ours) && !string.IsNullOrWhiteSpace(candidate.DecisionOption))
        {
            var closesAt = existing.DecisionClosesAt ?? candidate.DecisionClosesAt;
            if (closesAt is null || candidate.ClaimedAt < closesAt)
            {
                existing.DecisionOption = candidate.DecisionOption;
                existing.DecidedAt = candidate.ClaimedAt;
                existing.DecisionClosesAt ??= candidate.DecisionClosesAt;
            }
        }

        return Task.FromResult(new AZOAResult<PersonOperationClaim> { Result = existing });
    }
    /// <summary>D39: every live (unreleased) claim in one uniqueness domain.</summary>
    public Task<AZOAResult<IReadOnlyList<PersonOperationClaim>>> ListLiveByOperationAsync(
        string uniquenessScope, string operationKey, CancellationToken ct = default)
        => ListFailure is { } failure
        ? Task.FromResult(new AZOAResult<IReadOnlyList<PersonOperationClaim>> { IsError = true, Message = failure })
        : Task.FromResult(new AZOAResult<IReadOnlyList<PersonOperationClaim>>
        {
            Result = _rows
                .Where(r => string.Equals(r.UniquenessScope, uniquenessScope, StringComparison.Ordinal)
                    && string.Equals(r.OperationKey, operationKey, StringComparison.Ordinal)
                    && r.ReleasedAt is null)
                .ToList(),
        });


    public Task<AZOAResult<PersonOperationClaim?>> GetAsync(
        string uniquenessScope, string operationKey, string personKey, CancellationToken ct = default)
        => Task.FromResult(new AZOAResult<PersonOperationClaim?>
        {
            Result = _rows.FirstOrDefault(r =>
                string.Equals(r.UniquenessScope, uniquenessScope, StringComparison.Ordinal)
                && string.Equals(r.OperationKey, operationKey, StringComparison.Ordinal)
                && string.Equals(r.PersonKey, personKey, StringComparison.Ordinal)),
        });

    public Task<AZOAResult<bool>> ReleaseAsync(
        string uniquenessScope,
        string operationKey,
        string personKey,
        string userId,
        DateTimeOffset releasedAt,
        CancellationToken ct = default)
    {
        var row = _rows.FirstOrDefault(r =>
            string.Equals(r.UniquenessScope, uniquenessScope, StringComparison.Ordinal)
            && string.Equals(r.OperationKey, operationKey, StringComparison.Ordinal)
            && string.Equals(r.PersonKey, personKey, StringComparison.Ordinal)
            && string.Equals(r.UserId, userId, StringComparison.Ordinal)
            && r.ReleasedAt is null);

        if (row is null)
            return Task.FromResult(new AZOAResult<bool> { Result = false });

        row.ReleasedAt = releasedAt;
        return Task.FromResult(new AZOAResult<bool> { Result = true });
    }
}
