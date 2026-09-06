using System.Text.Json;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed implementation of <see cref="IBlockchainOperationStore"/>.
/// Translates between the legacy <see cref="BlockchainOperation"/> domain model
/// and the generated <see cref="OperationLog"/> POCO (table: operation_log).
///
/// Mapping notes:
///   - BlockchainOperation.Id (Guid)        → OperationLog.Id (string, "N" format, lowercase)
///   - BlockchainOperation.UserId (Guid?) → OperationLog.UserId (string?, "N" format)
///   - BlockchainOperation.WalletId (Guid?) → OperationLog.WalletId (string?, "N" format)
///   - BlockchainOperation.Amount (RailAmount) → OperationLog.Amount (string?, decimal base units)
///   - BlockchainOperation.ValueStamp (D16)  → OperationLog.Value* columns (all-null = unpriced)
///   - BlockchainOperation.Parameters (Dictionary) → OperationLog.Parameters (JsonElement?)
///   - BlockchainOperation.CreatedDate (DateTime UTC) → OperationLog.CreatedDate (DateTimeOffset UTC)
///   - BlockchainOperation.CompletedDate (DateTime?) → OperationLog.CompletedDate (DateTimeOffset?)
///   - BlockchainOperation.Status (string)  → OperationLog.Status (OperationLog.StatusKind enum)
///   - BlockchainOperation.IdempotencyKey (string?) → OperationLog.IdempotencyKey (safe correlation)
///   - BlockchainOperation.InitiatorUserId (Guid?) → OperationLog.InitiatorUserId (record link)
///   - BlockchainOperation.InitiatorApiKeyId (Guid?) → OperationLog.InitiatorApiKeyId (record link)
///
/// Fields in OperationLog NOT in BlockchainOperation / IBlockchainOperation:
///   - Error: not set by this adapter (no IBlockchainOperation.Error field exists)
/// </summary>
public sealed class SurrealBlockchainOperationStore : IBlockchainOperationStore
{
    private const string TableName = "operation_log";

    private readonly ISurrealExecutor _executor;

    public SurrealBlockchainOperationStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<IBlockchainOperation>> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
    {
        try
        {
            var surrealId = SurrealId.ToSurrealId(id);
            var q = SurrealQuery.SelectById(TableName, surrealId);
            var row = await _executor.QuerySingleAsync<OperationLog>(q, ct);

            if (row is null)
                return new AZOAResult<IBlockchainOperation>
                {
                    IsError = true,
                    Message = "Operation not found.",
                    Result  = null
                };

            return new AZOAResult<IBlockchainOperation>
            {
                Message = "Success",
                Result  = ToDomain(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IBlockchainOperation>()
                .CaptureException(ex, $"SurrealBlockchainOperationStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    // ── Receipt correlation and user history reads ───────────────────────────

    /// <inheritdoc />
    public async Task<AZOAResult<IBlockchainOperation>> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return new AZOAResult<IBlockchainOperation>
                {
                    IsError = true,
                    Message = "Idempotency key is required.",
                    Code = AzoaErrorCodes.InvalidRequest,
                    Result = null,
                };
            }

            var q = SurrealQuery<OperationLog>.From()
                .Where(operation => operation.IdempotencyKey == idempotencyKey)
                .AsUntyped();
            var row = await _executor.QuerySingleAsync<OperationLog>(q, ct);

            if (row is null)
            {
                return new AZOAResult<IBlockchainOperation>
                {
                    IsError = true,
                    Message = "Operation not found.",
                    Code = AzoaErrorCodes.NotFound,
                    Result = null,
                };
            }

            return new AZOAResult<IBlockchainOperation>
            {
                Message = "Success",
                Result = ToDomain(row),
            };
        }
        catch (Exception ex)
        {
            var failure = new AZOAResult<IBlockchainOperation>()
                .CaptureException(ex, "Operation lookup is temporarily unavailable.");
            failure.Code = AzoaErrorCodes.DependencyUnavailable;
            return failure;
        }
    }

    public async Task<AZOAResult<IEnumerable<IBlockchainOperation>>> GetByUserAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            var userSurrealId = SurrealLink.ToLink("user", SurrealId.ToSurrealId(userId));

            // SELECT * FROM operation_log WHERE user_id = $user_id
            var q = SurrealQuery<OperationLog>.From()
                .Where(o => o.UserId == userSurrealId);

            var rows = await _executor.QueryAsync<OperationLog>(q, ct);
            var results = rows.Select(r => (IBlockchainOperation)ToDomain(r)).ToList();

            return new AZOAResult<IEnumerable<IBlockchainOperation>>
            {
                Message = "Success",
                Result  = results
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<IBlockchainOperation>>()
                .CaptureException(ex, $"SurrealBlockchainOperationStore.GetByUserAsync failed: {ex.Message}");
        }
    }

    // ── UpsertAsync ───────────────────────────────────────────────────────────

    public async Task<AZOAResult<IBlockchainOperation>> UpsertAsync(
        IBlockchainOperation operation,
        CancellationToken ct = default)
    {
        try
        {
            var existing = await _executor.QuerySingleAsync<OperationLog>(
                SurrealQuery.SelectById(TableName, SurrealId.ToSurrealId(operation.Id)),
                ct);
            var poco = ToPoco(operation);

            if (existing is not null)
            {
                if (HasConflictingReceiptProvenance(operation, existing))
                {
                    return new AZOAResult<IBlockchainOperation>
                    {
                        IsError = true,
                        Message = "Operation receipt provenance cannot be changed.",
                        Code = AzoaErrorCodes.InvalidRequest,
                        Result = null,
                    };
                }

                // Null option fields are omitted by the typed writer, preserving creation provenance.
                poco.IdempotencyKey = null;
                poco.InitiatorUserId = null;
                poco.InitiatorApiKeyId = null;
            }

            var q = existing is null ? SurrealWriter.Create(poco) : SurrealWriter.Upsert(poco);
            var response = await _executor.ExecuteAsync(q, ct);
            response.EnsureAllOk();

            // RETURN AFTER emits the saved record; read it back for round-trip fidelity.
            var saved = response.GetValues<OperationLog>(0).FirstOrDefault();
            IBlockchainOperation domain = saved is not null ? ToDomain(saved) : operation;

            return new AZOAResult<IBlockchainOperation>
            {
                Message = "Saved.",
                Result  = domain
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IBlockchainOperation>()
                .CaptureException(ex, $"SurrealBlockchainOperationStore.UpsertAsync failed: {ex.Message}");
        }
    }

    private static bool HasConflictingReceiptProvenance(
        IBlockchainOperation operation,
        OperationLog existing)
    {
        return !MatchesImmutableValue(operation.IdempotencyKey, existing.IdempotencyKey)
            || !MatchesImmutableValue(
                operation.InitiatorUserId,
                ToOptionalGuid(existing.InitiatorUserId))
            || !MatchesImmutableValue(
                operation.InitiatorApiKeyId,
                ToOptionalGuid(existing.InitiatorApiKeyId));
    }

    private static bool MatchesImmutableValue(string? proposed, string? persisted)
        => proposed is null || string.Equals(proposed, persisted, StringComparison.Ordinal);

    private static bool MatchesImmutableValue(Guid? proposed, Guid? persisted)
        => !proposed.HasValue || proposed == persisted;

    private static Guid? ToOptionalGuid(string? link)
        => link is null ? null : SurrealId.FromSurrealId(SurrealLink.FromLink(link)!);

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            // Probe for existence first so we can return the canonical "not found" result.
            var surrealId = SurrealId.ToSurrealId(id);
            var probeQ = SurrealQuery.SelectById(TableName, surrealId);
            var existing = await _executor.QuerySingleAsync<OperationLog>(probeQ, ct);

            if (existing is null)
                return new AZOAResult<bool>
                {
                    IsError = true,
                    Message = "Operation not found.",
                    Result  = false
                };

            var delQ = SurrealQuery.DeleteById(TableName, surrealId);
            var response = await _executor.ExecuteAsync(delQ, ct);
            response.EnsureAllOk();

            return new AZOAResult<bool>
            {
                Message = "Deleted.",
                Result  = true
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>()
                .CaptureException(ex, $"SurrealBlockchainOperationStore.DeleteAsync failed: {ex.Message}");
        }
    }

    // ── G2 conditional status transition (private helper) ─────────────────────

    /// <summary>
    /// Attempts to transition <paramref name="id"/> from
    /// <paramref name="expectedStatus"/> to <paramref name="nextStatus"/>
    /// atomically via the G2 UpdateOnly primitive.
    ///
    /// Returns <c>true</c> when exactly one row was affected (the caller won the
    /// race), <c>false</c> when zero rows were affected (the status had already
    /// changed under the caller — optimistic-concurrency loss).
    ///
    /// This helper is NOT exposed on the public interface; it is used internally
    /// by upsert paths that perform explicit state-machine transitions, and is
    /// tested independently to prove G2 semantics.
    /// </summary>
    public async Task<bool> TryTransitionStatusAsync(
        Guid id,
        string expectedStatus,
        string nextStatus,
        CancellationToken ct = default)
    {
        var q = SurrealQuery
            .UpdateOnly(TableName, SurrealId.ToSurrealId(id))
            .Where("status", expectedStatus)
            .Set("status", nextStatus);

        var response = await _executor.ExecuteAsync(q, ct);

        // A non-OK statement (e.g. the record does not exist) returns 0 affected;
        // AffectedCount returns 0 for ERR statements so the contract holds either way.
        if (!response[0].IsOk)
            return false;

        int affected = response[0].AffectedCount();
        return affected == 1;
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Maps domain model → generated POCO for persistence.
    /// </summary>
    private static OperationLog ToPoco(IBlockchainOperation op)
    {
        // Resolve the typed status enum from the string constant.
        var statusKind = ParseStatusKind(op.Status);

        // Serialize Parameters dictionary to a JsonElement? for the POCO.
        // ALWAYS serialize (empty → `{}`) so the SET-based upsert REPLACES the
        // column; see Providers/Stores/Surreal/AGENTS.md §set-omits-null.
        JsonElement? parametersElement = JsonSerializer.SerializeToElement(
            op.Parameters ?? new Dictionary<string, string>());

        // IExchange/IMint/ITransfer fields are only available on the concrete type.
        var concrete = op as BlockchainOperation;

        return new OperationLog
        {
            Id            = SurrealId.ToSurrealId(op.Id),
            UserId      = op.UserId.HasValue  ? SurrealLink.ToLink("user", SurrealId.ToSurrealId(op.UserId.Value)) : null,
            WalletId      = op.WalletId.HasValue   ? SurrealLink.ToLink("wallet", SurrealId.ToSurrealId(op.WalletId.Value)) : null,
            OperationType = op.OperationType,
            Status        = statusKind,
            Parameters    = parametersElement,
            // Force UTC kind before constructing DateTimeOffset to avoid
            // ArgumentException when DateTimeKind is Unspecified.
            CreatedDate   = new DateTimeOffset(DateTime.SpecifyKind(op.CreatedDate, DateTimeKind.Utc)),
            CompletedDate = op.CompletedDate.HasValue
                            ? new DateTimeOffset(DateTime.SpecifyKind(op.CompletedDate.Value, DateTimeKind.Utc))
                            : null,

            // IMintOperation
            TokenUri  = concrete?.TokenUri,
            // D13: operation_log.amount is a STRING of decimal base units, so the whole
            // BigInteger range round-trips. The former i64 column could not hold an
            // ordinary wei-denominated amount and forced a checked narrowing here.
            Amount    = concrete?.Amount.ToBaseUnitString(),
            AssetType = concrete?.AssetType,

            // D16: the five stamp columns move together. An unpriced operation writes
            // all of them null, which is what "never priced" looks like on the row and
            // is distinguishable from a stamped rate of "0".
            ValueCurrencyCode = op.ValueStamp?.CurrencyCode,
            ValueMinorUnits   = op.ValueStamp?.MinorUnits,
            ValueRate         = op.ValueStamp?.Rate,
            ValueRateAsOf     = op.ValueStamp?.RateAsOf,
            ValueRateSource   = op.ValueStamp?.RateSource,

            // IExchangeOperation
            SourceHolonId = concrete?.SourceHolonId.HasValue == true
                            ? SurrealLink.ToLink("holon", SurrealId.ToSurrealId(concrete.SourceHolonId!.Value))
                            : null,
            TargetHolonId = concrete?.TargetHolonId.HasValue == true
                            ? SurrealLink.ToLink("holon", SurrealId.ToSurrealId(concrete.TargetHolonId!.Value))
                            : null,
            ExchangeRate  = concrete?.ExchangeRate,

            // ITransferOperation
            RecipientAddress = concrete?.RecipientAddress,

            // Durable allocation receipt context.
            IdempotencyKey = op.IdempotencyKey,
            InitiatorUserId = op.InitiatorUserId.HasValue
                ? SurrealLink.ToLink("user", SurrealId.ToSurrealId(op.InitiatorUserId.Value))
                : null,
            InitiatorApiKeyId = op.InitiatorApiKeyId.HasValue
                ? SurrealLink.ToLink("api_key", SurrealId.ToSurrealId(op.InitiatorApiKeyId.Value))
                : null,

            // tenant-consent-delegation AC4: persist the acting tenant + signing
            // scope so the seam's live consent check survives the async saga hop.
            ActingTenantId = op.ActingTenantId.HasValue
                             ? SurrealLink.ToLink("user", SurrealId.ToSurrealId(op.ActingTenantId.Value))
                             : null,
            SigningScope   = op.SigningScope,

            // Error is not set here because IBlockchainOperation has no matching field.
        };
    }

    /// <summary>
    /// Maps generated POCO → domain model for consumption by service layer.
    /// </summary>
    private static BlockchainOperation ToDomain(OperationLog poco)
    {
        // Deserialize Parameters JsonElement? back to Dictionary<string,string>.
        Dictionary<string, string> parameters = new();
        if (poco.Parameters.HasValue &&
            poco.Parameters.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in poco.Parameters.Value.EnumerateObject())
            {
                parameters[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        return new BlockchainOperation
        {
            Id            = SurrealId.FromSurrealId(poco.Id),
            UserId      = poco.UserId  is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(poco.UserId)!) : null,
            WalletId      = poco.WalletId  is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(poco.WalletId)!) : null,
            OperationType = poco.OperationType,
            Status        = poco.Status.ToString(), // enum.ToString() matches OperationStatus const names
            Parameters    = parameters,
            CreatedDate   = poco.CreatedDate.UtcDateTime,
            CompletedDate = poco.CompletedDate?.UtcDateTime,

            // IMintOperation
            TokenUri  = poco.TokenUri,
            // D13: a decimal base-unit string. Absent reads as zero; present-but-corrupt
            // fails closed, as the former checked i64 narrowing did.
            Amount    = SurrealAmountColumn.Parse(poco.Amount, poco.Id, "Operation"),
            AssetType = poco.AssetType,

            // D16: null unless every component survived — a partial stamp is not a stamp.
            ValueStamp = ValueStamp.FromPersisted(
                poco.ValueCurrencyCode,
                poco.ValueMinorUnits,
                poco.ValueRate,
                poco.ValueRateAsOf,
                poco.ValueRateSource),

            // IExchangeOperation
            SourceHolonId = poco.SourceHolonId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(poco.SourceHolonId)!) : null,
            TargetHolonId = poco.TargetHolonId is not null ? SurrealId.FromSurrealId(SurrealLink.FromLink(poco.TargetHolonId)!) : null,
            ExchangeRate  = poco.ExchangeRate,

            // ITransferOperation
            RecipientAddress = poco.RecipientAddress,

            IdempotencyKey = poco.IdempotencyKey,
            InitiatorUserId = poco.InitiatorUserId is not null
                ? SurrealId.FromSurrealId(SurrealLink.FromLink(poco.InitiatorUserId)!)
                : null,
            InitiatorApiKeyId = poco.InitiatorApiKeyId is not null
                ? SurrealId.FromSurrealId(SurrealLink.FromLink(poco.InitiatorApiKeyId)!)
                : null,

            // tenant-consent-delegation AC4
            ActingTenantId = poco.ActingTenantId is not null
                             ? SurrealId.FromSurrealId(SurrealLink.FromLink(poco.ActingTenantId)!)
                             : null,
            SigningScope   = poco.SigningScope,

            // Error is operation_log-only; IBlockchainOperation has no matching field.
        };
    }

    /// <summary>
    /// Converts an <see cref="OperationStatus"/> string constant to the
    /// generated <see cref="OperationLog.StatusKind"/> enum.
    /// Unknown/unrecognized values fall back to <c>StatusKind.Unknown</c>.
    /// </summary>
    private static OperationLog.StatusKind ParseStatusKind(string status) =>
        Enum.TryParse<OperationLog.StatusKind>(status, ignoreCase: true, out var kind)
            ? kind
            : OperationLog.StatusKind.Unknown;
}
