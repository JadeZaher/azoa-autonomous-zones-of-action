// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Services.Custody;

/// <summary>
/// Makes a brand-new custodial account able to actually receive a specific asset, and
/// (optionally) delivers the units in the same indivisible group. See
/// <c>Services/Custody/AGENTS.md</c> §recipient-preparation.
/// </summary>
public interface ICustodialRecipientPreparer
{
    /// <summary>
    /// Makes <paramref name="request"/>'s recipient able to hold the asset — funding and
    /// acceptance all-or-nothing. It moves no units: delivery stays with the caller so a
    /// value move always carries a durable, reconcilable operation of its own.
    /// </summary>
    Task<AZOAResult<CustodialRecipientReadiness>> PrepareAsync(
        CustodialRecipientPreparation request, CancellationToken ct = default);
}

/// <summary>
/// What a caller must supply to make one recipient ready for one asset. The fee payer is
/// the account that funds the recipient's account minimum; on the issuance path it is the
/// platform reserve, which is also the sender of the units.
/// </summary>
/// <param name="ChainType">Rail the recipient's account lives on.</param>
/// <param name="AssetId">The asset the recipient must be able to hold.</param>
/// <param name="RecipientAddress">The recipient's custodial account address.</param>
/// <param name="RecipientSigningContext">
/// The recipient's own per-user signing context. Acceptance can only be authorised by the
/// recipient's key, so a context that is not a resolvable user context is REFUSED — the
/// platform key must never stand in for a person here.
/// </param>
/// <param name="FeePayerAddress">Account that funds the recipient's account minimum.</param>
/// <param name="FeePayerSigningContext">Signing context for the fee payer.</param>
public sealed record CustodialRecipientPreparation(
    string ChainType,
    string AssetId,
    string RecipientAddress,
    SigningContext RecipientSigningContext,
    string FeePayerAddress,
    SigningContext FeePayerSigningContext);

/// <summary>What preparation did, and what the caller still has to do.</summary>
/// <param name="AlreadyPrepared">True when the recipient could already receive the asset.</param>
/// <param name="TransactionId">The acceptance transaction, when one was needed.</param>
/// <param name="PendingConfirmation">
/// True when the group was accepted by the rail but has not confirmed inside the poll
/// window. The caller must treat the recipient as NOT yet ready and retry rather than
/// sending units into an account whose acceptance has not landed.
/// </param>
/// <param name="FundedBaseUnits">Native units the fee payer advanced to the recipient.</param>
public sealed record CustodialRecipientReadiness(
    bool AlreadyPrepared,
    string? TransactionId,
    bool PendingConfirmation,
    string FundedBaseUnits);

/// <summary>
/// Provider capability for preparing a recipient account. Declared here rather than beside
/// the other chain modules because the preparer is its only consumer and the contract is
/// custody-shaped: every leg it describes is signed through
/// <see cref="AZOA.WebAPI.Interfaces.Managers.IKeyCustodyService"/>.
/// </summary>
public interface IRecipientPreparationModule : IBlockchainProviderModule
{
    /// <summary>True only when this bound adapter can really prepare a recipient.</summary>
    bool SupportsRecipientPreparation { get; }

    /// <summary>Reads, from the rail itself, whether the recipient can hold the asset yet.</summary>
    Task<AZOAResult<RecipientAssetReadiness>> GetRecipientAssetReadinessAsync(
        string assetId, string recipientAddress, CancellationToken ct = default);

    /// <summary>
    /// Funds the recipient's account minimum and has the recipient accept the asset as ONE
    /// indivisible group, so a funded-but-unaccepted half-state cannot exist.
    /// </summary>
    Task<AZOAResult<RecipientPreparationReceipt>> PrepareRecipientForAssetAsync(
        RecipientPreparationRequest request, CancellationToken ct = default);
}

/// <summary>Rail-read state of one recipient against one asset.</summary>
/// <param name="CanReceive">True when the account already accepts this asset.</param>
/// <param name="IsBlocked">True when the account holds the asset but cannot move it.</param>
/// <param name="ShortfallBaseUnits">
/// Native base units the fee payer must advance so the account can accept the asset and
/// pay for the acceptance itself. "0" when nothing is owed.
/// </param>
public sealed record RecipientAssetReadiness(
    bool CanReceive, bool IsBlocked, string ShortfallBaseUnits);

/// <summary>One group: fund the recipient, then have the recipient accept the asset.</summary>
public sealed record RecipientPreparationRequest(
    string AssetId,
    string RecipientAddress,
    SigningContext RecipientSigningContext,
    string FeePayerAddress,
    SigningContext FeePayerSigningContext,
    string FundingBaseUnits);

/// <summary>Accepted group evidence. Ids are rail detail and never cross the tenant boundary.</summary>
public sealed record RecipientPreparationReceipt(
    string GroupId,
    string AcceptanceTransactionId,
    bool PendingConfirmation,
    string FundedBaseUnits);

