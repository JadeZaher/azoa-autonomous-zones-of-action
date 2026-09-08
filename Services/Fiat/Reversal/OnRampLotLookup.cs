// SPDX-License-Identifier: UNLICENSED

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
