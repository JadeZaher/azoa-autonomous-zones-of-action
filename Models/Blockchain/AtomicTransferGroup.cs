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
            ? effect.SigningContext.AvatarId == Guid.Empty && effect.SigningContext.WalletId == Guid.Empty
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
        AppendCanonical(builder, effect.SigningContext.AvatarId.ToString("N"));
        AppendCanonical(builder, effect.SigningContext.WalletId.ToString("N"));
        AppendCanonical(builder, effect.SigningContext.IsPlatform ? "1" : "0");
        AppendCanonical(builder, effect.SigningContext.ActingTenantId.ToString("N"));
        AppendCanonical(builder, effect.SigningContext.GrantorAvatarId.ToString("N"));
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
