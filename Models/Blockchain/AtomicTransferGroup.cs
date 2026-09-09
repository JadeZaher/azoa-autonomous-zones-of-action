using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Models.Blockchain;

/// <summary>One leg of an atomic two-effect asset transfer group.</summary>
public sealed record AtomicTransferEffect(
    string AssetId,
    string FromAddress,
    string ToAddress,
    ulong Amount,
    SigningContext SigningContext);

/// <summary>Immutable, chain-bound request for a primary plus treasury transfer group.</summary>
public sealed record AtomicTransferGroupRequest
{
    private const string CanonicalVersion = "azoa.atomic-transfer-group.v1";

    private AtomicTransferGroupRequest(
        string chainType,
        ChainNetwork network,
        string idempotencyKeyHash,
        string groupIdentity,
        AtomicTransferEffect primary,
        AtomicTransferEffect treasury)
    {
        ChainType = chainType;
        Network = network;
        IdempotencyKeyHash = idempotencyKeyHash;
        GroupIdentity = groupIdentity;
        Primary = primary;
        Treasury = treasury;
    }

    /// <summary>Canonical chain binding that an adapter must match exactly.</summary>
    public string ChainType { get; }
    /// <summary>Canonical network binding that an adapter must match exactly.</summary>
    public ChainNetwork Network { get; }
    /// <summary>SHA-256 digest of the caller's trimmed idempotency key.</summary>
    public string IdempotencyKeyHash { get; }
    /// <summary>Stable digest of the entire immutable group decision.</summary>
    public string GroupIdentity { get; }
    /// <summary>The recipient-facing transfer leg.</summary>
    public AtomicTransferEffect Primary { get; }
    /// <summary>The node treasury transfer leg.</summary>
    public AtomicTransferEffect Treasury { get; }

