// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Analytics;

/// <summary>
/// Projects <c>operation_log</c> into a transaction history and per-asset positions.
/// Read-only: it never writes, never prices, and never invents a rate.
/// See <c>Services/Analytics/AGENTS.md</c>.
/// </summary>
public sealed class AnalyticsReadService : IAnalyticsReadService
{
    /// <summary>Page size applied when the caller names none.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>Largest page a caller can ask for. Must not exceed the store's own page cap.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Rows one positions request will fold before it declares itself truncated.</summary>
    public const int MaxPositionScanEntries = 10_000;

    /// <summary>Operation types that move one identifiable quantity, per §value-stamp.</summary>
    private const string MintOperationType = ValueStampOperationTypes.Mint;
    private const string BurnOperationType = ValueStampOperationTypes.Burn;
    private const string TransferOperationType = ValueStampOperationTypes.Transfer;

    /// <summary>
    /// An allocation that puts units INTO the subject's account. It shares the Transfer SHAPE
    /// (the quantity is <c>Parameters["Amount"]</c>, the asset is <c>Parameters["SourceTokenId"]</c>)
    /// but NOT the transfer's direction. <c>AllocationManager</c> stamps the RECIPIENT on
    /// <c>UserId</c> and leaves <c>WalletId</c> null, because the units leave the reserve rather
    /// than the person's own wallet — so on the account this history is read for, an issuance is
    /// a credit.
    /// </summary>
    private const string IssueUnitsOperationType = ValueStampOperationTypes.IssueUnits;

    private readonly IBlockchainOperationStore _operations;

