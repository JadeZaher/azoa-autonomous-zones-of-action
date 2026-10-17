// SPDX-License-Identifier: UNLICENSED
// Provider-agnostic hosted-redirect flow DTOs. Nothing here is KYC-specific:
// a fiat-in checkout provider is the same shape. See Services/HostedSessions/AGENTS.md.

#nullable enable

using System.Text.Json.Serialization;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Models.HostedSessions;

/// <summary>Stable flow-family discriminators for <see cref="HostedSessionRequest.FlowKind"/>.</summary>
public static class HostedSessionFlowKinds
{
    /// <summary>Identity verification.</summary>
    public const string Kyc = "kyc";

    /// <summary>Business verification.</summary>
    public const string Kyb = "kyb";

    /// <summary>Reserved for the fiat-in checkout that reuses this seam.</summary>
    public const string FiatCheckout = "fiat-checkout";
}

/// <summary>Durable lifecycle states of a hosted session; mirrors the POCO allow-list.</summary>
public static class HostedSessionStates
{
    /// <summary>Admission claimed; no external call has been made yet.</summary>
    public const string Claimed = "CLAIMED";

    /// <summary>Provider session created; the subject holds a redirect.</summary>
    public const string AwaitingSubject = "AWAITING_SUBJECT";

    /// <summary>The provider signalled progress but has not reached a terminal verdict.</summary>
    public const string AwaitingOutcome = "AWAITING_OUTCOME";

    /// <summary>
    /// Non-terminal CAS CLAIM held while exactly one delivery runs domain resumption.
    /// It exists so the claim, not the terminal write, is what serialises concurrent
    /// callbacks: two deliveries that both read a non-terminal snapshot would
    /// otherwise both resume, and the CAS loser's divergent outcome would be
    /// discarded silently after its side effects had already landed.
    /// </summary>
    public const string Resuming = "RESUMING";

    /// <summary>Terminal: an authoritative verdict was re-fetched and recorded.</summary>
    public const string Completed = "COMPLETED";

    /// <summary>Terminal: the flow could not be completed.</summary>
    public const string Failed = "FAILED";

    /// <summary>Terminal: the claim expired before the subject finished.</summary>
    public const string Abandoned = "ABANDONED";

    /// <summary>States from which no further transition is permitted.</summary>
    public static bool IsTerminal(string? state)
        => state is Completed or Failed or Abandoned;
}

/// <summary>Non-secret capability report for a hosted-session provider.</summary>
public sealed class HostedSessionCapabilities
{
    public string ProviderKey { get; init; } = string.Empty;
    public string FlowKind { get; init; } = string.Empty;
    public bool Available { get; init; }

    /// <summary>Configuration key NAMES that are missing or invalid. Never values.</summary>
    public IReadOnlyList<string> MissingConfigurationKeys { get; init; } = [];

    public string? UnavailableReason { get; init; }
}

/// <summary>Inputs for creating a hosted provider session.</summary>
public sealed class HostedSessionRequest
{
    /// <summary>Internal current tenant authority for provider first-egress admission.</summary>
    [JsonIgnore]
    public TenantCustodialWitness? CustodialAuthority { get; init; }

    public string FlowKind { get; init; } = HostedSessionFlowKinds.Kyc;

    /// <summary>Subject user; also the provider-side client user correlation.</summary>
    public Guid SubjectUserId { get; init; }

    /// <summary>Owning tenant, when the flow was driven by a tenant rather than the node.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Stable admission key; the same key must resume rather than duplicate.</summary>
    public string AdmissionKey { get; init; } = string.Empty;

    /// <summary>Optional durable domain attempt correlation allocated before provider creation.</summary>
    public Guid? SessionId { get; init; }

    public string ProviderKey { get; init; } = string.Empty;

    public string PolicyRevision { get; init; } = string.Empty;
}