/// <summary>
/// The one place a fresh custodial account is made able to receive value. Every issuance
/// and every hold release goes through here, so the "the recipient has never touched this
/// asset" case is handled once instead of being rediscovered by each caller.
/// <para>
/// Idempotency is anchored on rail truth, not a ledger: preparation re-reads the
/// recipient's state first and returns <c>AlreadyPrepared</c> when the account already
/// accepts the asset, so a retried call cannot double-fund or double-accept. The delivery
/// leg is NOT idempotent on its own and therefore only ever runs inside a caller that
/// already owns an idempotency claim (<c>AllocationManager</c>).
/// </para>
/// </summary>
public sealed class CustodialRecipientPreparer : ICustodialRecipientPreparer
{
    private readonly IBlockchainProviderFactory _providers;
    private readonly ILogger<CustodialRecipientPreparer> _logger;

    public CustodialRecipientPreparer(
        IBlockchainProviderFactory providers,
        ILogger<CustodialRecipientPreparer> logger)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<CustodialRecipientReadiness>> PrepareAsync(
        CustodialRecipientPreparation request, CancellationToken ct = default)
    {
        if (request is null)
            return Refuse("Recipient preparation request is required.");
        if (string.IsNullOrWhiteSpace(request.ChainType))
            return Refuse("Recipient preparation requires a rail.");
        if (string.IsNullOrWhiteSpace(request.AssetId))
            return Refuse("Recipient preparation requires an asset.");
        if (string.IsNullOrWhiteSpace(request.RecipientAddress))
            return Refuse("Recipient preparation requires a recipient account.");
        if (string.IsNullOrWhiteSpace(request.FeePayerAddress))
            return Refuse("Recipient preparation requires a funding account.");

        // The recipient's acceptance can only be authorised by the recipient's own key.
        // A platform context here would silently mean "the node accepted on the person's
        // behalf", which is not a thing this rail permits and not a thing the node should
        // ever claim. Fail closed.
        if (!request.RecipientSigningContext.IsResolvableUserContext)
            return Refuse("Recipient preparation requires the recipient's own signing context.");

        var module = ResolveModule(request.ChainType);
        if (module is null)
        {
            _logger.LogWarning(
                "Recipient preparation is not supported on rail {Rail}; refusing to move value.",
                request.ChainType);
            return ConsumerSafeErrors.Failure<CustodialRecipientReadiness>(
                "not supported", ConsumerSafeErrorCodes.RailUnavailable);
        }

        var readiness = await module.GetRecipientAssetReadinessAsync(
            request.AssetId, request.RecipientAddress, ct);
        if (readiness.IsError || readiness.Result is null)
        {
            _logger.LogWarning(
                "Recipient readiness could not be read on rail {Rail}: {Detail}",
                request.ChainType, readiness.Message);
            return ConsumerSafeErrors.Failure<CustodialRecipientReadiness>(readiness.Message);
        }

        if (readiness.Result.IsBlocked)
        {
            return ConsumerSafeErrors.Failure<CustodialRecipientReadiness>(
                "frozen", ConsumerSafeErrorCodes.NotPermitted);
        }

        if (readiness.Result.CanReceive)
        {
            return AZOAResult<CustodialRecipientReadiness>.Success(
                new CustodialRecipientReadiness(
                    AlreadyPrepared: true, TransactionId: null,
                    PendingConfirmation: false, FundedBaseUnits: "0"),
                "Recipient was already able to receive these units.");
        }

        var prepared = await module.PrepareRecipientForAssetAsync(
            new RecipientPreparationRequest(
                request.AssetId,
                request.RecipientAddress,
                request.RecipientSigningContext,
                request.FeePayerAddress,
                request.FeePayerSigningContext,
                readiness.Result.ShortfallBaseUnits),
            ct);

        if (prepared.IsError || prepared.Result is null)
        {
            _logger.LogWarning(
                "Recipient preparation failed on rail {Rail}: {Detail}",
                request.ChainType, prepared.Message);
            return ConsumerSafeErrors.Failure<CustodialRecipientReadiness>(prepared.Message);
        }

        var receipt = prepared.Result;
        return AZOAResult<CustodialRecipientReadiness>.Success(
            new CustodialRecipientReadiness(
                AlreadyPrepared: false,
                TransactionId: receipt.AcceptanceTransactionId,
                PendingConfirmation: receipt.PendingConfirmation,
                FundedBaseUnits: receipt.FundedBaseUnits),
            receipt.PendingConfirmation
                ? "Recipient preparation was submitted and is still settling."
                : "Recipient is now able to receive these units.");
    }

    /// <summary>Resolves the rail's preparation capability, or null when it has none.</summary>
    private IRecipientPreparationModule? ResolveModule(string chainType)
    {
        IBlockchainProvider provider;
        try
        {
            provider = _providers.GetProvider(chainType, _providers.DefaultNetwork);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No provider is bound for rail {Rail}.", chainType);
            return null;
        }

        return _providers.TryGetModule<IRecipientPreparationModule>(provider, out var module)
               && module is { SupportsRecipientPreparation: true }
            ? module
            : null;
    }

    /// <summary>
    /// A caller-shaped refusal: the inputs were wrong, so the message is about the request
    /// and carries no rail detail to scrub.
    /// </summary>
    private static AZOAResult<CustodialRecipientReadiness> Refuse(string message)
        => AZOAResult<CustodialRecipientReadiness>.FailureWithCode(
            message, ConsumerSafeErrorCodes.RecipientNotReady);
}