    /// <summary>Builds a canonical same-asset, same-signer request for one resolved provider binding.</summary>
    public static AZOAResult<AtomicTransferGroupRequest> TryCreate(
        IBlockchainProvider provider,
        string chainType,
        ChainNetwork network,
        string idempotencyKey,
        AtomicTransferEffect primary,
        AtomicTransferEffect treasury)
    {
        if (provider is null)
            return Error("A resolved blockchain provider is required.");
        if (string.IsNullOrWhiteSpace(chainType))
            return Error("A chain type is required.");
        if (!Enum.IsDefined(network))
            return Error("A supported chain network is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Error("An idempotency key is required.");
        if (!IsValidEffect(primary) || !IsValidEffect(treasury))
            return Error("Each atomic transfer effect requires asset, source, destination, positive amount, and a resolvable signer.");

        var normalizedPrimary = Normalize(primary);
        var normalizedTreasury = Normalize(treasury);
        if (!string.Equals(normalizedPrimary.AssetId, normalizedTreasury.AssetId, StringComparison.Ordinal)
            || !string.Equals(normalizedPrimary.FromAddress, normalizedTreasury.FromAddress, StringComparison.Ordinal))
        {
            return Error("Atomic transfer effects must use the same asset and source address.");
        }

        if (normalizedPrimary.SigningContext != normalizedTreasury.SigningContext)
            return Error("Atomic transfer effects must use the same signing context.");
        if (string.Equals(normalizedPrimary.ToAddress, normalizedTreasury.ToAddress, StringComparison.Ordinal))
            return Error("Atomic transfer recipients must differ.");

        var providerChain = provider.ChainType;
        if (string.IsNullOrWhiteSpace(providerChain)
            || !string.Equals(providerChain, providerChain.Trim(), StringComparison.Ordinal))
        {
            return Error("The resolved blockchain provider has no canonical chain type.");
        }

        if (!string.Equals(chainType.Trim(), providerChain, StringComparison.OrdinalIgnoreCase))
            return Error("The requested chain type does not match the resolved blockchain provider.");
        if (network != provider.ActiveNetwork)
            return Error("The requested chain network does not match the resolved blockchain provider.");

        var idempotencyKeyHash = Digest(idempotencyKey.Trim());
        var groupIdentity = Digest(Canonicalize(
            providerChain,
            network,
            idempotencyKeyHash,
            normalizedPrimary,
            normalizedTreasury));

        return new AZOAResult<AtomicTransferGroupRequest>
        {
            Result = new AtomicTransferGroupRequest(
                providerChain,
                network,
                idempotencyKeyHash,
                groupIdentity,
                normalizedPrimary,
                normalizedTreasury),
        };
    }

    private static bool IsValidEffect(AtomicTransferEffect effect) =>
        effect.Amount > 0
        && !string.IsNullOrWhiteSpace(effect.AssetId)
        && !string.IsNullOrWhiteSpace(effect.FromAddress)
        && !string.IsNullOrWhiteSpace(effect.ToAddress)
        && (effect.SigningContext.IsPlatform
            ? effect.SigningContext.UserId == Guid.Empty && effect.SigningContext.WalletId == Guid.Empty
            : effect.SigningContext.IsResolvableUserContext);

    private static AtomicTransferEffect Normalize(AtomicTransferEffect effect) => effect with
    {
        AssetId = effect.AssetId.Trim(),
        FromAddress = effect.FromAddress.Trim(),
        ToAddress = effect.ToAddress.Trim(),
        SigningContext = effect.SigningContext with { Scope = effect.SigningContext.Scope?.Trim() },
    };

    private static string Canonicalize(
        string chainType,
        ChainNetwork network,
        string idempotencyKeyHash,
        AtomicTransferEffect primary,
        AtomicTransferEffect treasury)
    {
        var builder = new StringBuilder(CanonicalVersion);
        AppendCanonical(builder, chainType);
        AppendCanonical(builder, network.ToString());
        AppendCanonical(builder, idempotencyKeyHash);
        AppendEffect(builder, primary);
        AppendEffect(builder, treasury);
        return builder.ToString();
    }

    private static void AppendEffect(StringBuilder builder, AtomicTransferEffect effect)
    {
        AppendCanonical(builder, effect.AssetId);
        AppendCanonical(builder, effect.FromAddress);
        AppendCanonical(builder, effect.ToAddress);
        AppendCanonical(builder, effect.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendCanonical(builder, effect.SigningContext.UserId.ToString("N"));
        AppendCanonical(builder, effect.SigningContext.WalletId.ToString("N"));
        AppendCanonical(builder, effect.SigningContext.IsPlatform ? "1" : "0");
        AppendCanonical(builder, effect.SigningContext.ActingTenantId.ToString("N"));
        AppendCanonical(builder, effect.SigningContext.GrantorUserId.ToString("N"));
        AppendCanonical(builder, effect.SigningContext.Scope ?? string.Empty);
    }

    private static void AppendCanonical(StringBuilder builder, string value) =>
        builder.Append('|').Append(value.Length).Append(':').Append(value);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static AZOAResult<AtomicTransferGroupRequest> Error(string message) => new()
    {
        IsError = true,
        Message = message,
    };
}


/// <summary>
/// One leg of a multi-signer atomic group: its own asset, its own source, its own
/// destination and its own signing identity.
/// </summary>
/// <param name="FeeIsPooled">
/// True when this leg pays no chain fee of its own and the group's fee payer covers it. It is
/// declared per leg rather than inferred because the reason a leg cannot pay is WHOSE account it
/// is: a person's custodial account on this node holds no spendable native units, so a leg that
/// person signs fails on chain the moment it is asked for a fee.
/// </param>
public sealed record MultiSignerAtomicGroupLeg(
    string AssetId,
    string FromAddress,
    string ToAddress,
    BigInteger Amount,
    SigningContext SigningContext,
    bool FeeIsPooled)
{
    /// <summary>True when the amount is a positive whole number a chain base-unit field can carry.</summary>
    public bool FitsChainBaseUnits => Amount > BigInteger.Zero && Amount <= ulong.MaxValue;

    /// <summary>The amount in chain base units; only meaningful once <see cref="FitsChainBaseUnits"/> holds.</summary>
    public ulong AmountBaseUnits => (ulong)Amount;
}

/// <summary>
/// Immutable, provider-bound request for an atomic group whose legs may each carry a DIFFERENT
/// asset, source and signing identity, with the chain fee pooled onto one payer.
/// </summary>
/// <remarks>
/// <para><b>Group submission only.</b> <see cref="AtomicTransferGroupRequest"/> above is the
/// same-asset, same-source, same-signer transfer pair the node-fee split settles as, and it is
/// deliberately unchanged: its equality checks are what stop a fee split from paying two
/// different assets out of two different accounts. An exchange group is a different animal —
/// two assets, two signers, pooled fees — so it gets its own validated shape here rather than a
/// relaxation of that one.</para>
/// <para><b>What this type is FOR is the provider binding.</b> A group plan knows what the node
/// decided; only a resolved provider knows which chain and network the node would actually
/// submit to. Binding them here is what stops a group composed for one network from being
/// broadcast to another, and it is checked before anything is signed.</para>
/// </remarks>
public sealed record MultiSignerAtomicGroupRequest
{
    private const string CanonicalVersion = "azoa.multi-signer-atomic-group.v1";

    /// <summary>Most legs one atomic group may carry. Algorand's ceiling, and the tightest of the rails.</summary>
    public const int MaxLegs = 16;

    private MultiSignerAtomicGroupRequest(
        string chainType,
        ChainNetwork network,
        string idempotencyKeyHash,
        string groupIdentity,
        IReadOnlyList<MultiSignerAtomicGroupLeg> legs,
        string feePayerAddress,
        SigningContext feePayerSigningContext)
    {
        ChainType = chainType;
        Network = network;
        IdempotencyKeyHash = idempotencyKeyHash;
        GroupIdentity = groupIdentity;
        Legs = legs;
        FeePayerAddress = feePayerAddress;
        FeePayerSigningContext = feePayerSigningContext;
    }

    /// <summary>Canonical chain binding, taken from the resolved provider rather than the caller.</summary>
    public string ChainType { get; }

    /// <summary>Canonical network binding, matched against the resolved provider.</summary>
    public ChainNetwork Network { get; }

    /// <summary>SHA-256 digest of the caller's trimmed idempotency key.</summary>
    public string IdempotencyKeyHash { get; }

    /// <summary>Stable digest of the entire immutable group decision.</summary>
    public string GroupIdentity { get; }

    /// <summary>Every leg, in submission order.</summary>
    public IReadOnlyList<MultiSignerAtomicGroupLeg> Legs { get; }

    /// <summary>The account that pools the chain fee for every leg that cannot pay its own.</summary>
    public string FeePayerAddress { get; }

    /// <summary>The fee payer's signing identity; always a node-held platform account.</summary>
    public SigningContext FeePayerSigningContext { get; }

    /// <summary>How many legs the fee payer must cover.</summary>
    public int PooledFeeLegCount => Legs.Count(leg => leg.FeeIsPooled);

    /// <summary>True when no leg the group carries is itself sent from the fee payer's account.</summary>
    public bool NeedsSeparateFeePayerLeg =>
        !Legs.Any(leg => string.Equals(leg.FromAddress, FeePayerAddress, StringComparison.Ordinal));

    /// <summary>Validates and freezes one multi-signer group against a resolved provider, or refuses it.</summary>
    public static AZOAResult<MultiSignerAtomicGroupRequest> TryCreate(
        IBlockchainProvider provider,
        string chainType,
        ChainNetwork network,
        string idempotencyKey,
        IReadOnlyList<MultiSignerAtomicGroupLeg> legs,
        string feePayerAddress,
        SigningContext feePayerSigningContext)
    {
        if (provider is null)
            return Error("A resolved blockchain provider is required.");
        if (string.IsNullOrWhiteSpace(chainType))
            return Error("A chain type is required.");
        if (!Enum.IsDefined(network))
            return Error("A supported chain network is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Error("An idempotency key is required.");
        if (legs is null || legs.Count == 0)
            return Error("A multi-signer atomic group must carry at least one leg.");
        if (legs.Count > MaxLegs)
            return Error($"A multi-signer atomic group may carry at most {MaxLegs} legs.");
        if (string.IsNullOrWhiteSpace(feePayerAddress))
            return Error("A multi-signer atomic group must name the account that pools its fees.");
        if (!feePayerSigningContext.IsPlatform)
            return Error("The fee payer of a multi-signer atomic group must be a node-held account.");

        var providerChain = provider.ChainType;
        if (string.IsNullOrWhiteSpace(providerChain)
            || !string.Equals(providerChain, providerChain.Trim(), StringComparison.Ordinal))
        {
            return Error("The resolved blockchain provider has no canonical chain type.");
        }

        if (!string.Equals(chainType.Trim(), providerChain, StringComparison.OrdinalIgnoreCase))
            return Error("The requested chain type does not match the resolved blockchain provider.");
        if (network != provider.ActiveNetwork)
            return Error("The requested chain network does not match the resolved blockchain provider.");

        var normalizedFeePayer = feePayerAddress.Trim();
        var normalized = new List<MultiSignerAtomicGroupLeg>(legs.Count);
        foreach (var leg in legs)
        {
            if (leg is null)
                return Error("A multi-signer atomic group leg is required.");
            if (string.IsNullOrWhiteSpace(leg.AssetId)
                || string.IsNullOrWhiteSpace(leg.FromAddress)
                || string.IsNullOrWhiteSpace(leg.ToAddress))
            {
                return Error("Every multi-signer atomic group leg needs an asset, a source and a destination.");
            }

            if (!leg.FitsChainBaseUnits)
            {
                return Error(
                    "Every multi-signer atomic group leg must move a positive whole amount this chain "
                    + "can carry in one transaction.");
            }

            var candidate = leg with
            {
                AssetId = leg.AssetId.Trim(),
                FromAddress = leg.FromAddress.Trim(),
                ToAddress = leg.ToAddress.Trim(),
                SigningContext = leg.SigningContext with { Scope = leg.SigningContext.Scope?.Trim() },
            };

            if (string.Equals(candidate.FromAddress, candidate.ToAddress, StringComparison.Ordinal))
                return Error("A multi-signer atomic group leg cannot send an amount to the account it came from.");

            // The signing identity and the account it is supposed to unlock must agree. A
            // node-held leg carrying a person's identity (or the reverse) is a leg somebody
            // would sign with the wrong key.
            if (candidate.SigningContext.IsPlatform)
            {
                if (candidate.SigningContext.UserId != Guid.Empty
                    || candidate.SigningContext.WalletId != Guid.Empty)
                {
                    return Error("A node-held multi-signer atomic group leg must not carry a person's identity.");
                }
            }
            else if (!candidate.SigningContext.IsResolvableUserContext)
            {
                return Error("A person-signed multi-signer atomic group leg needs a resolvable custodial identity.");
            }

            // The whole point of pooling: a person's custodial account holds no spendable
            // native units, so it can never be the account that pays the group's fee.
            if (candidate.FeeIsPooled
                && string.Equals(candidate.FromAddress, normalizedFeePayer, StringComparison.Ordinal))
            {
                return Error(
                    "The fee payer of a multi-signer atomic group cannot also be the source of a leg "
                    + "whose fee it pools.");
            }

            normalized.Add(candidate);
        }

        var idempotencyKeyHash = Digest(idempotencyKey.Trim());
        var groupIdentity = Digest(Canonicalize(
            providerChain, network, idempotencyKeyHash, normalized, normalizedFeePayer));

        return new AZOAResult<MultiSignerAtomicGroupRequest>
        {
            Result = new MultiSignerAtomicGroupRequest(
                providerChain,
                network,
                idempotencyKeyHash,
                groupIdentity,
                normalized,
                normalizedFeePayer,
                feePayerSigningContext),
        };
    }

    private static string Canonicalize(
        string chainType,
        ChainNetwork network,
        string idempotencyKeyHash,
        IReadOnlyList<MultiSignerAtomicGroupLeg> legs,
        string feePayerAddress)
    {
        var builder = new StringBuilder(CanonicalVersion);
        AppendCanonical(builder, chainType);
        AppendCanonical(builder, network.ToString());
        AppendCanonical(builder, idempotencyKeyHash);
        AppendCanonical(builder, feePayerAddress);
        foreach (var leg in legs)
        {
            AppendCanonical(builder, leg.AssetId);
            AppendCanonical(builder, leg.FromAddress);
            AppendCanonical(builder, leg.ToAddress);
            AppendCanonical(builder, leg.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendCanonical(builder, leg.FeeIsPooled ? "1" : "0");
            AppendCanonical(builder, leg.SigningContext.UserId.ToString("N"));
            AppendCanonical(builder, leg.SigningContext.WalletId.ToString("N"));
            AppendCanonical(builder, leg.SigningContext.IsPlatform ? "1" : "0");
            AppendCanonical(builder, leg.SigningContext.ActingTenantId.ToString("N"));
            AppendCanonical(builder, leg.SigningContext.GrantorUserId.ToString("N"));
            AppendCanonical(builder, leg.SigningContext.Scope ?? string.Empty);
        }

        return builder.ToString();
    }

    private static void AppendCanonical(StringBuilder builder, string value) =>
        builder.Append('|').Append(value.Length).Append(':').Append(value);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static AZOAResult<MultiSignerAtomicGroupRequest> Error(string message) => new()
    {
        IsError = true,
        Message = message,
    };
}

/// <summary>A chain adapter's accepted atomic group, never an individual-leg result.</summary>
public sealed record AtomicTransferGroupSubmission
{
    private AtomicTransferGroupSubmission(
        string groupIdentity,
        string chainGroupId,
        string primaryTransactionId,
        string treasuryTransactionId,
        AtomicTransferGroupSubmissionState state)
    {
        GroupIdentity = groupIdentity;
        ChainGroupId = chainGroupId;
        PrimaryTransactionId = primaryTransactionId;
        TreasuryTransactionId = treasuryTransactionId;
        State = state;
    }

    /// <summary>Stable identity of the group decision.</summary>
    public string GroupIdentity { get; }
    /// <summary>The chain-native identifier assigned to the submitted transaction group.</summary>
    public string ChainGroupId { get; }
    /// <summary>Transaction identifier for the recipient-facing leg.</summary>
    public string PrimaryTransactionId { get; }
    /// <summary>Transaction identifier for the treasury leg.</summary>
    public string TreasuryTransactionId { get; }
    /// <summary>Group state after one atomic submission.</summary>
    public AtomicTransferGroupSubmissionState State { get; }

    /// <summary>Creates an accepted two-leg result bound to its originating group request.</summary>
    public static AtomicTransferGroupSubmission Accepted(
        AtomicTransferGroupRequest request,
        string chainGroupId,
        string primaryTransactionId,
        string treasuryTransactionId,
        AtomicTransferGroupSubmissionState state)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsCanonicalSha256Digest(request.GroupIdentity))
            throw new ArgumentException("The originating request has an invalid group identity.", nameof(request));

        chainGroupId = ValidateChainGroupId(chainGroupId);
        primaryTransactionId = ValidateTransactionId(primaryTransactionId, nameof(primaryTransactionId));
        treasuryTransactionId = ValidateTransactionId(treasuryTransactionId, nameof(treasuryTransactionId));
        if (string.Equals(primaryTransactionId, treasuryTransactionId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The primary and treasury transaction identifiers must be distinct.",
                nameof(treasuryTransactionId));
        }
        if (state is AtomicTransferGroupSubmissionState.NotSubmitted)
            throw new ArgumentOutOfRangeException(nameof(state), "An accepted group cannot be NotSubmitted.");

        return new AtomicTransferGroupSubmission(
            request.GroupIdentity,
            chainGroupId,
            primaryTransactionId,
            treasuryTransactionId,
            state);
    }

    private static string ValidateTransactionId(string transactionId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId, parameterName);
        if (!string.Equals(transactionId, transactionId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("A transaction identifier cannot have surrounding whitespace.", parameterName);
        return transactionId;
    }

    private static string ValidateChainGroupId(string chainGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chainGroupId);
        if (!string.Equals(chainGroupId, chainGroupId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("A chain group identifier cannot have surrounding whitespace.", nameof(chainGroupId));
        return chainGroupId;
    }

    private static bool IsCanonicalSha256Digest(string value)
    {
        if (value.Length != 64)
            return false;

        foreach (var character in value)
        {
            if ((character < '0' || character > '9')
                && (character < 'a' || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>States available only after a two-leg group has been accepted as one submission.</summary>
public enum AtomicTransferGroupSubmissionState
{
    NotSubmitted = 0,
    Submitted,
    PendingConfirmation,
    Confirmed,
}

/// <summary>Immutable inputs for a read-only audit of an accepted atomic group.</summary>
public sealed record AtomicTransferGroupObservationRequest
{
    private AtomicTransferGroupObservationRequest(
        AtomicTransferGroupRequest transferGroup,
        AtomicTransferGroupSubmission submission)
    {
        TransferGroup = transferGroup;
        Submission = submission;
    }

    /// <summary>The immutable economic and routing decision to match.</summary>
    public AtomicTransferGroupRequest TransferGroup { get; }
    /// <summary>The accepted chain identifiers to inspect.</summary>
    public AtomicTransferGroupSubmission Submission { get; }

    /// <summary>Validates that accepted evidence is bound to the supplied immutable request.</summary>
    public static AZOAResult<AtomicTransferGroupObservationRequest> TryCreate(
        AtomicTransferGroupRequest? transferGroup,
        AtomicTransferGroupSubmission? submission)
    {
        if (transferGroup is null || submission is null)
            return Error("An immutable transfer group and accepted submission are required.");
        if (!IsCanonicalSha256Digest(transferGroup.GroupIdentity)
            || !string.Equals(transferGroup.GroupIdentity, submission.GroupIdentity, StringComparison.Ordinal))
        {
            return Error("Accepted submission evidence is not bound to the immutable transfer group.");
        }
        if (submission.State == AtomicTransferGroupSubmissionState.NotSubmitted
            || !IsCanonicalIdentifier(submission.ChainGroupId)
            || !IsCanonicalIdentifier(submission.PrimaryTransactionId)
            || !IsCanonicalIdentifier(submission.TreasuryTransactionId)
            || string.Equals(submission.PrimaryTransactionId, submission.TreasuryTransactionId, StringComparison.Ordinal)
            || string.Equals(submission.ChainGroupId, submission.PrimaryTransactionId, StringComparison.Ordinal)
            || string.Equals(submission.ChainGroupId, submission.TreasuryTransactionId, StringComparison.Ordinal))
        {
            return Error("Accepted submission group and transaction identifiers must be distinct, non-empty, and canonical.");
        }

        return new AZOAResult<AtomicTransferGroupObservationRequest>
        {
            Result = new AtomicTransferGroupObservationRequest(transferGroup, submission),
        };
    }

    private static bool IsCanonicalIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsCanonicalSha256Digest(string value)
    {
        if (value.Length != 64)
            return false;

        foreach (var character in value)
        {
            if ((character < '0' || character > '9')
                && (character < 'a' || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static AZOAResult<AtomicTransferGroupObservationRequest> Error(string message) => new()
    {
        IsError = true,
        Message = message,
    };
}

/// <summary>Evidence state of one accepted atomic transfer leg.</summary>
public enum AtomicTransferLegObservationVerdict
{
    Confirmed = 1,
    Pending,
    Unseen,
    PoolRejected,
    Mismatched,
    Unavailable,
}

/// <summary>Aggregated evidence state of an accepted atomic transfer group.</summary>
public enum AtomicTransferGroupObservationVerdict
{
    Confirmed = 1,
    Incomplete,
    Rejected,
    Mismatched,
    Unavailable,
}

/// <summary>Read-only observation for one requested transaction identifier.</summary>
public sealed record AtomicTransferLegObservation(
    string TransactionId,
    AtomicTransferLegObservationVerdict Verdict,
    long? ConfirmedRound,
    string? Detail = null);

/// <summary>Read-only aggregate of exact evidence for both atomic transfer legs.</summary>
public sealed record AtomicTransferGroupObservation(
    AtomicTransferGroupObservationVerdict Verdict,
    AtomicTransferLegObservation Primary,
    AtomicTransferLegObservation Treasury);

/// <summary>Secret-free immutable transfer facts sufficient for post-acceptance chain observation.</summary>
public sealed record AtomicTransferGroupObservationEvidence
{
    private AtomicTransferGroupObservationEvidence(
        string chainType,
        ChainNetwork network,
        string groupIdentity,
        AtomicTransferObservationEffect primary,
        AtomicTransferObservationEffect treasury,
        string chainGroupId,
        string primaryTransactionId,
        string treasuryTransactionId,
        AtomicTransferGroupSubmissionState acceptedState)
    {
        ChainType = chainType;
        Network = network;
        GroupIdentity = groupIdentity;
        Primary = primary;
        Treasury = treasury;
        ChainGroupId = chainGroupId;
        PrimaryTransactionId = primaryTransactionId;
        TreasuryTransactionId = treasuryTransactionId;
        AcceptedState = acceptedState;
    }

    /// <summary>Canonical chain binding for the provider observation.</summary>
    public string ChainType { get; }
    /// <summary>Canonical network binding for the provider observation.</summary>
    public ChainNetwork Network { get; }
    /// <summary>Precommitted immutable transfer-group identity.</summary>
    public string GroupIdentity { get; }
    /// <summary>Recipient-facing effect to verify.</summary>
    public AtomicTransferObservationEffect Primary { get; }
    /// <summary>Treasury-facing effect to verify.</summary>
    public AtomicTransferObservationEffect Treasury { get; }
    /// <summary>Accepted chain-native group identifier.</summary>
    public string ChainGroupId { get; }
    /// <summary>Accepted recipient-facing transaction identifier.</summary>
    public string PrimaryTransactionId { get; }
    /// <summary>Accepted treasury-facing transaction identifier.</summary>
    public string TreasuryTransactionId { get; }
    /// <summary>State captured when the group was accepted; never an observation verdict.</summary>
    public AtomicTransferGroupSubmissionState AcceptedState { get; }

    /// <summary>Builds secret-free evidence from the original immutable request and accepted submission.</summary>
    public static AZOAResult<AtomicTransferGroupObservationEvidence> TryCreate(
        AtomicTransferGroupRequest? transferGroup,
        AtomicTransferGroupSubmission? submission)
    {
        if (transferGroup is null || submission is null)
            return Error("An immutable transfer group and accepted submission are required.");

        return TryCreate(
            transferGroup.ChainType,
            transferGroup.Network,
            transferGroup.GroupIdentity,
            new AtomicTransferObservationEffect(
                transferGroup.Primary.AssetId,
                transferGroup.Primary.FromAddress,
                transferGroup.Primary.ToAddress,
                transferGroup.Primary.Amount),
            new AtomicTransferObservationEffect(
                transferGroup.Treasury.AssetId,
                transferGroup.Treasury.FromAddress,
                transferGroup.Treasury.ToAddress,
                transferGroup.Treasury.Amount),
            submission.ChainGroupId,
            submission.PrimaryTransactionId,
            submission.TreasuryTransactionId,
            submission.State,
            submission.GroupIdentity);
    }

    /// <summary>Builds validated observation facts from durable receipt and settlement data.</summary>
    public static AZOAResult<AtomicTransferGroupObservationEvidence> TryCreate(
        string? chainType,
        ChainNetwork network,
        string? groupIdentity,
        AtomicTransferObservationEffect primary,
        AtomicTransferObservationEffect treasury,
        string? chainGroupId,
        string? primaryTransactionId,
        string? treasuryTransactionId,
        AtomicTransferGroupSubmissionState acceptedState,
        string? acceptedGroupIdentity = null)
    {
        if (string.IsNullOrWhiteSpace(chainType)
            || !string.Equals(chainType, chainType.Trim(), StringComparison.Ordinal)
            || !Enum.IsDefined(network)
            || !IsCanonicalSha256Digest(groupIdentity)
            || !IsEffect(primary)
            || !IsEffect(treasury)
            || !string.Equals(primary.AssetId, treasury.AssetId, StringComparison.Ordinal)
            || !string.Equals(primary.FromAddress, treasury.FromAddress, StringComparison.Ordinal)
            || string.Equals(primary.ToAddress, treasury.ToAddress, StringComparison.Ordinal)
            || !IsIdentifier(chainGroupId)
            || !IsIdentifier(primaryTransactionId)
            || !IsIdentifier(treasuryTransactionId)
            || string.Equals(primaryTransactionId, treasuryTransactionId, StringComparison.Ordinal)
            || string.Equals(chainGroupId, primaryTransactionId, StringComparison.Ordinal)
            || string.Equals(chainGroupId, treasuryTransactionId, StringComparison.Ordinal)
            || acceptedState is AtomicTransferGroupSubmissionState.NotSubmitted
            || (acceptedGroupIdentity is not null
                && !string.Equals(groupIdentity, acceptedGroupIdentity, StringComparison.Ordinal)))
        {
            return Error("Durable atomic-group observation evidence is incomplete or inconsistent.");
        }

        return new AZOAResult<AtomicTransferGroupObservationEvidence>
        {
            Result = new AtomicTransferGroupObservationEvidence(
                chainType!,
                network,
                groupIdentity!,
                primary,
                treasury,
                chainGroupId!,
                primaryTransactionId!,
                treasuryTransactionId!,
                acceptedState),
        };
    }

    private static bool IsEffect(AtomicTransferObservationEffect effect) =>
        effect.Amount > 0
        && IsIdentifier(effect.AssetId)
        && IsIdentifier(effect.FromAddress)
        && IsIdentifier(effect.ToAddress);

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsCanonicalSha256Digest(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AZOAResult<AtomicTransferGroupObservationEvidence> Error(string message) => new()
    {
        IsError = true,
        Message = message,
    };
}

/// <summary>One secret-free effect used only to verify persisted accepted transfer evidence.</summary>
public sealed record AtomicTransferObservationEffect(
    string AssetId,
    string FromAddress,
    string ToAddress,
    ulong Amount);
