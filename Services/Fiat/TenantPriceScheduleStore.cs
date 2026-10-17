// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Idempotency;   // SurrealTransientConflict (retryable 3.x txn conflicts)
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat;

/// <summary>
/// The registered, versioned price at which money buys units, and the read the on-ramp applies
/// at webhook time. See <c>Services/Fiat/OnRamp/AGENTS.md</c> §pricing.
/// </summary>
/// <remarks>
/// Rows are APPEND-ONLY. Registering a new price writes a new version rather than updating the
/// current one, so an issuance settled months ago can still name the exact row that priced it.
/// </remarks>
public interface ITenantPriceScheduleStore
{
    /// <summary>
    /// Appends the next version for one scope's price of one asset. The version is assigned by
    /// the node, never by the caller.
    /// </summary>
    Task<AZOAResult<RegisteredPriceSchedule>> RegisterAsync(
        PriceScheduleRegistration registration,
        string scopeKey,
        Guid? tenantId,
        Guid registeredByUserId,
        CancellationToken ct = default);

    /// <summary>
    /// The current version for one scope, whatever its status. Returns a null result — not an
    /// error — when the scope has registered nothing for this asset.
    /// </summary>
    Task<AZOAResult<RegisteredPriceSchedule?>> GetLatestAsync(
        string scopeKey, string chainType, string assetId, CancellationToken ct = default);

    /// <summary>Version history for one scope, newest first.</summary>
    Task<AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>> ListVersionsAsync(
        string scopeKey, string chainType, string assetId, int limit, CancellationToken ct = default);

    /// <summary>
    /// The current version of every scope that has registered a price for one asset. Used by the
    /// D16 stamp, which is handed a chain and an asset and no scope at all.
    /// </summary>
    Task<AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>> ListLatestPerScopeAsync(
        string chainType, string assetId, CancellationToken ct = default);
}

/// <summary>SurrealDB-backed price-schedule store. Colocated with the seam, like the webhook inbox.</summary>
public sealed class SurrealTenantPriceScheduleStore : ITenantPriceScheduleStore
{
    private const string Table = TenantPriceSchedule.SchemaNameConst;

    /// <summary>Attempts to win the next version before giving up to a concurrent registrant.</summary>
    private const int VersionClaimAttempts = 4;

    /// <summary>
    /// Rows scanned when resolving "the current version per scope" for one asset. Append-only
    /// history means this is a bound on VERSIONS, not on scopes; it is generous for a node whose
    /// consumer re-declares a rate on a schedule, and the read is ordered newest-first so the
    /// versions that matter are the ones inside the bound.
    /// </summary>
    public const int LatestPerScopeScanLimit = 500;

    private const string LatestStatement =
        "SELECT * FROM tenant_price_schedule WHERE scope_key = $_scope AND chain_type = $_chain "
        + "AND asset_id = $_asset ORDER BY version DESC LIMIT 1";

    private const string VersionsStatement =
        "SELECT * FROM tenant_price_schedule WHERE scope_key = $_scope AND chain_type = $_chain "
        + "AND asset_id = $_asset ORDER BY version DESC LIMIT $_limit";

    private const string AssetStatement =
        "SELECT * FROM tenant_price_schedule WHERE chain_type = $_chain AND asset_id = $_asset "
        + "ORDER BY version DESC LIMIT $_limit";

    private readonly ISurrealExecutor _executor;

    public SurrealTenantPriceScheduleStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<RegisteredPriceSchedule>> RegisterAsync(
        PriceScheduleRegistration registration,
        string scopeKey,
        Guid? tenantId,
        Guid registeredByUserId,
        CancellationToken ct = default)
    {
        var refusal = PriceScheduleValidator.Validate(registration, out var rate);
        if (refusal is not null)
            return AZOAResult<RegisteredPriceSchedule>.FailureWithCode(refusal, AzoaErrorCodes.InvalidRequest);

        if (string.IsNullOrWhiteSpace(scopeKey))
            return AZOAResult<RegisteredPriceSchedule>.FailureWithCode(
                "A price scope is required.", AzoaErrorCodes.InvalidRequest);
        if (registeredByUserId == Guid.Empty)
            return AZOAResult<RegisteredPriceSchedule>.FailureWithCode(
                "A registering principal is required.", AzoaErrorCodes.Forbidden);

        var chainType = NormalizeChain(registration.ChainType);
        var assetId = registration.AssetId.Trim();
        var scope = scopeKey.Trim();
        var declaredAt = registration.DeclaredAt ?? DateTimeOffset.UtcNow;

        for (var attempt = 0; attempt < VersionClaimAttempts; attempt++)
        {
            var latest = await GetLatestAsync(scope, chainType, assetId, ct);
            if (latest.IsError)
                return AZOAResult<RegisteredPriceSchedule>.Failure(latest.Message);

            var current = latest.Result?.Version ?? 0L;

            // A caller that quoted a version is telling the node which price it believes it is
            // replacing. Honouring a stale quote is how two operators both "correct" a rate and
            // the second one silently wins.
            if (registration.ExpectedCurrentVersion is { } expected && expected != current)
            {
                return AZOAResult<RegisteredPriceSchedule>.FailureWithCode(
                    $"The current price version is {current.ToString(CultureInfo.InvariantCulture)}, not "
                    + $"{expected.ToString(CultureInfo.InvariantCulture)}. Re-read it and register again.",
                    AzoaErrorCodes.Conflict);
            }

            var version = current + 1;
            var row = new TenantPriceSchedule
            {
                Id = TenantPriceSchedule.RecordIdFor(scope, chainType, assetId, version),
                ScopeKey = scope,
                TenantId = tenantId is { } tenant && tenant != Guid.Empty ? tenant.ToString("N") : null,
                ChainType = chainType,
                AssetId = assetId,
                CurrencyCode = PriceScheduleLimits.SupportedCurrencyCode,
                Version = version,
                UnitsPerMinorUnit = PriceScheduleMath.FormatRate(rate),
                Source = registration.Source.Trim(),
                Note = string.IsNullOrWhiteSpace(registration.Note) ? null : registration.Note!.Trim(),
                Status = registration.Retire ? PriceScheduleStatuses.Retired : PriceScheduleStatuses.Active,
                DeclaredAt = declaredAt,
                RegisteredAt = DateTimeOffset.UtcNow,
                RegisteredByUserId = registeredByUserId.ToString("N"),
            };

            var created = await TryCreateAsync(row, ct);
            if (created.IsError)
            {
                // The version was taken between the read and the write. Re-read and try the next
                // one rather than overwriting somebody else's declaration.
                if (created.Code == AzoaErrorCodes.Conflict)
                    continue;

                return AZOAResult<RegisteredPriceSchedule>.Failure(created.Message);
            }

            return AZOAResult<RegisteredPriceSchedule>.Success(
                Project(created.Result!),
                registration.Retire ? "Price retired." : "Price registered.");
        }

        return AZOAResult<RegisteredPriceSchedule>.FailureWithCode(
            "The price version sequence is being written by another caller. Retry shortly.",
            AzoaErrorCodes.Conflict);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<RegisteredPriceSchedule?>> GetLatestAsync(
        string scopeKey, string chainType, string assetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeKey)
            || string.IsNullOrWhiteSpace(chainType)
            || string.IsNullOrWhiteSpace(assetId))
        {
            return AZOAResult<RegisteredPriceSchedule?>.Success(null, "Not found.");
        }

