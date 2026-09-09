// SPDX-License-Identifier: UNLICENSED

using System.Text.RegularExpressions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Core;

/// <summary>Stable wire codes for value failures that cross the tenant boundary.</summary>
public static class ConsumerSafeErrorCodes
{
    /// <summary>The recipient's account could not be made ready to receive units.</summary>
    public const string RecipientNotReady = "RECIPIENT_NOT_READY";

    /// <summary>The source of funds does not have enough available to cover the request.</summary>
    public const string InsufficientFunds = "INSUFFICIENT_FUNDS";

    /// <summary>The funding rail this request needs is not configured on this node.</summary>
    public const string RailUnavailable = "RAIL_UNAVAILABLE";

    /// <summary>The rail is reachable but rate-limited, timing out, or otherwise busy.</summary>
    public const string RailBusy = "RAIL_BUSY";

    /// <summary>The movement was submitted and is still settling; it must not be resent.</summary>
    public const string SettlementPending = "SETTLEMENT_PENDING";

    /// <summary>The account is not permitted to send or receive these units.</summary>
    public const string NotPermitted = "NOT_PERMITTED";

    /// <summary>The request was read, refused outright, and provably moved nothing.</summary>
    /// <remarks>
    /// The one code a consumer holding a seller's units may RELEASE those units on. It is
    /// deliberately distinct from an ambiguous outcome (a conflict, a timeout, an unreachable
    /// rail), where the movement may already have run and releasing would let one holding be
    /// sold twice. "We said no" and "we do not know" are different sentences to a person whose
    /// funds are in the balance, and conflating them silently confiscates. Carried on the wire
    /// as HTTP 422 by <c>ExchangeController.Translate</c>; see <c>Core/AGENTS.md</c>.
    /// </remarks>
    public const string Declined = "REQUEST_DECLINED";

    /// <summary>Nothing more specific could be established without leaking rail detail.</summary>
    public const string TemporarilyUnavailable = "TEMPORARILY_UNAVAILABLE";
}

/// <summary>A consumer-safe code plus the only message permitted to leave the node for it.</summary>
public readonly record struct ConsumerSafeError(string Code, string Message);

/// <summary>
/// Translates a rail/provider failure into a fixed, consumer-safe code and message
/// before it crosses the tenant boundary. Provider text is NEVER interpolated into
/// the result: every message returned here is a compile-time constant, so an address,
/// an asset identifier, a key-config path, a rail name, a fee unit or the words
/// "opt-in" / "token" cannot escape through this seam even if a future provider starts
/// putting them somewhere new. See <c>Core/AGENTS.md</c>.
/// </summary>
public static class ConsumerSafeErrors
{
    private const string RecipientNotReadyMessage =
        "This recipient's account is not ready to receive funds yet. Preparation was attempted " +
        "and did not complete; try again shortly.";

    private const string InsufficientFundsMessage =
        "There are not enough available funds to complete this request.";

    private const string RailUnavailableMessage =
        "This funding rail is not available on this node right now.";

    private const string RailBusyMessage =
        "The funding rail is busy. Nothing was moved; try again shortly.";

    private const string SettlementPendingMessage =
        "The transfer has been submitted and is still settling. It will complete on its own - " +
        "do not resend it.";

    private const string NotPermittedMessage =
        "This account is not permitted to send or receive these funds.";

    /// <remarks>
    /// Carried under <see cref="ConsumerSafeErrorCodes.NotPermitted"/> but worded for a
    /// PERMANENT refusal. Telling a member to "try again shortly" when the node will never
    /// allow the request is a false statement, not a vague one.
    /// </remarks>
    private const string PolicyRefusedMessage =
        "This node does not allow this kind of request. Nothing was moved, and retrying will " +
        "not change the answer - ask the node operator if you believe it should be allowed.";

    private const string TemporarilyUnavailableMessage =
        "This request could not be completed right now. Nothing was moved; try again shortly.";

    /// <summary>
    /// Message prefixes the API layer routes an HTTP status off (AllocationController maps
    /// KYC_FORBIDDEN to 403). Replacing a leaking message wholesale would silently downgrade
    /// that 403 to a 400, so the prefix is split off, kept, and re-attached.
    /// </summary>
    private static readonly string[] RoutingPrefixes =
    {
        KycAuthorizationError.Forbidden,
        KycAuthorizationError.NotFound,
        TenantAuthorizationError.Forbidden,
        TenantAuthorizationError.NotFound,
    };

    /// <summary>
    /// Vocabulary a non-crypto sponsor must never see. Used both to classify a raw
    /// provider message and, as a belt-and-braces assertion, to prove that nothing this
    /// class returns carries rail detail.
    /// </summary>
    private static readonly string[] RailVocabulary =
    {
        // "assetId" (concatenated) is deliberately ABSENT: it is the name of a request
        // field a tenant developer must be told about by name, not a leaked identifier.
        // The spaced prose form below is what a rail message uses.
        "opt-in", "opt in", "opted in", "opted into", "microalgo", "algorand", "solana",
        "ethereum", "polygon", "asset id", "asset index", "wallet address",
        "mnemonic", "seed phrase", "private key", "keypair", "gas fee", "gwei", "lamport",
        "blockchain", "on-chain", "on chain", "txid", "tx id", "transaction id",
        "signature", "signer", "custody", "base32", "base58", "asset holding",
        "min balance", "minimum balance", "min-balance", "clawback", "genesis",
    };