    public AnalyticsReadService(IBlockchainOperationStore operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<TransactionHistoryPage>> GetTransactionsAsync(
        Guid userId,
        AnalyticsQueryRequest request,
        CancellationToken ct = default)
    {
        if (!TryBuildQuery(userId, request, out var query, out var take, out var error))
            return AZOAResult<TransactionHistoryPage>.FailureWithCode(error, AzoaErrorCodes.InvalidRequest);

        var page = await _operations.QueryAsync(query, ct);
        if (page.IsError || page.Result is null)
        {
            return AZOAResult<TransactionHistoryPage>.FailureWithCode(
                "Transaction history is unavailable.",
                page.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        var items = page.Result.Select(ToEntry).ToList();

        return AZOAResult<TransactionHistoryPage>.Success(new TransactionHistoryPage
        {
            Items = items,
            Skip = query.Skip,
            Take = take,
            HasMore = items.Count == take,
            UnpricedEntryCount = items.Count(entry => entry.Pricing == PricingStatus.NeverPriced),
            UnknownAmountEntryCount = items.Count(entry => entry.AmountStatus == QuantityStatus.Unknown),
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// Positions need the whole window, not one page, so this walks the store in pages up to
    /// <see cref="MaxPositionScanEntries"/>. Hitting that bound does not silently truncate the
    /// answer: the response says so, because a partial total presented as final is the failure
    /// mode this whole surface exists to avoid.
    /// </remarks>
    public async Task<AZOAResult<PositionsResponse>> GetPositionsAsync(
        Guid userId,
        AnalyticsQueryRequest request,
        CancellationToken ct = default)
    {
        var scanRequest = new AnalyticsQueryRequest
        {
            From = request?.From,
            To = request?.To,
            Skip = 0,
            Take = MaxPageSize,
        };

        if (!TryBuildQuery(userId, scanRequest, out var query, out var pageSize, out var error))
            return AZOAResult<PositionsResponse>.FailureWithCode(error, AzoaErrorCodes.InvalidRequest);

        var accumulators = new Dictionary<PositionKey, PositionAccumulator>();
        var scanned = 0;
        var complete = true;

        while (true)
        {
            var page = await _operations.QueryAsync(query with { Skip = scanned }, ct);
            if (page.IsError || page.Result is null)
            {
                return AZOAResult<PositionsResponse>.FailureWithCode(
                    "Positions are unavailable.",
                    page.Code ?? AzoaErrorCodes.DependencyUnavailable);
            }

            foreach (var operation in page.Result)
            {
                var entry = ToEntry(operation);
                var key = new PositionKey(entry.ChainType, entry.AssetId);

                if (!accumulators.TryGetValue(key, out var accumulator))
                {
                    accumulator = new PositionAccumulator();
                    accumulators[key] = accumulator;
                }

                accumulator.Add(entry);
                scanned++;
            }

            if (page.Result.Count < pageSize)
                break;

            if (scanned >= MaxPositionScanEntries)
            {
                complete = false;
                break;
            }
        }

        var items = accumulators
            .OrderBy(pair => pair.Key.ChainType, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.AssetId, StringComparer.Ordinal)
            .Select(pair => pair.Value.ToSummary(pair.Key))
            .ToList();

        return AZOAResult<PositionsResponse>.Success(new PositionsResponse
        {
            Items = items,
            ScannedEntryCount = scanned,
            IsComplete = complete,
        });
    }

    // ── Request validation ────────────────────────────────────────────────────

    /// <summary>
    /// Turns a caller's window into a store query. <paramref name="userId"/> is the ONLY source
    /// of the subject; the request type carries no user id at all so none can be smuggled in.
    /// </summary>
    private static bool TryBuildQuery(
        Guid userId,
        AnalyticsQueryRequest? request,
        out BlockchainOperationQuery query,
        out int take,
        out string error)
    {
        query = null!;
        take = 0;
        error = string.Empty;

        if (userId == Guid.Empty)
        {
            error = "An authenticated user is required.";
            return false;
        }

        var skip = request?.Skip ?? 0;
        if (skip < 0)
        {
            error = "Skip cannot be negative.";
            return false;
        }

        var requested = request?.Take ?? DefaultPageSize;
        if (requested <= 0)
        {
            error = "Take must be greater than zero.";
            return false;
        }

        if (request?.From is { } from && request.To is { } to && from > to)
        {
            error = "The start of the window cannot be after its end.";
            return false;
        }

        take = Math.Min(requested, MaxPageSize);
        query = new BlockchainOperationQuery
        {
            UserId = userId,
            From = request?.From,
            To = request?.To,
            Skip = skip,
            Take = take,
        };
        return true;
    }

    // ── Projection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps one persisted operation onto the wire. The stamp is mapped straight through:
    /// <c>ValueStamp.FromPersisted</c> already returned null for a partial stamp, so a null
    /// here means "never priced" and nothing further needs deciding.
    /// </summary>
    internal static TransactionHistoryEntry ToEntry(IBlockchainOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var stamp = operation.ValueStamp;
        var amount = ResolveAmount(operation);

        return new TransactionHistoryEntry
        {
            OperationId = operation.Id,
            Timestamp = ToUtcOffset(operation.CreatedDate),
            SettledAt = operation.CompletedDate.HasValue ? ToUtcOffset(operation.CompletedDate.Value) : null,
            OperationType = operation.OperationType,
            Status = operation.Status,
            AssetType = (operation as IMintOperation)?.AssetType,
            AssetId = ResolveAssetId(operation),
            ChainType = Parameter(operation, "ChainType"),
            Amount = amount,

            // The quantity axis gets the same three-way treatment as the price axis: an
            // absent amount is carried as absent AND labelled, so no consumer can default it.
            AmountStatus = amount is null ? QuantityStatus.Unknown : QuantityStatus.Known,
            CounterpartyAddress = (operation as ITransferOperation)?.RecipientAddress
                                  ?? Parameter(operation, "RecipientAddress"),
            TxHash = Parameter(operation, "TxHash"),
            Fee = Parameter(operation, "NodeFeeAmount"),
            IsStampable = IsStampable(operation),

            // Pricing is carried alongside Value, never inferred from it by the consumer:
            // a null-stripping client would otherwise turn "unpriced" into a silent zero.
            Pricing = stamp is null ? PricingStatus.NeverPriced : PricingStatus.Priced,
            Value = stamp is null ? null : ValueStampResponse.From(stamp),
        };
    }

    /// <summary>
    /// Mirrors §value-stamp. This USED to be a second list that "must stay identical to" the
    /// write side, and twice it was not: a stamped Exchange read back as
    /// <c>"pricing": "Priced"</c> beside <c>"isStampable": false</c>, and then IssueUnits
    /// reproduced the same contradiction the moment the write side learned it. There is now ONE
    /// declaration — <see cref="ValueStampOperationTypes.All"/> — which this reads and
    /// <c>BlockchainOperationManager.TryResolveStampableAmount</c> is held to by
    /// <c>tests/AZOA.WebAPI.Tests/Analytics/StampableSetDriftTests.cs</c>.
    /// </summary>
    private static bool IsStampable(IBlockchainOperation operation) =>
        ValueStampOperationTypes.Contains(operation.OperationType) && HasBroadcast(operation.Status);

    /// <summary>True only for statuses that mean a transaction actually went to the chain.</summary>
    private static bool HasBroadcast(string status) =>
        status is not (OperationStatus.Failed
            or OperationStatus.AwaitingSignature
            or OperationStatus.Unknown
            or OperationStatus.Pending);

    /// <summary>True only for terminal states, so an in-flight movement never lands in a total.</summary>
    private static bool IsSettled(string status) =>
        status is OperationStatus.Completed
            or OperationStatus.Minted
            or OperationStatus.Burned
            or OperationStatus.Exchanged
            or OperationStatus.Swapped
            or OperationStatus.Transferred
            or OperationStatus.Deployed
            or OperationStatus.Called;

    /// <summary>
    /// Which way the position moves. Only the single-quantity operations have a direction the
    /// log determines; everything else contributes zero and is counted as unclassified rather
    /// than folded in at an assumed sign.
    /// </summary>
    private static int Direction(string operationType) => operationType switch
    {
        MintOperationType => 1,
        // An issuance is the reserve paying units INTO this account, so it is a credit and not a
        // transfer out. Left unclassified it contributed zero, which made units credited to a
        // worker or a sponsor invisible in the only money view they ever read.
        IssueUnitsOperationType => 1,
        BurnOperationType => -1,
        TransferOperationType => -1,
        _ => 0,
    };

    /// <summary>The chain-side asset identifier under the several keys producers have used for it.</summary>
    private static string? ResolveAssetId(IBlockchainOperation operation) =>
        Parameter(operation, "AssetId")
        ?? Parameter(operation, "TokenId")
        ?? Parameter(operation, "SourceTokenId");

    /// <summary>
    /// The moved quantity as a base-unit decimal string, or <c>null</c> when the log records
    /// none. The typed field wins where it carries a value; the <c>Amount</c> parameter is the
    /// fallback because Burn and Transfer record the quantity there, which is the same place
    /// the settlement stamp reads it from.
    /// </summary>
    /// <remarks>
    /// An absent or unreadable amount returns null, NEVER <c>"0"</c>. Rendering an unknown
    /// quantity as a real zero is the §unpriced-representation failure mode moved from the
    /// price axis to the quantity axis — and on a tax surface a fabricated zero quantity is
    /// exactly as wrong as a fabricated rate.
    /// </remarks>
    private static string? ResolveAmount(IBlockchainOperation operation)
    {
        if (operation is IMintOperation { Amount.IsZero: false } typed)
            return typed.Amount.ToBaseUnitString();

        var parameter = Parameter(operation, "Amount");
        return parameter is not null
               && BigInteger.TryParse(parameter, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string? Parameter(IBlockchainOperation operation, string key)
    {
        if (operation.Parameters is null || !operation.Parameters.TryGetValue(key, out var value))
            return null;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    // ── Position folding ──────────────────────────────────────────────────────

    private readonly record struct PositionKey(string? ChainType, string? AssetId);

    /// <summary>
    /// Folds entries into one position. Every exclusion is counted rather than dropped, so the
    /// response can state exactly how much of the window the totals actually cover.
    /// </summary>
    private sealed class PositionAccumulator
    {
        private BigInteger _quantity = BigInteger.Zero;
        private BigInteger _realizedMinorUnits = BigInteger.Zero;
        private string? _currencyCode;
        private bool _valueUnusable;

        private int _entryCount;
        private int _unsettledEntryCount;
        private int _unclassifiedEntryCount;
        private int _unknownAmountEntryCount;
        private int _pricedEntryCount;
        private int _unpricedEntryCount;
        private int _unstampableEntryCount;

        public void Add(TransactionHistoryEntry entry)
        {
            _entryCount++;

            if (entry.Pricing == PricingStatus.Priced && entry.Value is not null)
                _pricedEntryCount++;
            else
            {
                _unpricedEntryCount++;
                if (!entry.IsStampable)
                    _unstampableEntryCount++;
            }

            var direction = Direction(entry.OperationType);
            if (direction == 0)
            {
                _unclassifiedEntryCount++;
                return;
            }

            if (!IsSettled(entry.Status))
            {
                _unsettledEntryCount++;
                return;
            }

            // An unknown quantity is LEFT OUT and counted, never folded in as zero: adding
            // zero would quietly under-report the position while IsQuantityComplete still
            // claimed the total covered every entry.
            if (entry.AmountStatus != QuantityStatus.Known
                || !BigInteger.TryParse(entry.Amount, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            {
                _unknownAmountEntryCount++;
                return;
            }

            _quantity += direction * amount;

            AccumulateRealized(entry, direction);
        }

        /// <summary>
        /// Adds a stamped leg to the running fiat total, or marks the total unusable. Two
        /// currencies cannot be added, and an unparseable stamp cannot be trusted — in both
        /// cases the position reports no total rather than a plausible-looking wrong one.
        /// </summary>
        private void AccumulateRealized(TransactionHistoryEntry entry, int direction)
        {
            if (entry.Value is not { } value)
                return;

            if (_currencyCode is null)
                _currencyCode = value.CurrencyCode;
            else if (!string.Equals(_currencyCode, value.CurrencyCode, StringComparison.Ordinal))
                _valueUnusable = true;

            if (BigInteger.TryParse(value.MinorUnits, NumberStyles.None, CultureInfo.InvariantCulture, out var minorUnits))
                _realizedMinorUnits += direction * minorUnits;
            else
                _valueUnusable = true;
        }

        public PositionSummary ToSummary(PositionKey key)
        {
            var quantityComplete = _unsettledEntryCount == 0
                && _unclassifiedEntryCount == 0
                && _unknownAmountEntryCount == 0;
            var valueComplete = _unpricedEntryCount == 0;
            var currencyCode = _currencyCode;

            // A total is emitted only when nothing was left out of it. Summing an unpriced leg
            // as zero would render a fiat figure that is quietly missing part of the position.
            var realized = valueComplete
                           && quantityComplete
                           && !_valueUnusable
                           && _entryCount > 0
                           && currencyCode is not null
                ? new FiatValueResponse
                {
                    CurrencyCode = currencyCode,
                    MinorUnits = _realizedMinorUnits.ToString(CultureInfo.InvariantCulture),
                }
                : null;

            return new PositionSummary
            {
                ChainType = key.ChainType,
                AssetId = key.AssetId,
                Quantity = _quantity.ToString(CultureInfo.InvariantCulture),
                EntryCount = _entryCount,
                UnsettledEntryCount = _unsettledEntryCount,
                UnclassifiedEntryCount = _unclassifiedEntryCount,
                UnknownAmountEntryCount = _unknownAmountEntryCount,
                IsQuantityComplete = quantityComplete,
                PricedEntryCount = _pricedEntryCount,
                UnpricedEntryCount = _unpricedEntryCount,
                UnstampableEntryCount = _unstampableEntryCount,
                IsValueComplete = valueComplete,
                RealizedValue = realized,

                // Always absent: a current valuation needs a CURRENT quote, and no rate source
                // exists. Re-presenting a settlement stamp as today's price is exactly the
                // stale-as-current fabrication §value-stamp forbids.
                UnrealizedValue = null,
            };
        }
    }
}
