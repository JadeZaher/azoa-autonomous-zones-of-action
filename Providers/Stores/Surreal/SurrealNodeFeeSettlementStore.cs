using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using DomainIdempotencyRecord = AZOA.WebAPI.Models.Idempotency.IdempotencyRecord;
using DomainIdempotencyState = AZOA.WebAPI.Models.Idempotency.IdempotencyState;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealNodeFeeSettlementStore : INodeFeeSettlementStore
{
    private static readonly NodeFeeSettlement.StateKind[] RecoverableStates =
    [
        NodeFeeSettlement.StateKind.Prepared,
        NodeFeeSettlement.StateKind.PrimarySubmitted,
        NodeFeeSettlement.StateKind.FeeSubmitted,
        NodeFeeSettlement.StateKind.AwaitingReconciliation,
    ];

    private const string AcceptedGroupReconciliationReason =
        "Accepted atomic group requires independent chain reconciliation.";

    private readonly ISurrealExecutor _executor;

    public SurrealNodeFeeSettlementStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeSettlementAdmissionResult>> AdmitAsync(
        NodeFeeSettlement settlement,
        string parentIdempotencyKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentIdempotencyKey);

        var parentKey = NodeFeeSettlement.CanonicalizeParentIdempotencyKey(parentIdempotencyKey);
        var parentId = NodeFeeSettlement.HashParentIdempotencyKey(parentKey);
        EnsurePreparedAdmission(settlement, parentKey, parentId);

        var now = DateTimeOffset.UtcNow;
        var parentOperation = NodeFeeSettlement.ParentClaimOperationType(settlement.Operation);
        var parentContent = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = parentId,
            ["key"] = SurrealIdempotencyStore.EncodeKeyForConfiguredLedger(parentKey),
            ["operation_type"] = parentOperation,
            ["state"] = IdempotencyKeyStore.StateKind.InProgress.ToString(),
            ["created_at"] = now,
            ["updated_at"] = now,
        };

        // raw: the parent claim and settlement must be admitted in one transaction;
        // current SurrealForge typed primitives cannot conditionally create/replay two tables. Waiver expires 2026-08-31.
        var atomic = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealQuery
                .Of("LET $_parent = (SELECT * FROM type::record($_parent_table, $_parent_id)).first()")
                .WithParam("_parent_table", IdempotencyKeyStore.SchemaNameConst)
                .WithParam("_parent_id", parentId),
            SurrealQuery
                .Of("LET $_settlement = (SELECT * FROM type::record($_settlement_table, $_settlement_id)).first()")
                .WithParam("_settlement_table", NodeFeeSettlement.SchemaNameConst)
                .WithParam("_settlement_id", settlement.Id),
            SurrealQuery
                .Of("IF $_parent = NONE AND $_settlement = NONE { CREATE type::record($_parent_table, $_parent_id) CONTENT $_parent_content RETURN AFTER } ELSE IF $_parent != NONE AND $_settlement != NONE { IF $_parent.key != type::string($_parent_key) OR $_parent.operation_type != type::string($_parent_operation) { THROW 'Node fee settlement parent claim conflict' } ELSE IF $_parent.state = $_in_progress AND $_settlement.state INSIDE $_terminal_states { THROW 'Node fee settlement is terminal before parent terminal' } ELSE IF $_parent.state != $_in_progress AND !($_settlement.state INSIDE $_terminal_states) { THROW 'Node fee settlement parent claim is terminal before settlement terminal' } ELSE { NONE } } ELSE { THROW 'Node fee settlement admission is inconsistent' }")
                .WithParam("_parent_table", IdempotencyKeyStore.SchemaNameConst)
                .WithParam("_parent_id", parentId)
                .WithParam("_settlement_table", NodeFeeSettlement.SchemaNameConst)
                .WithParam("_settlement_id", settlement.Id)
                .WithParam("_parent_content", parentContent)
                .WithParam("_parent_key", SurrealIdempotencyStore.EncodeKeyForConfiguredLedger(parentKey))
                .WithParam("_parent_operation", parentOperation)
                .WithParam("_in_progress", IdempotencyKeyStore.StateKind.InProgress.ToString())
                .WithParam("_terminal_states", new[]
                {
                    NodeFeeSettlement.StateKind.Settled.ToString(),
                    NodeFeeSettlement.StateKind.Cancelled.ToString(),
                }),
            SurrealQuery
                .Of("IF $_parent = NONE AND $_settlement = NONE { CREATE type::record($_settlement_table, $_settlement_id) CONTENT $_settlement_content RETURN AFTER } ELSE { NONE }")
                .WithParam("_settlement_table", NodeFeeSettlement.SchemaNameConst)
                .WithParam("_settlement_id", settlement.Id)
                .WithParam("_settlement_content", BuildSettlementContent(settlement)),
            SurrealQuery
                .Of("SELECT * FROM type::record($_parent_table, $_parent_id)")
                .WithParam("_parent_table", IdempotencyKeyStore.SchemaNameConst)
                .WithParam("_parent_id", parentId),
            SurrealQuery
                .Of("SELECT * FROM type::record($_settlement_table, $_settlement_id)")
                .WithParam("_settlement_table", NodeFeeSettlement.SchemaNameConst)
                .WithParam("_settlement_id", settlement.Id),
            SurrealQuery.Of("COMMIT"));

        SurrealResponse response;
        try
        {
            response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var attempt = await _executor.ExecuteAsync(atomic, ct);
                attempt.EnsureAllOk();
                return attempt;
            }, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var replayParent = await _executor.QuerySingleAsync<IdempotencyKeyStore>(
                SurrealQuery<IdempotencyKeyStore>.Key(parentId),
                ct);
            var replaySettlement = await _executor.QuerySingleAsync<NodeFeeSettlement>(
                SurrealQuery<NodeFeeSettlement>.Key(settlement.Id),
                ct);
            if (replayParent is not null && replaySettlement is not null)
            {
                EnsurePairedAdmission(replayParent, replaySettlement, parentKey, parentOperation);
                return AZOAResult<NodeFeeSettlementAdmissionResult>.Success(
                    new NodeFeeSettlementAdmissionResult(
                        replaySettlement,
                        ToParentClaim(replayParent, parentKey),
                        NodeFeeSettlementAdmissionDisposition.Replayed),
                    "Settlement admission replayed.");
            }

            if (replayParent is not null && replaySettlement is null)
            {
                if (!IsExpectedParentClaim(replayParent, parentKey, parentOperation))
                {
                    return AZOAResult<NodeFeeSettlementAdmissionResult>.Failure(
                        "Parent idempotency key is already claimed by another operation.");
                }

                throw new InvalidOperationException(
                    "Node fee settlement admission failed with a partial durable pair.",
                    exception);
            }

            if (replayParent is null && replaySettlement is not null)
            {
                throw new InvalidOperationException(
                    "Node fee settlement admission failed with a partial durable pair.",
                    exception);
            }

            throw;
        }

        if (response.Count < 7)
            throw new InvalidOperationException("Node fee settlement admission returned an incomplete transaction response.");

        var parent = response.GetValues<IdempotencyKeyStore>(5).SingleOrDefault()
            ?? throw new InvalidOperationException("Node fee settlement admission returned no parent claim.");
        var persisted = response.GetValues<NodeFeeSettlement>(6).SingleOrDefault()
            ?? throw new InvalidOperationException("Node fee settlement admission returned no settlement.");
        var created = response.GetValues<NodeFeeSettlement>(4).Count == 1;
        EnsurePairedAdmission(parent, persisted, parentKey, parentOperation);

        return AZOAResult<NodeFeeSettlementAdmissionResult>.Success(
            new NodeFeeSettlementAdmissionResult(
                persisted,
                ToParentClaim(parent, parentKey),
                created
                    ? NodeFeeSettlementAdmissionDisposition.Created
                    : NodeFeeSettlementAdmissionDisposition.Replayed),
            created ? "Settlement admitted." : "Settlement admission replayed.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeSettlement?>> GetAsync(
        string settlementId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settlementId);
        var settlement = await _executor.QuerySingleAsync<NodeFeeSettlement>(
            SurrealQuery<NodeFeeSettlement>.Key(settlementId),
            ct);
        return AZOAResult<NodeFeeSettlement?>.Success(settlement, settlement is null ? "Not found." : "Success");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeAtomicGroup?>> GetAcceptedAtomicGroupAsync(
        string settlementId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settlementId);
        var canonicalSettlementId = settlementId.Trim();
        var receipt = await _executor.QuerySingleAsync<NodeFeeAtomicGroup>(
            SurrealQuery<NodeFeeAtomicGroup>.Key(NodeFeeAtomicGroup.RecordIdFor(canonicalSettlementId)),
            ct);
        if (receipt is null)
            return AZOAResult<NodeFeeAtomicGroup?>.Success(null, "Accepted atomic group receipt was not found.");

        if (!NodeFeeAtomicGroup.IsBoundToSettlement(receipt, canonicalSettlementId))
        {
            return AZOAResult<NodeFeeAtomicGroup?>.Failure(
                "Accepted atomic group receipt is not bound to the requested settlement.");
        }

        return AZOAResult<NodeFeeAtomicGroup?>.Success(receipt, "Accepted atomic group receipt loaded.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<NodeFeeSettlement>>> ListRecoverableAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        // raw: current consumed SurrealForge package lowercases enum predicates and
        // cannot express this uppercase schema enum's due-or-expired lease OR; waiver expires 2026-08-31.
        var query = SurrealQuery
            .Of("SELECT * FROM node_fee_settlement WHERE state INSIDE $_recoverable_states AND ((lease_token = NONE AND next_attempt_at <= $_now) OR lease_expires_at <= $_now) ORDER BY next_attempt_at ASC LIMIT $_limit")
            .WithParam("_recoverable_states", RecoverableStates.Select(state => state.ToString()).ToArray())
            .WithParam("_now", now)
            .WithParam("_limit", batchSize);
        var rows = await _executor.QueryAsync<NodeFeeSettlement>(query, ct);
        return AZOAResult<IReadOnlyList<NodeFeeSettlement>>.Success(rows, "Success");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeSettlement?>> TryClaimRecoveryAsync(
        NodeFeeSettlement candidate,
        string leaseToken,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (leaseExpiresAt <= now)
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));

        var nextVersion = checked(candidate.StateVersion + 1);
        var nextAttemptCount = checked(candidate.AttemptCount + 1);
        // raw: current consumed SurrealForge package has no typed multi-field CAS
        // with an ORed due-or-expired lease predicate; waiver expires 2026-08-31.
        var query = SurrealQuery
            .Of("UPDATE ONLY type::record($_t, $_id) SET lease_token = type::string($_lease_token), lease_expires_at = $_lease_expires_at, attempt_count = $_attempt_count, state_version = $_next_version, updated_at = $_now WHERE state_version = $_expected_version AND state INSIDE $_recoverable_states AND ((lease_token = NONE AND next_attempt_at <= $_now) OR lease_expires_at <= $_now) RETURN AFTER")
            .WithParam("_t", NodeFeeSettlement.SchemaNameConst)
            .WithParam("_id", candidate.Id)
            .WithParam("_lease_token", leaseToken)
            .WithParam("_lease_expires_at", leaseExpiresAt)
            .WithParam("_attempt_count", nextAttemptCount)
            .WithParam("_next_version", nextVersion)
            .WithParam("_expected_version", candidate.StateVersion)
            .WithParam("_recoverable_states", RecoverableStates.Select(state => state.ToString()).ToArray())
            .WithParam("_now", now);

        return await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await _executor.ExecuteAsync(query, ct);
            if (response.Count == 1 && response[0].IsOk && response[0].AffectedCount() == 1)
            {
                var claimed = response.GetValues<NodeFeeSettlement>(0).SingleOrDefault()
                    ?? throw new InvalidOperationException("Node fee settlement claim returned no row.");
                return AZOAResult<NodeFeeSettlement?>.Success(claimed, "Recovery lease claimed.");
            }

            if (response.Count == 1 && response[0].IsOk)
                return AZOAResult<NodeFeeSettlement?>.Success(null, "Recovery lease contention.");

            response.EnsureAllOk();
            throw new InvalidOperationException("Node fee settlement claim returned no statement result.");
        }, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeSettlement?>> TryClaimAcceptedAtomicGroupRecoveryAsync(
        NodeFeeSettlement candidate,
        string leaseToken,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (leaseExpiresAt <= now)
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));

        var nextVersion = checked(candidate.StateVersion + 1);
        var nextAttemptCount = checked(candidate.AttemptCount + 1);
        // raw: current consumed SurrealForge package cannot atomically combine this
        // exact recovery-lease CAS with the deterministic immutable receipt existence
        // predicate; waiver expires 2026-08-31.
        var query = SurrealQuery
            .Of("UPDATE ONLY type::record($_t, $_id) SET lease_token = type::string($_lease_token), lease_expires_at = $_lease_expires_at, attempt_count = $_attempt_count, state_version = $_next_version, updated_at = $_now WHERE state_version = $_expected_version AND state INSIDE $_recoverable_states AND ((lease_token = NONE AND next_attempt_at <= $_now) OR lease_expires_at <= $_now) AND (SELECT * FROM type::record($_receipt_table, $_receipt_id)).first() != NONE RETURN AFTER")
            .WithParam("_t", NodeFeeSettlement.SchemaNameConst)
            .WithParam("_id", candidate.Id)
            .WithParam("_lease_token", leaseToken)
            .WithParam("_lease_expires_at", leaseExpiresAt)
            .WithParam("_attempt_count", nextAttemptCount)
            .WithParam("_next_version", nextVersion)
            .WithParam("_expected_version", candidate.StateVersion)
            .WithParam("_recoverable_states", RecoverableStates.Select(state => state.ToString()).ToArray())
            .WithParam("_receipt_table", NodeFeeAtomicGroup.SchemaNameConst)
            .WithParam("_receipt_id", NodeFeeAtomicGroup.RecordIdFor(candidate.Id))
            .WithParam("_now", now);

        return await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await _executor.ExecuteAsync(query, ct);
            if (response.Count == 1 && response[0].IsOk && response[0].AffectedCount() == 1)
            {
                var claimed = response.GetValues<NodeFeeSettlement>(0).SingleOrDefault()
                    ?? throw new InvalidOperationException("Node fee accepted-group recovery claim returned no row.");
                return AZOAResult<NodeFeeSettlement?>.Success(claimed, "Accepted-group recovery lease claimed.");
            }

            if (response.Count == 1 && response[0].IsOk)
                return AZOAResult<NodeFeeSettlement?>.Success(null, "Accepted-group recovery claim contention or no receipt.");

            response.EnsureAllOk();
            throw new InvalidOperationException("Node fee accepted-group recovery claim returned no statement result.");
        }, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NodeFeeAtomicGroup?>> TryRecordAcceptedAtomicGroupAsync(
        NodeFeeSettlementRecoveryLease lease,
        NodeFeeAcceptedAtomicGroup acceptedGroup,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(acceptedGroup);
        if (now == default)
            throw new ArgumentOutOfRangeException(nameof(now));

        var request = acceptedGroup.Request ?? throw new ArgumentException("An atomic group request is required.", nameof(acceptedGroup));
        var submission = acceptedGroup.Submission ?? throw new ArgumentException("An atomic group submission is required.", nameof(acceptedGroup));
        ValidateAcceptedGroup(request, submission);

        var current = await _executor.QuerySingleAsync<NodeFeeSettlement>(
            SurrealQuery<NodeFeeSettlement>.Key(lease.SettlementId),
            ct);
        if (current is null)
            return AZOAResult<NodeFeeAtomicGroup?>.Success(null, "Settlement was not found.");
        if (!MatchesSettlementEconomics(current, request))
        {
            return AZOAResult<NodeFeeAtomicGroup?>.Failure(
                "Accepted atomic group does not match the settlement's immutable economics and routing.");
        }
        if (string.IsNullOrWhiteSpace(current.ExpectedAtomicGroupIdentity)
            || !string.Equals(current.ExpectedAtomicGroupIdentity, request.GroupIdentity, StringComparison.Ordinal))
        {
            return AZOAResult<NodeFeeAtomicGroup?>.Failure(
                "Settlement has no matching precommitted expected atomic group identity.");
        }

        var receiptId = NodeFeeAtomicGroup.RecordIdFor(lease.SettlementId);
        var existingReceipt = await _executor.QuerySingleAsync<NodeFeeAtomicGroup>(
            SurrealQuery<NodeFeeAtomicGroup>.Key(receiptId),
            ct);
        if (existingReceipt is not null)
        {
            return ReceiptMatches(existingReceipt, lease.SettlementId, request, submission)
                ? AZOAResult<NodeFeeAtomicGroup?>.Success(existingReceipt, "Accepted atomic group already recorded.")
                : AZOAResult<NodeFeeAtomicGroup?>.Failure("Accepted atomic group conflicts with the immutable receipt.");
        }

        var nextVersion = checked(lease.StateVersion + 1);
        var receiptState = ToReceiptState(submission.State);
        var primaryTransactionId = submission.PrimaryTransactionId;
        var treasuryTransactionId = submission.TreasuryTransactionId;
        var sourceAddress = request.Primary.FromAddress;
        var primaryRecipient = request.Primary.ToAddress;
        var nextAttemptAt = now;

        // raw: receipt creation and the leased settlement's transition must be one transaction;
        // current SurrealForge typed primitives cannot conditionally mutate the settlement and create its receipt. Waiver expires 2026-08-31.
        var atomic = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealQuery
                .Of("LET $_receipt = (SELECT * FROM type::record($_receipt_table, $_receipt_id)).first()")
                .WithParam("_receipt_table", NodeFeeAtomicGroup.SchemaNameConst)
                .WithParam("_receipt_id", receiptId),
            SurrealQuery
                .Of("LET $_updated = (IF $_receipt = NONE { (UPDATE ONLY type::record($_settlement_table, $_settlement_id) SET state = $_awaiting_reconciliation, primary_effect_state = $_submitted, primary_transaction_hash = array::join($_primary_transaction_id_chars, ''), fee_effect_state = $_submitted, fee_transaction_hash = array::join($_treasury_transaction_id_chars, ''), state_version = $_next_version, reconciliation_reason = type::string($_reason), next_attempt_at = $_next_attempt_at, lease_token = NONE, lease_expires_at = NONE, updated_at = $_now WHERE state_version = $_expected_version AND state = $_prepared AND primary_effect_state = $_not_started AND fee_effect_state = $_not_started AND primary_transaction_hash = NONE AND fee_transaction_hash = NONE AND lease_token = type::string($_lease_token) AND lease_expires_at > $_now AND parent_idempotency_key_hash = $_parent_key_hash AND expected_atomic_group_identity = $_expected_group_identity AND chain = $_chain AND network = $_network AND asset_id = $_asset_id AND gross_amount = $_gross_amount AND fee_amount = $_fee_amount AND net_amount = $_net_amount AND treasury_address = array::join($_treasury_address_chars, '') RETURN AFTER) } ELSE { NONE })")
                .WithParam("_settlement_table", NodeFeeSettlement.SchemaNameConst)
                .WithParam("_settlement_id", lease.SettlementId)
                .WithParam("_awaiting_reconciliation", NodeFeeSettlement.StateKind.AwaitingReconciliation.ToString())
                .WithParam("_submitted", NodeFeeSettlement.EffectStateKind.Submitted.ToString())
                .WithParam("_primary_transaction_id_chars", SurrealScalarString.ToCharacters(primaryTransactionId))
                .WithParam("_treasury_transaction_id_chars", SurrealScalarString.ToCharacters(treasuryTransactionId))
                .WithParam("_next_version", nextVersion)
                .WithParam("_reason", AcceptedGroupReconciliationReason)
                .WithParam("_next_attempt_at", nextAttemptAt)
                .WithParam("_expected_version", lease.StateVersion)
                .WithParam("_prepared", NodeFeeSettlement.StateKind.Prepared.ToString())
                .WithParam("_not_started", NodeFeeSettlement.EffectStateKind.NotStarted.ToString())
                .WithParam("_lease_token", lease.LeaseToken)
                .WithParam("_parent_key_hash", request.IdempotencyKeyHash)
                .WithParam("_expected_group_identity", request.GroupIdentity)
                .WithParam("_chain", request.ChainType.Trim().ToLowerInvariant())
                .WithParam("_network", request.Network.ToString())
                .WithParam("_asset_id", request.Primary.AssetId)
                .WithParam("_gross_amount", checked(request.Primary.Amount + request.Treasury.Amount).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithParam("_fee_amount", request.Treasury.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithParam("_net_amount", request.Primary.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithParam("_treasury_address_chars", SurrealScalarString.ToCharacters(request.Treasury.ToAddress))
                .WithParam("_now", now),
            SurrealQuery
                .Of("LET $_created = (IF $_updated != NONE { (CREATE ONLY type::record($_receipt_table, $_receipt_id) SET settlement_id = type::record($_settlement_table, $_settlement_id), group_identity = type::string($_group_identity), chain_group_id = array::join($_chain_group_id_chars, ''), source_address = array::join($_source_address_chars, ''), primary_recipient_address = array::join($_primary_recipient_chars, ''), primary_transaction_id = array::join($_primary_transaction_id_chars, ''), treasury_transaction_id = array::join($_treasury_transaction_id_chars, ''), state = $_receipt_state RETURN AFTER) } ELSE { NONE })")
                .WithParam("_receipt_table", NodeFeeAtomicGroup.SchemaNameConst)
                .WithParam("_receipt_id", receiptId)
                .WithParam("_settlement_table", NodeFeeSettlement.SchemaNameConst)
                .WithParam("_settlement_id", lease.SettlementId)
                .WithParam("_group_identity", request.GroupIdentity)
                .WithParam("_chain_group_id_chars", SurrealScalarString.ToCharacters(submission.ChainGroupId))
                .WithParam("_source_address_chars", SurrealScalarString.ToCharacters(sourceAddress))
                .WithParam("_primary_recipient_chars", SurrealScalarString.ToCharacters(primaryRecipient))
                .WithParam("_primary_transaction_id_chars", SurrealScalarString.ToCharacters(primaryTransactionId))
                .WithParam("_treasury_transaction_id_chars", SurrealScalarString.ToCharacters(treasuryTransactionId))
                .WithParam("_receipt_state", receiptState.ToString()),
            SurrealQuery
                .Of("SELECT * FROM type::record($_receipt_table, $_receipt_id)")
                .WithParam("_receipt_table", NodeFeeAtomicGroup.SchemaNameConst)
                .WithParam("_receipt_id", receiptId),
            SurrealQuery.Of("COMMIT"));

        return await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await _executor.ExecuteAsync(atomic, ct);
            response.EnsureAllOk();
            if (response.Count < 6)
                throw new InvalidOperationException("Accepted atomic group transaction returned an incomplete response.");

            var persisted = response.GetValues<NodeFeeAtomicGroup>(4).SingleOrDefault();
            if (persisted is null)
                return AZOAResult<NodeFeeAtomicGroup?>.Success(null, "Accepted atomic group lease contention.");
            return ReceiptMatches(persisted, lease.SettlementId, request, submission)
                ? AZOAResult<NodeFeeAtomicGroup?>.Success(persisted, "Accepted atomic group recorded.")
                : AZOAResult<NodeFeeAtomicGroup?>.Failure("Accepted atomic group conflicts with the immutable receipt.");
        }, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> TryDeferToReconciliationAsync(
        NodeFeeSettlementRecoveryLease lease,
        string reason,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (nextAttemptAt <= now)
            throw new ArgumentOutOfRangeException(nameof(nextAttemptAt));

        var nextVersion = checked(lease.StateVersion + 1);
        // raw: current consumed SurrealForge package has no typed multi-field CAS
        // with exact lease-token plus expiry guards; waiver expires 2026-08-31.
        var query = SurrealQuery
            .Of("UPDATE ONLY type::record($_t, $_id) SET state = $_awaiting_reconciliation, state_version = $_next_version, reconciliation_reason = type::string($_reason), next_attempt_at = $_next_attempt_at, lease_token = NONE, lease_expires_at = NONE, updated_at = $_now WHERE state_version = $_expected_version AND lease_token = type::string($_lease_token) AND lease_expires_at > $_now RETURN AFTER")
            .WithParam("_t", NodeFeeSettlement.SchemaNameConst)
            .WithParam("_id", lease.SettlementId)
            .WithParam("_awaiting_reconciliation", NodeFeeSettlement.StateKind.AwaitingReconciliation.ToString())
            .WithParam("_next_version", nextVersion)
            .WithParam("_reason", reason)
            .WithParam("_next_attempt_at", nextAttemptAt)
            .WithParam("_expected_version", lease.StateVersion)
            .WithParam("_lease_token", lease.LeaseToken)
            .WithParam("_now", now);

        return await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await _executor.ExecuteAsync(query, ct);
            if (response.Count == 1 && response[0].IsOk)
            {
                return AZOAResult<bool>.Success(
                    response[0].AffectedCount() == 1,
                    response[0].AffectedCount() == 1
                        ? "Recovery lease deferred for reconciliation."
                        : "Recovery lease contention.");
            }

            response.EnsureAllOk();
            throw new InvalidOperationException("Node fee settlement defer returned no statement result.");
        }, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> TryRecordNonTerminalReconciliationAsync(
        NodeFeeSettlementRecoveryLease lease,
        NodeFeeSettlementEffectReconciliation reconciliation,
        string reason,
        DateTimeOffset nextAttemptAt,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(reconciliation);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!reconciliation.IsNonTerminal)
        {
            throw new ArgumentException(
                "Nonterminal reconciliation must contain an Unknown or Failed effect.",
                nameof(reconciliation));
        }
        if (!IsReconciliationEffectState(reconciliation.PrimaryEffectState)
            || !IsReconciliationEffectState(reconciliation.FeeEffectState))
        {
            throw new ArgumentException(
                "Nonterminal reconciliation may only record Confirmed, Unknown, or Failed effects.",
                nameof(reconciliation));
        }
        if ((reconciliation.PrimaryEffectState == NodeFeeSettlement.EffectStateKind.Confirmed
             && string.IsNullOrWhiteSpace(reconciliation.PrimaryEffectReference))
            || (reconciliation.FeeEffectState == NodeFeeSettlement.EffectStateKind.Confirmed
                && string.IsNullOrWhiteSpace(reconciliation.FeeEffectReference)))
        {
            throw new ArgumentException(
                "A confirmed effect requires an observed effect reference.",
                nameof(reconciliation));
        }
        if (nextAttemptAt <= now)
            throw new ArgumentOutOfRangeException(nameof(nextAttemptAt));

        var primaryReference = NormalizeOptionalReference(reconciliation.PrimaryEffectReference);
        var feeReference = NormalizeOptionalReference(reconciliation.FeeEffectReference);
        var nextVersion = checked(lease.StateVersion + 1);
        // raw: current consumed SurrealForge package cannot express this exact
        // lease-token/expiry CAS with four effect fields; waiver expires 2026-08-31.
        var query = SurrealQuery
            .Of("UPDATE ONLY type::record($_t, $_id) SET state = $_awaiting_reconciliation, primary_effect_state = $_primary_state, primary_transaction_hash = IF $_has_primary_reference { array::join($_primary_reference_chars, '') } ELSE { NONE }, fee_effect_state = $_fee_state, fee_transaction_hash = IF $_has_fee_reference { array::join($_fee_reference_chars, '') } ELSE { NONE }, state_version = $_next_version, reconciliation_reason = type::string($_reason), next_attempt_at = $_next_attempt_at, lease_token = NONE, lease_expires_at = NONE, updated_at = $_now WHERE state_version = $_expected_version AND state INSIDE $_recoverable_states AND lease_token = type::string($_lease_token) AND lease_expires_at > $_now AND (primary_effect_state != $_confirmed OR (primary_effect_state = $_confirmed AND $_primary_state = $_confirmed AND primary_transaction_hash = IF $_has_primary_reference { array::join($_primary_reference_chars, '') } ELSE { NONE })) AND (fee_effect_state != $_confirmed OR (fee_effect_state = $_confirmed AND $_fee_state = $_confirmed AND fee_transaction_hash = IF $_has_fee_reference { array::join($_fee_reference_chars, '') } ELSE { NONE })) RETURN AFTER")
            .WithParam("_t", NodeFeeSettlement.SchemaNameConst)
            .WithParam("_id", lease.SettlementId)
            .WithParam("_awaiting_reconciliation", NodeFeeSettlement.StateKind.AwaitingReconciliation.ToString())
            .WithParam("_primary_state", reconciliation.PrimaryEffectState.ToString())
            .WithParam("_has_primary_reference", primaryReference is not null)
            .WithParam("_primary_reference_chars", SurrealScalarString.ToCharacters(primaryReference))
            .WithParam("_fee_state", reconciliation.FeeEffectState.ToString())
            .WithParam("_has_fee_reference", feeReference is not null)
            .WithParam("_fee_reference_chars", SurrealScalarString.ToCharacters(feeReference))
            .WithParam("_next_version", nextVersion)
            .WithParam("_reason", reason.Trim())
            .WithParam("_next_attempt_at", nextAttemptAt)
            .WithParam("_expected_version", lease.StateVersion)
            .WithParam("_recoverable_states", RecoverableStates.Select(state => state.ToString()).ToArray())
            .WithParam("_lease_token", lease.LeaseToken)
            .WithParam("_confirmed", NodeFeeSettlement.EffectStateKind.Confirmed.ToString())
            .WithParam("_now", now);

        return await ExecuteExpectedCasAsync(
            query,
            ct,
            "Nonterminal reconciliation recorded.",
            "Nonterminal reconciliation contention.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> TrySettlePairedAsync(
        NodeFeeSettlementRecoveryLease lease,
        NodeFeeSettlementTerminalization terminalization,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(terminalization);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalization.ParentIdempotencyKeyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalization.PrimaryEffectReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalization.FeeEffectReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalization.ParentResultPayload);

        var primaryReference = terminalization.PrimaryEffectReference.Trim();
        var feeReference = terminalization.FeeEffectReference.Trim();
        if (string.Equals(primaryReference, feeReference, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Confirmed primary and fee effects must have distinct references.",
                nameof(terminalization));
        }

        var current = await _executor.QuerySingleAsync<NodeFeeSettlement>(
            SurrealQuery<NodeFeeSettlement>.Key(lease.SettlementId),
            ct);
        if (current is null)
            return AZOAResult<bool>.Success(false, "Settlement was not found.");

        var parentId = terminalization.ParentIdempotencyKeyHash;
        if (!string.Equals(current.ParentIdempotencyKeyHash, parentId, StringComparison.Ordinal))
            return AZOAResult<bool>.Success(false, "Settlement parent mismatch.");
        if (!PreservesConfirmedEffect(
                current.PrimaryEffectState,
                current.PrimaryTransactionHash,
                primaryReference)
            || !PreservesConfirmedEffect(
                current.FeeEffectState,
                current.FeeTransactionHash,
                feeReference))
        {
            return AZOAResult<bool>.Success(false, "Settlement effect reference conflict.");
        }

        var parentOperation = NodeFeeSettlement.ParentClaimOperationType(current.Operation);
        var nextVersion = checked(lease.StateVersion + 1);
        var parentKey = terminalization.ParentIdempotencyKey;
        var encodedParentKey = parentKey is null
            ? string.Empty
            : SurrealIdempotencyStore.EncodeKeyForConfiguredLedger(parentKey);

        // raw: settlement and its independent parent claim must become terminal
        // together; current typed primitives cannot conditionally mutate both tables. Waiver expires 2026-08-31.
        var atomic = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealQuery
                .Of("LET $_parent = (SELECT * FROM type::record($_parent_table, $_parent_id)).first()")
                .WithParam("_parent_table", IdempotencyKeyStore.SchemaNameConst)
                .WithParam("_parent_id", parentId),
            SurrealQuery
                .Of("LET $_settled = (IF $_parent != NONE AND $_parent.state = $_in_progress AND ($_require_parent_key = false OR $_parent.key = type::string($_parent_key)) AND $_parent.operation_type = type::string($_parent_operation) { (UPDATE ONLY type::record($_settlement_table, $_settlement_id) SET state = $_settled_state, primary_effect_state = $_confirmed, primary_transaction_hash = array::join($_primary_reference_chars, ''), fee_effect_state = $_confirmed, fee_transaction_hash = array::join($_fee_reference_chars, ''), state_version = $_next_version, reconciliation_reason = NONE, lease_token = NONE, lease_expires_at = NONE, updated_at = $_now WHERE state_version = $_expected_version AND state INSIDE $_recoverable_states AND lease_token = type::string($_lease_token) AND lease_expires_at > $_now AND (primary_effect_state != $_confirmed OR primary_transaction_hash = array::join($_primary_reference_chars, '')) AND (fee_effect_state != $_confirmed OR fee_transaction_hash = array::join($_fee_reference_chars, '')) RETURN AFTER) } ELSE { NONE })")
                .WithParam("_parent_table", IdempotencyKeyStore.SchemaNameConst)
                .WithParam("_parent_id", parentId)
                .WithParam("_in_progress", IdempotencyKeyStore.StateKind.InProgress.ToString())
                .WithParam("_require_parent_key", parentKey is not null)
                .WithParam("_parent_key", encodedParentKey)
                .WithParam("_parent_operation", parentOperation)
                .WithParam("_settlement_table", NodeFeeSettlement.SchemaNameConst)
                .WithParam("_settlement_id", lease.SettlementId)
                .WithParam("_settled_state", NodeFeeSettlement.StateKind.Settled.ToString())
                .WithParam("_confirmed", NodeFeeSettlement.EffectStateKind.Confirmed.ToString())
                .WithParam("_primary_reference_chars", SurrealScalarString.ToCharacters(primaryReference))
                .WithParam("_fee_reference_chars", SurrealScalarString.ToCharacters(feeReference))
                .WithParam("_next_version", nextVersion)
                .WithParam("_expected_version", lease.StateVersion)
                .WithParam("_recoverable_states", RecoverableStates.Select(state => state.ToString()).ToArray())
                .WithParam("_lease_token", lease.LeaseToken)
                .WithParam("_now", now),
            SurrealQuery
                .Of("LET $_completed_parent = (IF $_settled = NONE { NONE } ELSE { (UPDATE ONLY type::record($_parent_table, $_parent_id) SET state = $_completed, result_payload = type::string($_payload), error = NONE, updated_at = $_now WHERE state = $_in_progress AND ($_require_parent_key = false OR key = type::string($_parent_key)) AND operation_type = type::string($_parent_operation) RETURN AFTER) })")
                .WithParam("_parent_table", IdempotencyKeyStore.SchemaNameConst)
                .WithParam("_parent_id", parentId)
                .WithParam("_completed", IdempotencyKeyStore.StateKind.Completed.ToString())
                .WithParam("_payload", terminalization.ParentResultPayload.Trim())
                .WithParam("_in_progress", IdempotencyKeyStore.StateKind.InProgress.ToString())
                .WithParam("_require_parent_key", parentKey is not null)
                .WithParam("_parent_key", encodedParentKey)
                .WithParam("_parent_operation", parentOperation)
                .WithParam("_now", now),
            SurrealQuery.Of("IF $_settled != NONE AND $_completed_parent = NONE { THROW 'Node fee settlement parent completion contention' } ELSE { $_completed_parent }"),
            SurrealQuery
                .Of("SELECT * FROM type::record($_parent_table, $_parent_id)")
                .WithParam("_parent_table", IdempotencyKeyStore.SchemaNameConst)
                .WithParam("_parent_id", parentId),
            SurrealQuery
                .Of("SELECT * FROM type::record($_settlement_table, $_settlement_id)")
                .WithParam("_settlement_table", NodeFeeSettlement.SchemaNameConst)
                .WithParam("_settlement_id", lease.SettlementId),
            SurrealQuery.Of("COMMIT"));

        return await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await _executor.ExecuteAsync(atomic, ct);
            response.EnsureAllOk();
            if (response.Count < 8)
                throw new InvalidOperationException("Node fee settlement terminalization returned an incomplete transaction response.");

            var completedParent = response.GetValues<IdempotencyKeyStore>(4).SingleOrDefault();
            if (completedParent is null)
                return AZOAResult<bool>.Success(false, "Settlement terminalization contention.");

            var persistedParent = response.GetValues<IdempotencyKeyStore>(5).SingleOrDefault()
                ?? throw new InvalidOperationException("Node fee settlement terminalization returned no parent claim.");
            var persistedSettlement = response.GetValues<NodeFeeSettlement>(6).SingleOrDefault()
                ?? throw new InvalidOperationException("Node fee settlement terminalization returned no settlement.");
            EnsurePairedTerminalization(
                persistedParent,
                persistedSettlement,
                parentId,
                parentKey,
                parentOperation,
                primaryReference,
                feeReference,
                terminalization.ParentResultPayload.Trim());
            return AZOAResult<bool>.Success(true, "Settlement and parent claim completed together.");
        }, ct);
    }

    private async Task<AZOAResult<bool>> ExecuteExpectedCasAsync(
        SurrealQuery query,
        CancellationToken ct,
        string successMessage,
        string contentionMessage)
        => await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await _executor.ExecuteAsync(query, ct);
            if (response.Count == 1 && response[0].IsOk)
            {
                return AZOAResult<bool>.Success(
                    response[0].AffectedCount() == 1,
                    response[0].AffectedCount() == 1 ? successMessage : contentionMessage);
            }

            response.EnsureAllOk();
            throw new InvalidOperationException("Node fee settlement conditional mutation returned no statement result.");
        }, ct);

    private static void ValidateAcceptedGroup(
        AtomicTransferGroupRequest request,
        AtomicTransferGroupSubmission submission)
    {
        if (!IsCanonicalSha256Digest(request.GroupIdentity)
            || !IsCanonicalSha256Digest(request.IdempotencyKeyHash)
            || !string.Equals(request.GroupIdentity, submission.GroupIdentity, StringComparison.Ordinal))
        {
            throw new ArgumentException("Accepted atomic group evidence must be bound to one canonical request identity.");
        }

        if (!Enum.IsDefined(request.Network)
            || request.Primary.Amount == 0
            || request.Treasury.Amount == 0
            || string.IsNullOrWhiteSpace(request.ChainType)
            || string.IsNullOrWhiteSpace(request.Primary.AssetId)
            || !string.Equals(request.Primary.AssetId, request.Treasury.AssetId, StringComparison.Ordinal)
            || !string.Equals(request.Primary.FromAddress, request.Treasury.FromAddress, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.Primary.FromAddress)
            || string.IsNullOrWhiteSpace(request.Primary.ToAddress)
            || string.IsNullOrWhiteSpace(request.Treasury.ToAddress)
            || string.Equals(request.Primary.ToAddress, request.Treasury.ToAddress, StringComparison.Ordinal)
            || request.Primary.SigningContext != request.Treasury.SigningContext)
        {
            throw new ArgumentException("Accepted atomic group evidence has an invalid two-leg request binding.");
        }

        if (submission.State is AtomicTransferGroupSubmissionState.NotSubmitted
            || string.IsNullOrWhiteSpace(submission.ChainGroupId)
            || string.IsNullOrWhiteSpace(submission.PrimaryTransactionId)
            || string.IsNullOrWhiteSpace(submission.TreasuryTransactionId)
            || !HasNoSurroundingWhitespace(submission.ChainGroupId)
            || !HasNoSurroundingWhitespace(submission.PrimaryTransactionId)
            || !HasNoSurroundingWhitespace(submission.TreasuryTransactionId)
            || string.Equals(submission.PrimaryTransactionId, submission.TreasuryTransactionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Accepted atomic group evidence must contain distinct, non-empty chain identifiers.");
        }
    }

    private static bool MatchesSettlementEconomics(
        NodeFeeSettlement settlement,
        AtomicTransferGroupRequest request)
    {
        var gross = (UInt128)request.Primary.Amount + request.Treasury.Amount;
        return gross <= ulong.MaxValue
           && string.Equals(settlement.ParentIdempotencyKeyHash, request.IdempotencyKeyHash, StringComparison.Ordinal)
           && string.Equals(settlement.Chain, request.ChainType.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(settlement.Network, request.Network.ToString(), StringComparison.Ordinal)
           && string.Equals(settlement.AssetId, request.Primary.AssetId, StringComparison.Ordinal)
           && string.Equals(settlement.GrossAmount, gross.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
           && string.Equals(settlement.FeeAmount, request.Treasury.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
           && string.Equals(settlement.NetAmount, request.Primary.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
           && string.Equals(settlement.TreasuryAddress, request.Treasury.ToAddress, StringComparison.Ordinal);
    }

    private static bool ReceiptMatches(
        NodeFeeAtomicGroup receipt,
        string settlementId,
        AtomicTransferGroupRequest request,
        AtomicTransferGroupSubmission submission)
    {
        return NodeFeeAtomicGroup.IsBoundToSettlement(receipt, settlementId)
           && string.Equals(receipt.GroupIdentity, request.GroupIdentity, StringComparison.Ordinal)
           && string.Equals(receipt.ChainGroupId, submission.ChainGroupId, StringComparison.Ordinal)
           && string.Equals(receipt.SourceAddress, request.Primary.FromAddress, StringComparison.Ordinal)
           && string.Equals(receipt.PrimaryRecipientAddress, request.Primary.ToAddress, StringComparison.Ordinal)
           && string.Equals(receipt.PrimaryTransactionId, submission.PrimaryTransactionId, StringComparison.Ordinal)
           && string.Equals(receipt.TreasuryTransactionId, submission.TreasuryTransactionId, StringComparison.Ordinal)
           && receipt.State == ToReceiptState(submission.State);
    }

    private static NodeFeeAtomicGroup.StateKind ToReceiptState(AtomicTransferGroupSubmissionState state)
        => state switch
        {
            AtomicTransferGroupSubmissionState.Submitted => NodeFeeAtomicGroup.StateKind.Submitted,
            AtomicTransferGroupSubmissionState.PendingConfirmation => NodeFeeAtomicGroup.StateKind.PendingConfirmation,
            AtomicTransferGroupSubmissionState.Confirmed => NodeFeeAtomicGroup.StateKind.Confirmed,
            _ => throw new ArgumentOutOfRangeException(nameof(state), "An accepted atomic group cannot be NotSubmitted."),
        };

    private static bool IsCanonicalSha256Digest(string value)
        => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasNoSurroundingWhitespace(string value)
        => string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static Dictionary<string, object?> BuildSettlementContent(NodeFeeSettlement settlement)
    {
        var content = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = settlement.Id,
            ["parent_idempotency_key_hash"] = settlement.ParentIdempotencyKeyHash,
            ["operation"] = settlement.Operation,
            ["chain"] = settlement.Chain,
            ["network"] = settlement.Network,
            ["asset_id"] = settlement.AssetId,
            ["gross_amount"] = settlement.GrossAmount,
            ["fee_amount"] = settlement.FeeAmount,
            ["net_amount"] = settlement.NetAmount,
            ["fee_schedule_version"] = settlement.FeeScheduleVersion,
            ["treasury_address"] = settlement.TreasuryAddress,
            ["treasury_destination_version"] = settlement.TreasuryDestinationVersion,
            ["state"] = NodeFeeSettlement.StateKind.Prepared.ToString(),
            ["primary_effect_state"] = NodeFeeSettlement.EffectStateKind.NotStarted.ToString(),
            ["fee_effect_state"] = NodeFeeSettlement.EffectStateKind.NotStarted.ToString(),
            ["state_version"] = 0,
            ["attempt_count"] = 0,
            ["next_attempt_at"] = settlement.NextAttemptAt,
            ["updated_at"] = settlement.UpdatedAt,
        };

        if (settlement.ExpectedAtomicGroupIdentity is not null)
            content["expected_atomic_group_identity"] = settlement.ExpectedAtomicGroupIdentity;

        return content;
    }

    private static DomainIdempotencyRecord ToParentClaim(IdempotencyKeyStore parent, string parentKey) => new()
    {
        Key = parentKey,
        OperationType = parent.OperationType,
        State = parent.State switch
        {
            IdempotencyKeyStore.StateKind.Completed => DomainIdempotencyState.Completed,
            IdempotencyKeyStore.StateKind.Failed => DomainIdempotencyState.Failed,
            _ => DomainIdempotencyState.InProgress,
        },
        ResultPayload = parent.ResultPayload,
        Error = parent.Error,
        CreatedAt = parent.CreatedAt.UtcDateTime,
        UpdatedAt = parent.UpdatedAt.UtcDateTime,
    };

    private static bool IsReconciliationEffectState(NodeFeeSettlement.EffectStateKind state)
        => state is NodeFeeSettlement.EffectStateKind.Confirmed
            or NodeFeeSettlement.EffectStateKind.Unknown
            or NodeFeeSettlement.EffectStateKind.Failed;

    private static string? NormalizeOptionalReference(string? reference)
        => string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();

    private static bool PreservesConfirmedEffect(
        NodeFeeSettlement.EffectStateKind storedState,
        string? storedReference,
        string incomingReference)
        => storedState != NodeFeeSettlement.EffectStateKind.Confirmed
           || string.Equals(storedReference, incomingReference, StringComparison.Ordinal);

    private static void EnsurePairedTerminalization(
        IdempotencyKeyStore parent,
        NodeFeeSettlement settlement,
        string parentId,
        string? parentKey,
        string parentOperation,
        string primaryReference,
        string feeReference,
        string parentPayload)
    {
        var parentMatches = string.Equals(settlement.ParentIdempotencyKeyHash, parentId, StringComparison.Ordinal)
            && string.Equals(parent.OperationType, parentOperation, StringComparison.Ordinal)
            && (parentKey is null || IsExpectedParentClaim(parent, parentKey, parentOperation));
        var terminalStateMatches = parent.State == IdempotencyKeyStore.StateKind.Completed
            && settlement.State == NodeFeeSettlement.StateKind.Settled;
        var primaryReferenceMatches = settlement.PrimaryEffectState == NodeFeeSettlement.EffectStateKind.Confirmed
            && string.Equals(settlement.PrimaryTransactionHash, primaryReference, StringComparison.Ordinal);
        var feeReferenceMatches = settlement.FeeEffectState == NodeFeeSettlement.EffectStateKind.Confirmed
            && string.Equals(settlement.FeeTransactionHash, feeReference, StringComparison.Ordinal);
        var referencesMatch = primaryReferenceMatches && feeReferenceMatches;
        var parentResultMatches = string.Equals(parent.ResultPayload, parentPayload, StringComparison.Ordinal)
            && parent.Error is null;
        var leaseReleased = settlement.LeaseToken is null && !settlement.LeaseExpiresAt.HasValue;
        if (!parentMatches || !terminalStateMatches || !referencesMatch || !parentResultMatches || !leaseReleased)
        {
            throw new InvalidOperationException(
                "Node fee settlement terminal transaction did not persist a complete paired terminal state: " +
                $"parentMatches={parentMatches}, terminalStateMatches={terminalStateMatches}, " +
                $"primaryReferenceMatches={primaryReferenceMatches}, feeReferenceMatches={feeReferenceMatches}, " +
                $"parentResultMatches={parentResultMatches}, leaseReleased={leaseReleased}.");
        }
    }

    private static void EnsurePairedAdmission(
        IdempotencyKeyStore parent,
        NodeFeeSettlement settlement,
        string parentKey,
        string parentOperation)
    {
        if (!IsExpectedParentClaim(parent, parentKey, parentOperation))
        {
            throw new InvalidOperationException("Node fee settlement parent claim conflict.");
        }

        var parentIsTerminal = parent.State is IdempotencyKeyStore.StateKind.Completed
            or IdempotencyKeyStore.StateKind.Failed;
        var settlementIsTerminal = settlement.State is NodeFeeSettlement.StateKind.Settled
            or NodeFeeSettlement.StateKind.Cancelled;
        if (parentIsTerminal != settlementIsTerminal)
        {
            throw new InvalidOperationException(
                "Node fee settlement parent and settlement terminal states disagree.");
        }
    }

    private static bool IsExpectedParentClaim(
        IdempotencyKeyStore parent,
        string parentKey,
        string parentOperation)
        => string.Equals(parent.Key, SurrealIdempotencyStore.EncodeKeyForConfiguredLedger(parentKey), StringComparison.Ordinal)
           && string.Equals(parent.OperationType, parentOperation, StringComparison.Ordinal);

    private static void EnsurePreparedAdmission(
        NodeFeeSettlement settlement,
        string parentKey,
        string parentId)
    {
        if (!string.Equals(settlement.ParentIdempotencyKeyHash, parentId, StringComparison.Ordinal)
            || !string.Equals(settlement.Id,
                NodeFeeSettlement.RecordIdFor(parentKey, settlement.Operation),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Settlement identity must be derived from the supplied parent idempotency key and operation.",
                nameof(settlement));
        }

        if (settlement.State != NodeFeeSettlement.StateKind.Prepared
            || settlement.PrimaryEffectState != NodeFeeSettlement.EffectStateKind.NotStarted
            || settlement.FeeEffectState != NodeFeeSettlement.EffectStateKind.NotStarted
            || settlement.PrimaryOperationId is not null
            || settlement.FeeOperationId is not null
            || settlement.PrimaryTransactionHash is not null
            || settlement.FeeTransactionHash is not null
            || settlement.LeaseToken is not null
            || settlement.LeaseExpiresAt.HasValue
            || settlement.ReconciliationReason is not null
            || settlement.StateVersion != 0
            || settlement.AttemptCount != 0)
        {
            throw new ArgumentException(
                "Settlement admission accepts only an inert prepared row with no effects, lease, reconciliation, or lifecycle history.",
                nameof(settlement));
        }

        if (settlement.NextAttemptAt == default || settlement.UpdatedAt == default)
        {
            throw new ArgumentException(
                "Prepared settlement timestamps are required for recovery scheduling.",
                nameof(settlement));
        }

        if (!NodeFeeSettlement.TryParseCanonicalPositiveBaseUnitAmount(settlement.GrossAmount, out var gross)
            || !NodeFeeSettlement.TryParseCanonicalPositiveBaseUnitAmount(settlement.FeeAmount, out var fee)
            || !NodeFeeSettlement.TryParseCanonicalPositiveBaseUnitAmount(settlement.NetAmount, out var net)
            || (UInt128)fee + net != gross)
        {
            throw new ArgumentException(
                "Settlement amounts must be canonical positive unsigned 64-bit base-unit strings satisfying gross = fee + net.",
                nameof(settlement));
        }

        if (settlement.ExpectedAtomicGroupIdentity is not null
            && !IsCanonicalSha256Digest(settlement.ExpectedAtomicGroupIdentity))
        {
            throw new ArgumentException(
                "Settlement expected atomic group identity must be a canonical lowercase SHA-256 digest.",
                nameof(settlement));
        }
    }
}