    /// <summary>
    /// Short rail words that are only rail words on a word boundary ("ASA", "ALGO",
    /// "wei", "token") plus the raw identifier shapes - a 58-character base32 account
    /// address and a 64-character hex digest - which are rail detail on their own.
    /// </summary>
    private static readonly Regex RailTokenOrIdentifier = new(
        @"\b(?:asa|asas|algo|algos|wei|token|tokens|chain|freeze|frozen)\b" +
        @"|\b[A-Z2-7]{58}\b|\b[A-Fa-f0-9]{64}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Maps one raw provider/rail failure to its consumer-safe code and message.</summary>
    public static ConsumerSafeError Map(string? providerMessage)
    {
        var raw = providerMessage ?? string.Empty;

        // Ordered, first-match-wins classification. The order matters: a pending
        // settlement mentions a transaction, and an oversubscribed reserve partition
        // mentions a balance, so the more specific classes are tested first.
        if (Mentions(raw, "not yet confirmed", "pending confirmation", "pendingconfirmation",
                "still settling", "reconcile against chain truth", "in progress"))
            return new ConsumerSafeError(ConsumerSafeErrorCodes.SettlementPending, SettlementPendingMessage);

        if (Mentions(raw, "opt-in", "opt in", "opted into", "not opted", "min balance",
                "minimum balance", "below the minimum", "min-balance", "account not found",
                "was not found on"))
            return new ConsumerSafeError(ConsumerSafeErrorCodes.RecipientNotReady, RecipientNotReadyMessage);

        // A node-governance refusal is policy, not weather. It is NOT_PERMITTED like a frozen
        // holding, but it gets its own prose because "try again shortly" would be a lie.
        if (Mentions(raw, "governance disallows", "governance requires", "allowlist is configured"))
            return new ConsumerSafeError(ConsumerSafeErrorCodes.NotPermitted, PolicyRefusedMessage);

        if (Mentions(raw, "frozen", "not authorised", "not authorized", "forbidden", "consent",
                "scope", "clawback", "refusing to fall back"))
            return new ConsumerSafeError(ConsumerSafeErrorCodes.NotPermitted, NotPermittedMessage);

        if (Mentions(raw, "oversubscribed", "insufficient", "not enough", "overdraw", "exceeds",
                "holds only", "outstanding", "underfunded", "cannot cover"))
            return new ConsumerSafeError(ConsumerSafeErrorCodes.InsufficientFunds, InsufficientFundsMessage);

        if (Mentions(raw, "not configured", "no platform signing", "no custody", "not supported",
                "not yet implemented", "no provider", "no transaction signer", "is disabled",
                "no reserve", "unavailable"))
            return new ConsumerSafeError(ConsumerSafeErrorCodes.RailUnavailable, RailUnavailableMessage);

        if (Mentions(raw, "rate limit", "too many requests", "429", "timeout", "timed out",
                "temporarily", "try again", "503", "502", "connection"))
            return new ConsumerSafeError(ConsumerSafeErrorCodes.RailBusy, RailBusyMessage);

        return new ConsumerSafeError(
            ConsumerSafeErrorCodes.TemporarilyUnavailable, TemporarilyUnavailableMessage);
    }

    /// <summary>Maps a raw rail failure straight onto a typed, consumer-safe failure result.</summary>
    public static AZOAResult<T> Failure<T>(string? providerMessage)
    {
        var mapped = Map(providerMessage);
        return AZOAResult<T>.FailureWithCode(mapped.Message, mapped.Code);
    }

    /// <summary>Maps a raw rail failure onto a consumer-safe failure with a caller-pinned code.</summary>
    public static AZOAResult<T> Failure<T>(string? providerMessage, string code)
    {
        var mapped = Map(providerMessage);
        return AZOAResult<T>.FailureWithCode(mapped.Message, code);
    }

    /// <summary>
    /// True when <paramref name="text"/> carries rail vocabulary a non-crypto sponsor must
    /// never see. Public because it is the assertion the allocation tests make against every
    /// message this node returns on a value path.
    /// </summary>
    public static bool LeaksRailDetail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lowered = text.ToLowerInvariant();
        foreach (var term in RailVocabulary)
        {
            if (lowered.Contains(term, StringComparison.Ordinal))
                return true;
        }

        return RailTokenOrIdentifier.IsMatch(text);
    }

    /// <summary>
    /// The routing prefix <paramref name="message"/> opens with, or empty when it carries none.
    /// </summary>
    public static string RoutingPrefixOf(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        foreach (var prefix in RoutingPrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
                return prefix;
        }

        return string.Empty;
    }

    /// <summary>
    /// The consumer-safe replacement for a message that carries rail detail, or null when the
    /// message may leave as written. A routing prefix survives the replacement, so a refusal
    /// that the API layer turns into a 403 does not quietly become a 400.
    /// </summary>
    public static ConsumerSafeError? Redact(string? message)
    {
        var raw = message ?? string.Empty;
        var prefix = RoutingPrefixOf(raw);

        // The LEAK test reads the body alone, so a routing prefix can never itself force a
        // scrub. The CLASSIFICATION reads the whole thing, because "KYC_FORBIDDEN" is the
        // strongest signal there is about what the refusal means.
        if (!LeaksRailDetail(raw[prefix.Length..])) return null;

        var mapped = Map(raw);
        return new ConsumerSafeError(mapped.Code, prefix + mapped.Message);
    }

    private static bool Mentions(string raw, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (raw.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
