// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Settings;

/// <summary>
/// KYC module configuration, bound from the <c>Kyc</c> section. The base
/// provider is unavailable. Development may explicitly select manual review;
/// external adapters fail closed until reviewed. See
/// docs/TENANT-CUSTODIAL-ONBOARDING.md.
/// </summary>
public sealed class KycSettings
{
    public const string SectionName = "Kyc";

    /// <summary>
    /// Selected provider: <c>manual</c>, <c>veriff</c>, or <c>hosted</c>.
    /// Unknown values select an unavailable provider rather than falling back.
    /// </summary>
    public string Provider { get; set; } = "unavailable";

    /// <summary>External provider API key — NEVER committed; supplied at deploy time.</summary>
    public string? VeriffApiKey { get; set; }

    /// <summary>External provider API base URL — supplied at deploy time.</summary>
    public string? VeriffBaseUrl { get; set; }

    public string? VeriffSharedSecret { get; set; }
    public string? VeriffReturnUrl { get; set; }
    public string[] VeriffRedirectOrigins { get; set; } = ["https://magic.veriff.me"];

    /// <summary>Versioned trusted hosted identity protocol configuration.</summary>
    public HostedKycSettings Hosted { get; set; } = new();

    /// <summary>Identomat hosted-KYC + AML-screening adapter configuration.</summary>
    public IdentomatKycSettings Identomat { get; set; } = new();

    /// <summary>Person-key derivation, retention, and erasure policy (D23/D25).</summary>
    public KycIdentitySettings Identity { get; set; } = new();

    /// <summary>Operator-owned trust profile stamped onto each verification attempt.</summary>
    public KycApprovalPolicySettings ApprovalPolicy { get; set; } = new();

    /// <summary>Days until a submission expires; runtime clamps to 1-365.</summary>
    public int SubmissionExpiryDays { get; set; } = 30;

    /// <summary>Minutes a begin-session response may be replayed; runtime clamps to 1-1440.</summary>
    public int SessionExpiryMinutes { get; set; } = 30;
}

/// <summary>Fail-closed policy and assurance configuration for KYC approvals.</summary>
public sealed class KycApprovalPolicySettings
{
    /// <summary>Operator-controlled version; changing it requires re-verification.</summary>
    public string PolicyVersion { get; set; } = string.Empty;

    /// <summary>Provider-neutral assurance label required by the active policy.</summary>
    public string AssuranceLevel { get; set; } = string.Empty;

    /// <summary>Provider keys the operator accepts as identity authorities.</summary>
    public string[] TrustedProviderKeys { get; set; } = Array.Empty<string>();

    /// <summary>Explicit opt-in for the manual provider in Development only.</summary>
    public bool AllowManualInDevelopment { get; set; }
}

/// <summary>Operator-owned version-one hosted identity protocol and key-rotation policy.</summary>
public sealed class HostedKycSettings
{
    public string ProtocolVersion { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
    public string[] RedirectOrigins { get; set; } = [];
    public string? WebhookKeyId { get; set; }
    public List<HostedKycRetiredKey> RetiredWebhookKeys { get; set; } = [];
    /// <summary>Public provider label returned in capability/session metadata.</summary>
    public string? ProviderName { get; set; }

    /// <summary>HTTPS API origin for the provider.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Provider credential; secret-store only.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Inbound webhook verification secret; secret-store only.</summary>
    public string? WebhookSecret { get; set; }

}

public sealed class HostedKycRetiredKey
{
    public string KeyId { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public DateTimeOffset RetiredAt { get; set; }
    public DateTimeOffset AcceptUntil { get; set; }
}

/// <summary>
/// Identomat adapter configuration. The adapter genuinely spans TWO hosts:
/// <see cref="BeginBaseUrl"/> is the newer camelCase JSON API (session begin plus
/// all five AML screening endpoints) and <see cref="ResultBaseUrl"/> is the legacy
/// snake_case widget host that serves the authoritative <c>/result/</c> fetch.
/// They are separate configured values on purpose — see
/// <c>Providers/Kyc/AGENTS.md</c> §identomat. Credential VALUES never live in the
/// repo; only their configuration key names do.
/// </summary>
public sealed class IdentomatKycSettings
{
    /// <summary>Configuration key names surfaced to operators; never values.</summary>
    public const string CompanyKeyName = "Kyc__Identomat__CompanyKey";
    public const string ConfigIdName = "Kyc__Identomat__ConfigId";
    public const string CallbackApiKeyName = "Kyc__Identomat__CallbackApiKey";
    public const string BeginBaseUrlName = "Kyc__Identomat__BeginBaseUrl";
    public const string ResultBaseUrlName = "Kyc__Identomat__ResultBaseUrl";

