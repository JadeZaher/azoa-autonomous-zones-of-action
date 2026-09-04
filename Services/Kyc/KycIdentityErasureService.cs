// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// Retention + erasure for the identity binding AND the AML screening trail (D25).
/// Erasure tombstones the retained personal fields but PRESERVES the salted person
/// keys, the screening verdicts, and the provider ids, so a GDPR Art. 17 request and
/// KYC record-keeping can both be satisfied: uniqueness, audit, and the avatar links
/// survive an erasure that removes every readable personal attribute.
///
/// <para>The screening tables are in scope deliberately. <c>kyc_screening_result</c>
/// and <c>kyc_screening_monitor</c> each retain a full name and a date of birth as
/// the vendor's query key, so erasing only <c>kyc_identity</c> would leave a name and
/// a birthday on the node forever and make the erasure a fiction.</para>
/// </summary>
public interface IKycIdentityErasureService
{
    /// <summary>Erases one binding on request, e.g. an operator-honoured Art. 17 case.</summary>
    Task<AZOAResult<KycIdentity>> EraseAsync(
        string recordId,
        string reason,
        CancellationToken ct = default);

    /// <summary>Erases every binding and screening row whose retention window elapsed.</summary>
    Task<AZOAResult<int>> RunRetentionSweepAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class KycIdentityErasureService : IKycIdentityErasureService
{
    /// <summary>Reason stamped by the automatic sweep, as opposed to a manual request.</summary>
    public const string RetentionElapsedReason = "retention-period-elapsed";

    private const int SweepBatchSize = 200;

    private readonly IKycIdentityStore _store;
    private readonly IKycScreeningStore _screeningStore;
    private readonly ILogger<KycIdentityErasureService> _logger;
    private readonly TimeProvider _clock;

    public KycIdentityErasureService(
        IKycIdentityStore store,
        IKycScreeningStore screeningStore,
        ILogger<KycIdentityErasureService> logger,
        TimeProvider? clock = null)
    {
        _store = store;
        _screeningStore = screeningStore;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AZOAResult<KycIdentity>> EraseAsync(
        string recordId,
        string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recordId))
            return AZOAResult<KycIdentity>.Failure("A binding id is required.");

        // The reason is operator metadata and must stay non-personal: it is retained
        // after the personal fields are gone.
        var safeReason = string.IsNullOrWhiteSpace(reason)
            ? "operator-request"
            : reason.Trim()[..Math.Min(reason.Trim().Length, 128)];
        var erasedAt = _clock.GetUtcNow();

        var erased = await _store.EraseAsync(recordId, safeReason, erasedAt, ct);
        if (erased.IsError || erased.Result is null)
            return erased;

        await EraseScreeningTrailAsync(erased.Result, safeReason, erasedAt, ct);
        return erased;
    }

    public async Task<AZOAResult<int>> RunRetentionSweepAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var due = await _store.GetErasureDueAsync(now, SweepBatchSize, ct);
        if (due.IsError)
        {
            return AZOAResult<int>.FailureWithCode(
                due.Message,
                due.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        var erased = 0;
        foreach (var binding in due.Result ?? [])
        {
            var result = await _store.EraseAsync(binding.Id, RetentionElapsedReason, now, ct);
            if (result.IsError)
            {
                _logger.LogWarning("Retention erasure failed for one identity binding; it will be retried.");
                continue;
            }

            erased++;
            await EraseScreeningTrailAsync(result.Result ?? binding, RetentionElapsedReason, now, ct);
        }

        // Screening rows carry their OWN retention stamp and outlive the binding they
        // came from (a poll writes a new result long after the first one), so the
        // sweep must also erase them on their own schedule rather than only in the
        // slipstream of a binding erasure.
        var screening = await _screeningStore.EraseRetentionExpiredAsync(
            now,
            RetentionElapsedReason,
            SweepBatchSize,
            ct);
        if (screening.IsError)
        {
            _logger.LogWarning(
                "Retention erasure of the AML screening trail failed; it will be retried next sweep.");
        }
        else
        {
            erased += screening.Result;
        }

        if (erased > 0)
            _logger.LogInformation("Retention sweep erased {Count} record(s).", erased);

        return AZOAResult<int>.Success(erased);
    }

    private async Task EraseScreeningTrailAsync(
        KycIdentity binding,
        string reason,
        DateTimeOffset erasedAt,
        CancellationToken ct)
    {
        var avatarIds = binding.AvatarIds ?? [];
        if (avatarIds.Count == 0)
            return;

        Guid? tenantId = Guid.TryParseExact(binding.TenantId ?? string.Empty, "N", out var parsed)
            ? parsed
            : null;

        var screening = await _screeningStore.EraseSubjectAsync(
            avatarIds,
            tenantId,
            reason,
            erasedAt,
            ct);
        if (screening.IsError)
        {
            // The binding is already tombstoned; leaving the name and birthday on the
            // screening rows is exactly the D25 gap this exists to close, so it is an
            // error even though the caller's own request succeeded.
            _logger.LogError(
                "An identity binding was erased but its AML screening trail was not; a name and date of birth remain on kyc_screening_result / kyc_screening_monitor and must be swept.");
        }
    }
}
