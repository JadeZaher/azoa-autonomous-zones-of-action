// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core.Surreal;   // SurrealRecordParam (record<> binding, never a bare hex id)
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fiat.Reversal;

/// <summary>
/// Finds the issuance a reversal is about, from the correlation a reversal event actually
/// carries.
/// </summary>
/// <remarks>
/// <para>A reversal names a CHARGE or a PAYMENT; it does not name the node's own funding intent.
/// A <c>charge.refunded</c> body has no <c>client_reference_id</c> and a dispute object carries
/// its own (empty) metadata rather than the payment's, so the intent id the on-ramp keys
/// everything on is simply not in the delivery. The durable link back is
/// <c>on_ramp_lot.source_payment_intent_id</c> / <c>source_charge_id</c>, and the committed
/// schema already indexes both (<c>on_ramp_lot_payment</c>, <c>on_ramp_lot_charge</c>) for
/// exactly this read.</para>
/// <para>Read-only, and deliberately NOT an extension of <see cref="OnRamp.IOnRampLotLedger"/>:
/// <c>Services/Fiat/OnRamp/</c> belongs to the on-ramp item. The interface is what the pipeline
/// depends on, so folding it into the ledger later is a file move.</para>
/// </remarks>
public interface IOnRampLotLookup
{
    /// <summary>The lot a payment settled into, or a null result when there is none.</summary>
    Task<AZOAResult<OnRampLot?>> GetByPaymentIntentAsync(
        string paymentIntentId, CancellationToken ct = default);

    /// <summary>The lot a charge settled into, or a null result when there is none.</summary>
    Task<AZOAResult<OnRampLot?>> GetByChargeAsync(string chargeId, CancellationToken ct = default);

    /// <summary>
    /// Every lot that ever put units into one person's account, oldest first: the FIFO frame the
    /// per-lot purchase attribution is derived in.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately WIDER than <c>IOnRampLotLedger.ListIssuedForUserAsync</c>, which is the
    /// cash-out rule's list and excludes reversed lots. Attribution needs its frame to be stable:
    /// if a reversed lot dropped out of the order, every later lot would shift earlier and a
    /// purchase already attributed to lot A would silently re-attribute to lot B. Refused and
    /// stuck lots stay excluded -- they credited nothing, so they can have funded nothing.</para>
    /// <para>Ordered by <c>issued_at</c>, not <c>created_at</c>: that is the instant the units
    /// became spendable, and a purchase can only have been funded by a lot issued before it.</para>
    /// </remarks>
    Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListForSubjectAsync(
        Guid subjectUserId, CancellationToken ct = default);
}

/// <summary>SurrealDB-backed lot lookup over the committed provider-correlation indexes.</summary>
public sealed class SurrealOnRampLotLookup : IOnRampLotLookup
{
    private const string ByPaymentStatement =
        "SELECT * FROM on_ramp_lot WHERE source_payment_intent_id = $_payment "
        + "ORDER BY created_at DESC LIMIT 1";

    private const string ByChargeStatement =
        "SELECT * FROM on_ramp_lot WHERE source_charge_id = $_charge "
        + "ORDER BY created_at DESC LIMIT 1";

    private const string ForSubjectStatement =
        "SELECT * FROM on_ramp_lot WHERE subject_user_id = $_user AND status INSIDE $_statuses "
        + "ORDER BY issued_at ASC";

    /// <summary>The statuses under which a lot's units actually reached the person's account.</summary>
    private static readonly string[] CreditedStatuses =
    {
        OnRamp.OnRampLotStatuses.Issued,
        OnRamp.OnRampLotStatuses.Reversed,
    };

    private readonly ISurrealExecutor _executor;

    public SurrealOnRampLotLookup(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc />
    public Task<AZOAResult<OnRampLot?>> GetByPaymentIntentAsync(
        string paymentIntentId, CancellationToken ct = default)
        => FirstAsync(ByPaymentStatement, "_payment", paymentIntentId, ct);

    /// <inheritdoc />
    public Task<AZOAResult<OnRampLot?>> GetByChargeAsync(string chargeId, CancellationToken ct = default)
        => FirstAsync(ByChargeStatement, "_charge", chargeId, ct);

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<OnRampLot>>> ListForSubjectAsync(
        Guid subjectUserId, CancellationToken ct = default)
    {
        if (subjectUserId == Guid.Empty)
            return AZOAResult<IReadOnlyList<OnRampLot>>.Success(Array.Empty<OnRampLot>(), "Success");

        try
        {
            // subject_user_id is a record<user> column, so the parameter has to be bound as a
            // native record id -- a bare string compares equal to nothing and would answer "this
            // person has no on-ramped units" with no error at all.
            var query = SurrealQuery
                .Of(ForSubjectStatement)
                .WithParam("_user", SurrealRecordParam.Of(User.SchemaNameConst, subjectUserId.ToString("N")))
                .WithParam("_statuses", CreditedStatuses);

            var rows = await _executor.QueryAsync<OnRampLot>(query, ct);
            return AZOAResult<IReadOnlyList<OnRampLot>>.Success(
                rows?.ToList() ?? new List<OnRampLot>(), "Success");
        }
        catch (Exception ex)
        {
            return new AZOAResult<IReadOnlyList<OnRampLot>>()
                .CaptureException(ex, $"On-ramp lot read failed: {ex.Message}");
        }
    }

    private async Task<AZOAResult<OnRampLot?>> FirstAsync(
        string statement, string parameterName, string value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new AZOAResult<OnRampLot?> { Result = null, Message = "Not found." };

        try
        {
            var query = SurrealQuery.Of(statement).WithParam(parameterName, value.Trim());
            var rows = await _executor.QueryAsync<OnRampLot>(query, ct);
            var row = rows?.FirstOrDefault();
            return new AZOAResult<OnRampLot?>
            {
                Result = row,
                Message = row is null ? "Not found." : "Success",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<OnRampLot?>()
                .CaptureException(ex, $"On-ramp lot read failed: {ex.Message}");
        }
    }
}