    /// <summary>Named <see cref="System.Net.Http.IHttpClientFactory"/> client.</summary>
    public const string HttpClientName = "identomat";

    /// <summary>camelCase JSON host: <c>/begin</c> plus all AML screening endpoints.</summary>
    public string? BeginBaseUrl { get; set; } = "https://external-api.identomat.com";

    /// <summary>Legacy snake_case widget host serving the authoritative <c>/result/</c>.</summary>
    public string? ResultBaseUrl { get; set; } = "https://widget.identomat.com";

    /// <summary>Shared company secret (<c>companyKey</c> / <c>company_key</c>); secret-store only.</summary>
    public string? CompanyKey { get; set; }

    /// <summary>Verification flow configuration id supplied to <c>/begin</c>; secret-store only.</summary>
    public string? ConfigId { get; set; }

    /// <summary>Inbound-callback shared secret compared in constant time; secret-store only.</summary>
    public string? CallbackApiKey { get; set; }

    /// <summary>Per-request timeout for every Identomat call; clamped to 1-120s.</summary>
    public int RequestTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Ceiling for the ONE shared per-IP budget covering KYC session calls, AML
    /// screening, and monitor polls. Identomat bans the IP for an hour (HTTP 529)
    /// past 600/min, so the default leaves deliberate headroom.
    ///
    /// <para>This is the NODE-WIDE ceiling, but it is enforced PER PROCESS. Set
    /// <see cref="ReplicaCount"/> whenever more than one replica shares an egress
    /// IP, or the effective rate becomes this value multiplied by the replica count
    /// — which is precisely the vendor ban this budget exists to prevent.</para>
    /// </summary>
    public int SharedRequestsPerMinute { get; set; } = 480;

    /// <summary>
    /// Replicas sharing one egress IP. The per-process budget is
    /// <see cref="SharedRequestsPerMinute"/> divided by this, because the vendor's
    /// ceiling is per IP while the counter is per process; there is no shared
    /// counter, so the division is the only thing keeping N replicas from spending
    /// N times the node's allowance. Clamped to 1-100; 1 means single-replica.
    /// </summary>
    public int ReplicaCount { get; set; } = 1;

    /// <summary>
    /// Percent of the per-minute budget that result re-fetches (poll, refresh, callbacks) may use, so they
    /// can never starve begin and AML screening. Clamped to 10-100.
    /// </summary>
    public int ResultRequestSharePercent { get; set; } = 50;

    /// <summary>Minutes the adapter stays circuit-open after an observed HTTP 529 ban.</summary>
    public int RateLimitBanCooldownMinutes { get; set; } = 60;

    /// <summary>Screening/risk policy knobs.</summary>
    public IdentomatScreeningSettings Screening { get; set; } = new();

    /// <summary>Per-tenant call quotas; see <see cref="KycTenantQuotaSettings"/>.</summary>
    public KycTenantQuotaSettings TenantQuota { get; set; } = new();

    /// <summary>Lost-callback outcome poll; see <see cref="IdentomatOutcomePollSettings"/>.</summary>
    public IdentomatOutcomePollSettings OutcomePoll { get; set; } = new();

    /// <summary>Orphan supersede: bounded wait before a possibly-sent create counts as an orphan.</summary>
    public int OrphanWaitMinutes { get; set; } = 5;

    /// <summary>Orphans one subject may supersede per rolling day; past it the admission waits for an operator.</summary>
    public int OrphanSupersedesPerDay { get; set; } = 1;
}

/// <summary>
/// Backoff poll of <c>/external-api/result/</c> for attached Identomat sessions whose callback was lost.
/// Every call spends the shared <c>IdentomatRateBudget</c>. See <c>Services/HostedSessions/AGENTS.md</c> §recovery.
/// </summary>
public sealed class IdentomatOutcomePollSettings
{
    /// <summary>Unset means on wherever an Identomat config id is set (plan A-4 posture).</summary>
    public bool? Enabled { get; set; }

    /// <summary>Seconds between sweeps; clamped to 5-600.</summary>
    public int SweepIntervalSeconds { get; set; } = 30;

    /// <summary>Sessions polled per sweep; clamped to 1-100. Bounds the share of the shared budget.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>First backoff step in seconds; doubles per poll. Clamped to 10-3600.</summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>Backoff ceiling in minutes; clamped to 1-1440.</summary>
    public int MaxIntervalMinutes { get; set; } = 60;

    /// <summary>Backstop: hours after creation a session whose attempt state is unknown is abandoned; clamped to 1-2160. Live attempts poll until they expire.</summary>
    public int MaxAgeHours { get; set; } = 720;

