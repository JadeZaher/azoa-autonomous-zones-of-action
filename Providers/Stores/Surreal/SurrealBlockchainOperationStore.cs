using System.Text.Json;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Persistence.SurrealDb.Models;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed implementation of <see cref="IBlockchainOperationStore"/>.
/// Translates between the legacy <see cref="BlockchainOperation"/> domain model
/// and the generated <see cref="OperationLog"/> POCO (table: operation_log).
///
/// Mapping notes:
///   - BlockchainOperation.Id (Guid)        → OperationLog.Id (string, "N" format, lowercase)
///   - BlockchainOperation.AvatarId (Guid?) → OperationLog.AvatarId (string?, "N" format)
///   - BlockchainOperation.WalletId (Guid?) → OperationLog.WalletId (string?, "N" format)
///   - BlockchainOperation.Amount (int)     → OperationLog.Amount (long?)
///   - BlockchainOperation.Parameters (Dictionary) → OperationLog.Parameters (JsonElement?)
///   - BlockchainOperation.CreatedDate (DateTime UTC) → OperationLog.CreatedDate (DateTimeOffset UTC)
///   - BlockchainOperation.CompletedDate (DateTime?) → OperationLog.CompletedDate (DateTimeOffset?)
///   - BlockchainOperation.Status (string)  → OperationLog.Status (OperationLog.StatusKind enum)
///
/// Fields in OperationLog NOT in BlockchainOperation / IBlockchainOperation:
///   - IdempotencyKey: not set by this adapter (api-safety-hardening §4 enforces upstream)
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

    public async Task<OASISResult<IBlockchainOperation>> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
    {
        try
        {
            var surrealId = ToSurrealId(id);
            var q = SurrealQuery.SelectById(TableName, surrealId);
            var row = await _executor.QuerySingleAsync<OperationLog>(q, ct);

            if (row is null)
                return new OASISResult<IBlockchainOperation>
                {
                    IsError = true,
                    Message = "Operation not found.",
                    Result  = null
                };

            return new OASISResult<IBlockchainOperation>
            {
                Message = "Success",
                Result  = ToDomain(row)
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IBlockchainOperation>()
                .CaptureException(ex, $"SurrealBlockchainOperationStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    // ── GetByAvatarAsync ──────────────────────────────────────────────────────

    public async Task<OASISResult<IEnumerable<IBlockchainOperation>>> GetByAvatarAsync(
        Guid avatarId,
        CancellationToken ct = default)
    {
        try
        {
            var avatarSurrealId = ToSurrealId(avatarId);

            // SELECT * FROM operation_log WHERE avatar_id = $avatar_id
            var q = SurrealQuery<OperationLog>.From()
                .Where(o => o.AvatarId == avatarSurrealId)
                .AsUntyped();

            var rows = await _executor.QueryAsync<OperationLog>(q, ct);
            var results = rows.Select(r => (IBlockchainOperation)ToDomain(r)).ToList();

            return new OASISResult<IEnumerable<IBlockchainOperation>>
            {
                Message = "Success",
                Result  = results
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IEnumerable<IBlockchainOperation>>()
                .CaptureException(ex, $"SurrealBlockchainOperationStore.GetByAvatarAsync failed: {ex.Message}");
        }
    }

    // ── UpsertAsync ───────────────────────────────────────────────────────────

    public async Task<OASISResult<IBlockchainOperation>> UpsertAsync(
        IBlockchainOperation operation,
        CancellationToken ct = default)
    {
        try
        {
            var poco = ToPoco(operation);
            var surrealId = poco.Id;

            // UPDATE type::record('operation_log', $id) CONTENT $body RETURN AFTER
            // SurrealDB upsert: creates the record if it does not exist; replaces
            // it if it does.  No conditional guard — full replacement semantics.
            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", TableName)
                .WithParam("_id", surrealId)
                .WithParam("_body", poco);

            var response = await _executor.ExecuteAsync(q, ct);
            response.EnsureAllOk();

            // RETURN AFTER emits the saved record; read it back for round-trip fidelity.
            var saved = response.GetValues<OperationLog>(0).FirstOrDefault();
            IBlockchainOperation domain = saved is not null ? ToDomain(saved) : operation;

            return new OASISResult<IBlockchainOperation>
            {
                Message = "Saved.",
                Result  = domain
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<IBlockchainOperation>()
                .CaptureException(ex, $"SurrealBlockchainOperationStore.UpsertAsync failed: {ex.Message}");
        }
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    public async Task<OASISResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            // Probe for existence first so we can return the canonical "not found" result.
            var surrealId = ToSurrealId(id);
            var probeQ = SurrealQuery.SelectById(TableName, surrealId);
            var existing = await _executor.QuerySingleAsync<OperationLog>(probeQ, ct);

            if (existing is null)
                return new OASISResult<bool>
                {
                    IsError = true,
                    Message = "Operation not found.",
                    Result  = false
                };

            var delQ = SurrealQuery.DeleteById(TableName, surrealId);
            var response = await _executor.ExecuteAsync(delQ, ct);
            response.EnsureAllOk();

            return new OASISResult<bool>
            {
                Message = "Deleted.",
                Result  = true
            };
        }
        catch (Exception ex)
        {
            return new OASISResult<bool>()
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
            .UpdateOnly(TableName, ToSurrealId(id))
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

    /// <summary>Converts a <see cref="Guid"/> to the SurrealDB string id convention.</summary>
    private static string ToSurrealId(Guid id) =>
        id.ToString("N").ToLowerInvariant();

    /// <summary>Parses a SurrealDB string id back to a <see cref="Guid"/>.</summary>
    private static Guid FromSurrealId(string id) =>
        Guid.ParseExact(id, "N");

    /// <summary>
    /// Maps domain model → generated POCO for persistence.
    /// </summary>
    private static OperationLog ToPoco(IBlockchainOperation op)
    {
        // Resolve the typed status enum from the string constant.
        var statusKind = ParseStatusKind(op.Status);

        // Serialize Parameters dictionary to a JsonElement? for the POCO.
        JsonElement? parametersElement = null;
        if (op.Parameters is { Count: > 0 })
        {
            var json = JsonSerializer.Serialize(op.Parameters);
            using var doc = JsonDocument.Parse(json);
            parametersElement = doc.RootElement.Clone();
        }

        // IExchange/IMint/ITransfer fields are only available on the concrete type.
        var concrete = op as BlockchainOperation;

        return new OperationLog
        {
            Id            = ToSurrealId(op.Id),
            AvatarId      = op.AvatarId.HasValue  ? ToSurrealId(op.AvatarId.Value) : null,
            WalletId      = op.WalletId.HasValue   ? ToSurrealId(op.WalletId.Value) : null,
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
            Amount    = concrete is not null ? (long?)concrete.Amount : null,
            AssetType = concrete?.AssetType,

            // IExchangeOperation
            SourceHolonId = concrete?.SourceHolonId.HasValue == true
                            ? ToSurrealId(concrete.SourceHolonId!.Value)
                            : null,
            TargetHolonId = concrete?.TargetHolonId.HasValue == true
                            ? ToSurrealId(concrete.TargetHolonId!.Value)
                            : null,
            ExchangeRate  = concrete?.ExchangeRate,

            // ITransferOperation
            RecipientAddress = concrete?.RecipientAddress,

            // IdempotencyKey and Error are NOT set here:
            //   IdempotencyKey — must be supplied by the caller upstream (§4 validator).
            //   Error          — no corresponding field on IBlockchainOperation.
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
            Id            = FromSurrealId(poco.Id),
            AvatarId      = poco.AvatarId  is not null ? FromSurrealId(poco.AvatarId) : null,
            WalletId      = poco.WalletId  is not null ? FromSurrealId(poco.WalletId) : null,
            OperationType = poco.OperationType,
            Status        = poco.Status.ToString(), // enum.ToString() matches OperationStatus const names
            Parameters    = parameters,
            CreatedDate   = poco.CreatedDate.UtcDateTime,
            CompletedDate = poco.CompletedDate?.UtcDateTime,

            // IMintOperation
            TokenUri  = poco.TokenUri,
            Amount    = poco.Amount.HasValue ? (int)poco.Amount.Value : 0,
            AssetType = poco.AssetType,

            // IExchangeOperation
            SourceHolonId = poco.SourceHolonId is not null ? FromSurrealId(poco.SourceHolonId) : null,
            TargetHolonId = poco.TargetHolonId is not null ? FromSurrealId(poco.TargetHolonId) : null,
            ExchangeRate  = poco.ExchangeRate,

            // ITransferOperation
            RecipientAddress = poco.RecipientAddress,

            // IdempotencyKey and Error are not represented in BlockchainOperation —
            // they are operation_log-only fields for the SurrealDB idempotency contract.
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
