using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealPlatformReserveLedgerStore(ISurrealExecutor executor) : IPlatformReserveLedgerStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<PlatformReserveLedger?>> GetAsync(string recordId, CancellationToken ct = default)
        => new() { Result = await executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(recordId), ct) };

    /// <inheritdoc/>
    public async Task<AZOAResult<PlatformReserveLedgerClaim>> CreateIfAbsentAsync(PlatformReserveLedger row, CancellationToken ct = default)
    {
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(row), ct);
            response.EnsureAllOk();
            return AZOAResult<PlatformReserveLedgerClaim>.Success(new(response.GetValues<PlatformReserveLedger>(0).Single(), true));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            var existing = await GetAsync(row.Id, ct);
            return existing.Result is { } saved && ReserveAssetRegistrationPlan.SameIdentity(saved, row)
                ? AZOAResult<PlatformReserveLedgerClaim>.Success(new(saved, false))
                : AZOAResult<PlatformReserveLedgerClaim>.Failure("Reserve asset identity conflicts with the existing registration.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<PlatformReserveLedger>> UpdateIfVersionAsync(PlatformReserveLedger row, long expectedVersion, CancellationToken ct = default)
    {
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || row.Version != expectedVersion + 1)
            return AZOAResult<PlatformReserveLedger>.Failure("Reserve update requires the next ledger version.");
        var mutation = SurrealWriter.UpdateOnly<PlatformReserveLedger>(row.Id)
            .Where(saved => saved.Version == expectedVersion)
            .Set(saved => saved.Version, row.Version).Set(saved => saved.UpdatedAt, row.UpdatedAt);
        mutation = row.Partitions is { } partitions ? mutation.Set(saved => saved.Partitions, partitions) : mutation.Unset(saved => saved.Partitions);
        mutation = row.RecentDrawKeys is { } keys ? mutation.Set(saved => saved.RecentDrawKeys, keys) : mutation.Unset(saved => saved.RecentDrawKeys);
        var response = await executor.ExecuteAsync(mutation.Build(), ct);
        response.EnsureAllOk();
        var persisted = response.GetValues<PlatformReserveLedger>(0).SingleOrDefault();
        return persisted is null
            ? AZOAResult<PlatformReserveLedger>.Failure("Reserve ledger changed during this update.")
            : AZOAResult<PlatformReserveLedger>.Success(persisted);
    }
}
