using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Custody;

namespace AZOA.WebAPI.Providers.Blockchain.Simulated;

/// <summary>Recipient preparation for the in-process ledger; see Providers/Blockchain/AGENTS.md §simulated-recipient-preparation.</summary>
public sealed partial class SimulatedBlockchainProvider : IRecipientPreparationModule
{
    /// <inheritdoc/>
    public bool SupportsRecipientPreparation => true;

    /// <inheritdoc/>
    public Task<AZOAResult<RecipientAssetReadiness>> GetRecipientAssetReadinessAsync(
        string assetId, string recipientAddress, CancellationToken ct = default)
        => Task.FromResult(string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(recipientAddress)
            ? AZOAResult<RecipientAssetReadiness>.Failure("An asset and a recipient address are required.")
            // The simulated ledger has no account minimum and no opt-in: every address can hold every unit.
            : AZOAResult<RecipientAssetReadiness>.Success(new RecipientAssetReadiness(CanReceive: true, IsBlocked: false, ShortfallBaseUnits: "0")));

    /// <inheritdoc/>
    public Task<AZOAResult<RecipientPreparationReceipt>> PrepareRecipientForAssetAsync(
        RecipientPreparationRequest request, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<RecipientPreparationReceipt>.Failure(
            "Simulated recipients are always able to receive; there is nothing to prepare."));
}