        try
        {
            var query = SurrealQuery
                .Of(LatestStatement)
                .WithParam("_scope", scopeKey.Trim())
                .WithParam("_chain", NormalizeChain(chainType))
                .WithParam("_asset", assetId.Trim());

            var rows = await _executor.QueryAsync<TenantPriceSchedule>(query, ct);
            var row = rows?.FirstOrDefault();
            return new AZOAResult<RegisteredPriceSchedule?>
            {
                Result = row is null ? null : Project(row),
                Message = row is null ? "Not found." : "Success",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<RegisteredPriceSchedule?>()
                .CaptureException(ex, $"Price schedule read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>> ListVersionsAsync(
        string scopeKey, string chainType, string assetId, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeKey)
            || string.IsNullOrWhiteSpace(chainType)
            || string.IsNullOrWhiteSpace(assetId))
        {
            return AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>.Success(
                Array.Empty<RegisteredPriceSchedule>(), "Success");
        }

        try
        {
            var query = SurrealQuery
                .Of(VersionsStatement)
                .WithParam("_scope", scopeKey.Trim())
                .WithParam("_chain", NormalizeChain(chainType))
                .WithParam("_asset", assetId.Trim())
                .WithParam("_limit", Math.Clamp(limit, 1, 200));

            var rows = await _executor.QueryAsync<TenantPriceSchedule>(query, ct);
            return AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>.Success(
                rows?.Select(Project).ToList() ?? new List<RegisteredPriceSchedule>(), "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>()
                .CaptureException(ex, $"Price schedule read failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>> ListLatestPerScopeAsync(
        string chainType, string assetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chainType) || string.IsNullOrWhiteSpace(assetId))
        {
            return AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>.Success(
                Array.Empty<RegisteredPriceSchedule>(), "Success");
        }

        try
        {
            var query = SurrealQuery
                .Of(AssetStatement)
                .WithParam("_chain", NormalizeChain(chainType))
                .WithParam("_asset", assetId.Trim())
                .WithParam("_limit", LatestPerScopeScanLimit);

            var rows = await _executor.QueryAsync<TenantPriceSchedule>(query, ct);

            // Newest-first, so the FIRST row seen for a scope is that scope's current version.
            var latestByScope = new Dictionary<string, RegisteredPriceSchedule>(StringComparer.Ordinal);
            foreach (var row in rows ?? Enumerable.Empty<TenantPriceSchedule>())
            {
                var projected = Project(row);
                if (!latestByScope.ContainsKey(projected.ScopeKey))
                    latestByScope[projected.ScopeKey] = projected;
            }

            return AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>.Success(
                latestByScope.Values.ToList(), "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<RegisteredPriceSchedule>>()
                .CaptureException(ex, $"Price schedule read failed: {ex.Message}");
        }
    }

    /// <summary>Creates the row, reporting a lost race as a conflict rather than a fault.</summary>
    private async Task<AZOAResult<TenantPriceSchedule>> TryCreateAsync(
        TenantPriceSchedule row, CancellationToken ct)
    {
        try
        {
            var create = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", row.Id)
                .WithParam("_body", row);

            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(create, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);

            return AZOAResult<TenantPriceSchedule>.Success(
                response.GetValues<TenantPriceSchedule>(0).SingleOrDefault() ?? row, "Created.");
        }
        catch (Exception ex)
        {
            // Only conclude "somebody else took this version" when the row really is there now; a
            // transport failure reported as a conflict would spin the version sequence forward
            // for no reason.
            var existing = await GetByIdAsync(row.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return AZOAResult<TenantPriceSchedule>.FailureWithCode(
                    "That price version already exists.", AzoaErrorCodes.Conflict);

            return new AZOAResult<TenantPriceSchedule>()
                .CaptureException(ex, $"Price schedule write failed: {ex.Message}");
        }
    }

    private async Task<AZOAResult<TenantPriceSchedule?>> GetByIdAsync(string recordId, CancellationToken ct)
    {
        try
        {
            var query = SurrealQuery<TenantPriceSchedule>.Key(recordId);
            var row = await _executor.QuerySingleAsync<TenantPriceSchedule>(query, ct);
            return new AZOAResult<TenantPriceSchedule?> { Result = row, Message = row is null ? "Not found." : "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<TenantPriceSchedule?>()
                .CaptureException(ex, $"Price schedule read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Chain types are stored folded so a registration for "algorand" and a lookup for
    /// "Algorand" are the same price. An asset id is never folded — it is opaque.
    /// </summary>
    public static string NormalizeChain(string chainType) => chainType.Trim().ToLowerInvariant();

    private static RegisteredPriceSchedule Project(TenantPriceSchedule row)
    {
        // A row whose rate no longer parses is a corrupted money constant. It is projected as
        // zero, which every consumer refuses, rather than silently substituted with a default.
        var rate = decimal.TryParse(
            row.UnitsPerMinorUnit, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0m;

        return new RegisteredPriceSchedule(
            row.Id,
            row.ScopeKey,
            Guid.TryParseExact(row.TenantId, "N", out var tenantId) ? tenantId : null,
            row.ChainType,
            row.AssetId,
            row.CurrencyCode,
            row.Version,
            rate,
            row.Source,
            row.Note,
            row.Status,
            row.DeclaredAt,
            row.RegisteredAt);
    }
}

/// <summary>
/// Makes the registered price schedule the source of the D16 value stamp, so the USD figure on
/// an issuance's operation row is the SAME price that converted the money — not a second,
/// independently configured rate that could disagree with it.
/// </summary>
/// <remarks>
/// <para>This is the read side of the control. Without it the schedule would decide quantities
/// while the stamp read <c>ValueRates:Declared</c>, and the two could drift silently on a
/// financial record.</para>
/// <para><b>Scope precedence (OI-64).</b> When several scopes hold a current price for one asset
/// this store RESOLVES rather than going dark. The on-ramp's own order is the driving tenant's
/// schedule first and the operator's node-wide default second
/// (<c>OnRampSettlementHandler.ResolveScheduleAsync</c>); this seam is handed a chain and an asset
/// and NO tenant, so it can only apply the second half — the node-wide default — and that is exactly
/// what it does. Naming it is what keeps the platform unit priced: previously ANY second scope
/// blanked every stamp node-wide, which made "the price that sold the units stamps the operation"
/// true only on a single-tenant node. Stamping a tenant's own sale with its OWN price needs the
/// tenant threaded through <c>ITenantDeclaredRateStore</c>, which is a separate change to a shared
/// interface; until then a node-wide read stamps the node-wide price, and says so in the log.</para>
/// <para>It still refuses rather than guessing when the answer is genuinely not knowable: when the
/// latest version of the only scope that priced the asset is RETIRED; when two or more TENANT
/// scopes price one asset and the operator has declared no node-wide price to break the tie (that
/// case raises an operator alert, not a warning); and when the store cannot be read. In every one
/// of those the inner store is consulted, and if it declares nothing the operation is recorded
/// unpriced — the honest answer D16 requires.</para>
/// </remarks>
public sealed class PriceScheduleDeclaredRateStore : ITenantDeclaredRateStore
{
    private readonly ITenantPriceScheduleStore _schedules;
    private readonly ITenantDeclaredRateStore _fallback;
    private readonly ILogger<PriceScheduleDeclaredRateStore>? _logger;

    /// <param name="fallback">
    /// The configuration-backed store (<c>ValueRates:Declared</c>). Consulted only for assets
    /// this node has no registered schedule for, so a project token can still be priced from
    /// configuration while the platform unit is priced by its on-ramp schedule.
    /// </param>
    public PriceScheduleDeclaredRateStore(
        ITenantPriceScheduleStore schedules,
        ITenantDeclaredRateStore fallback,
        ILogger<PriceScheduleDeclaredRateStore>? logger = null)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<TenantDeclaredRate?> TryGetLatestAsync(
        string chainType, string assetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chainType) || string.IsNullOrWhiteSpace(assetId))
            return null;

        var scoped = await _schedules.ListLatestPerScopeAsync(chainType, assetId, ct);
        if (scoped.IsError)
        {
            _logger?.LogWarning(
                "The price schedule for {ChainType}/{AssetId} could not be read; falling back to the "
                + "configured declarations rather than stamping a guessed value.",
                chainType, assetId);
            return await _fallback.TryGetLatestAsync(chainType, assetId, ct);
        }

        var current = (scoped.Result ?? Array.Empty<RegisteredPriceSchedule>())
            .Where(schedule => schedule.IsActive && schedule.UnitsPerMinorUnit > 0m)
            .ToList();

        if (current.Count == 0)
            return await _fallback.TryGetLatestAsync(chainType, assetId, ct);

        if (!TryChooseScope(current, chainType, assetId, out var schedule))
            return null;

        if (!PriceScheduleMath.TryDeriveMinorUnitsPerBaseUnit(
                schedule.UnitsPerMinorUnit, out var minorUnitsPerBaseUnit))
        {
            return null;
        }

        return new TenantDeclaredRate(
            schedule.ChainType,
            schedule.AssetId,
            schedule.CurrencyCode,
            minorUnitsPerBaseUnit,
            schedule.DeclaredAt,
            schedule.Source,
            schedule.Version.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Picks the one scope a scope-less read may stamp with, or refuses when no rule can name one.
    /// </summary>
    /// <remarks>
    /// OI-64. One scope is unambiguous. Several are resolved by the node-wide default, which is
    /// the operator's own declaration and the price the on-ramp falls back to — a scope-less read
    /// is a node-level read, so that is the scope it can honestly name. Several TENANT scopes with
    /// no node-wide price left to break the tie is the one case that is genuinely unknowable from
    /// a chain and an asset, and it is an operator alert rather than a warning: until the operator
    /// registers a node-wide price, every operation on that asset is recorded unpriced.
    /// </remarks>
    private bool TryChooseScope(
        IReadOnlyList<RegisteredPriceSchedule> current,
        string chainType,
        string assetId,
        out RegisteredPriceSchedule chosen)
    {
        if (current.Count == 1)
        {
            chosen = current[0];
            return true;
        }

        // Highest version, so a store that ever hands back two rows for one scope still resolves
        // deterministically instead of depending on enumeration order.
        var nodeScoped = current
            .Where(schedule => PriceScheduleScope.IsNode(schedule.ScopeKey))
            .OrderByDescending(schedule => schedule.Version)
            .FirstOrDefault();

        if (nodeScoped is not null)
        {
            _logger?.LogWarning(
                "{ScopeCount} scopes have a current price for {ChainType}/{AssetId}; the operation is "
                + "stamped with the node-wide price (version {Version}), which is the scope a read that "
                + "names no tenant can resolve.",
                current.Count, chainType, assetId, nodeScoped.Version);
            chosen = nodeScoped;
            return true;
        }

        _logger?.LogError(
            "MANUAL INTERVENTION REQUIRED: {ScopeCount} tenant scopes have a current price for "
            + "{ChainType}/{AssetId} and this node has no node-wide price to resolve them, so every "
            + "operation on this asset is being recorded UNPRICED. Register a node-wide price for "
            + "this asset.",
            current.Count, chainType, assetId);
        chosen = null!;
        return false;
    }
}