/// <summary>What a provider returns from an external create call.</summary>
public sealed class HostedSessionCreation
{
    /// <summary>Provider-side session id. Server-side only; never tenant-projected.</summary>
    public string ProviderSessionId { get; init; } = string.Empty;

    /// <summary>Secondary provider correlation, when the provider issues one.</summary>
    public string? ProviderCorrelationId { get; init; }

    /// <summary>The hosted HTTPS redirect handed to the subject.</summary>
    public string RedirectUrl { get; init; } = string.Empty;

    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// What a VERIFIED callback yields. Deliberately carries no outcome: the callback
/// is an untrusted trigger, so it may only identify which session to re-fetch.
/// </summary>
public sealed class HostedCallbackTrigger
{
    public Guid? AttemptId { get; init; }
    public string? EventId { get; init; }
    /// <summary>Provider session id named by the callback body.</summary>
    public string ProviderSessionId { get; init; } = string.Empty;

    /// <summary>Provider event discriminator, for observability only.</summary>
    public string? EventType { get; init; }
}

/// <summary>The authoritative, server-fetched provider verdict.</summary>
public sealed class HostedSessionOutcome
{
    /// <summary>The server returned a recognized session result, independently of its final verdict.</summary>
    public bool SessionExists { get; init; }

    /// <summary>Provider-native outcome token, normalised to upper case.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>
    /// Whether this verdict may be persisted as terminal. A provider "in manual
    /// review" state is explicitly non-terminal and must never satisfy a value gate.
    /// </summary>
    public bool IsTerminal { get; init; }

    /// <summary>Whether a terminal outcome is a success.</summary>
    public bool IsApproved { get; init; }

    /// <summary>Non-secret detail safe to persist and show an operator.</summary>
    public string? Detail { get; init; }

    /// <summary>Verified person attributes, when the flow produces an identity.</summary>
    public HostedSessionSubjectAttributes? Subject { get; init; }

    public DateTimeOffset? EvidenceValidUntil { get; init; }

    /// <summary>Business evidence and server-only beneficiary linkage for KYB.</summary>
    public AZOA.WebAPI.Models.Kyb.KybBusinessEvidence? Business { get; init; }
}

/// <summary>
/// The ONLY person attributes AZOA accepts from a provider result. Address,
/// personal/national number, and every media or biometric artefact are omitted by
/// construction — they have no property to land in.
/// </summary>
public sealed class HostedSessionSubjectAttributes
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Birthday { get; init; }
    public string? Citizenship { get; init; }

    /// <summary>Used only to derive the salted person key; never persisted raw.</summary>
    public string? DocumentNumber { get; init; }

    /// <summary>Used only to derive the salted person key; never persisted raw.</summary>
    public string? IssuingState { get; init; }
}

/// <summary>Non-sensitive result of beginning a hosted flow.</summary>
public sealed class HostedSessionStartResult
{
    public Guid SessionId { get; init; }
    public string ProviderKey { get; init; } = string.Empty;
    public string RedirectUrl { get; init; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>True when an existing unexpired claim was resumed instead of created.</summary>
    public bool Resumed { get; init; }
}

/// <summary>Result of processing one inbound callback.</summary>
public sealed class HostedCallbackResult
{
    public Guid SessionId { get; init; }
    public string State { get; init; } = string.Empty;

    /// <summary>The authoritative outcome re-fetched from the provider.</summary>
    public string? Outcome { get; init; }

    /// <summary>True when this delivery performed the single terminal write.</summary>
    public bool TerminalWriteApplied { get; init; }

    /// <summary>True when the session was already terminal, i.e. this was a redelivery.</summary>
    public bool AlreadyTerminal { get; init; }

    /// <summary>
    /// True when a concurrent delivery held the resume claim, so this one did no
    /// domain work at all. Distinct from <see cref="AlreadyTerminal"/>: the session
    /// was still non-terminal when this delivery lost the claim.
    /// </summary>
    public bool ResumeClaimLost { get; init; }
}