    /// <summary>Minimum seconds between two user/tenant-initiated re-fetches of one session; clamped to 5-3600.</summary>
    public int RefreshCooldownSeconds { get; set; } = 30;

    /// <summary>The clamped on-demand refresh cooldown.</summary>
    public TimeSpan RefreshCooldown => TimeSpan.FromSeconds(Math.Clamp(RefreshCooldownSeconds, 5, 3600));
}

/// <summary>
/// AML screening + ongoing-monitoring policy. Identomat exposes no
/// APPROVED/REJECTED enum, so every band below is an AZOA operator decision.
/// </summary>
public sealed class IdentomatScreeningSettings
{
    /// <summary>Server-side filter sent as <c>query.scoreThreshold</c>; clamped to 1-100.</summary>
    public int ScoreThreshold { get; set; } = 80;

    /// <summary>At or above this score a hit is auto-blocking; clamped to 1-100.</summary>
    public int AutoBlockScore { get; set; } = 95;

    /// <summary>
    /// Topics that block at ANY returned score. Modelled as an open string set —
    /// the vendor does not publish an exhaustive topic list, so this is a
    /// case-insensitive substring-free exact-match allow list, never a C# enum.
    /// </summary>
    public string[] BlockingTopics { get; set; } =
    [
        "Sanctioned entity",
        "Counter-sanctioned entity",
        "Debarred entity",
        "Wanted person",
    ];

    /// <summary>Window size for one page of a sanctions search; clamped to 1-500.</summary>
    public int SearchPageSize { get; set; } = 100;

    /// <summary>
    /// Pages a single sanctions search may walk before it gives up and marks the
    /// result truncated. The search response carries no <c>total</c> and no
    /// documented sort order, so an unpaged read cannot even detect that it
    /// saturated; a truncated result is forced to manual review, never a pass.
    /// Clamped to 1-50.
    /// </summary>
    public int SearchMaxPages { get; set; } = 5;

    /// <summary>Page size for the batched monitoring list poll; clamped to 1-500.</summary>
    public int MonitorPollPageSize { get; set; } = 100;

    /// <summary>Minutes between batched monitoring polls; clamped to 5-1440.</summary>
    public int MonitorPollIntervalMinutes { get; set; } = 60;

    /// <summary>Cold-poll monitoring switch; unset means on wherever an Identomat config id is set (plan A-4).</summary>
    public bool? MonitorPollEnabled { get; set; }

    /// <summary>Maximum pages walked per poll cycle; bounds the shared rate budget.</summary>
    public int MonitorPollMaxPagesPerCycle { get; set; } = 10;
}

/// <summary>
/// Per-tenant call quota. The Identomat per-IP cap is a single shared budget for
/// the whole node, so without this one consumer's begin-session storm evicts every
/// other tenant from KYC — a cross-tenant denial of service.
/// </summary>
public sealed class KycTenantQuotaSettings
{
    /// <summary>Begin-session calls one tenant may make per window; clamped to 1-10000.</summary>
    public int BeginSessionsPerWindow { get; set; } = 20;

    /// <summary>Screening calls one tenant may make per window; clamped to 1-10000.</summary>
    public int ScreeningCallsPerWindow { get; set; } = 20;

    /// <summary>Quota window length in seconds; clamped to 1-3600.</summary>
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// Identity-binding policy: how the salted person key is derived (D23) and how
/// long the four retained personal fields survive before erasure (D25).
/// </summary>
public sealed class KycIdentitySettings
{
    /// <summary>Configuration key name surfaced to operators; never the value.</summary>
    public const string PersonKeySecretName = "Kyc__Identity__PersonKeySecret";

    /// <summary>
    /// Root secret from which every per-tenant person-key salt is derived via
    /// HKDF. Secret-store only; rotating it re-pseudonymises every person key.
    /// </summary>
    public string? PersonKeySecret { get; set; }

    /// <summary>Days the four retained personal fields are kept; clamped to 1-3650.</summary>
    public int RetentionDays { get; set; } = 1825;

    /// <summary>
    /// Hours between retention sweeps. The sweep makes no vendor calls, so it has no
    /// rate-budget cost and is always on: an unswept <c>retention_expires_at</c> is a
    /// retention policy that exists only in the schema. Clamped to 1-168.
    /// </summary>
    public int RetentionSweepIntervalHours { get; set; } = 24;

    /// <summary>
    /// Allows a node operator to resolve a person across tenants for one explicit
    /// operation. Off by default: per-tenant scope is the default uniqueness domain
    /// and cross-tenant linkage is never implicit.
    /// </summary>
    public bool GlobalScopeOptInAvailable { get; set; }
}
