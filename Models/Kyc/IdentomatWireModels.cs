// SPDX-License-Identifier: UNLICENSED
// Identomat wire DTOs. Field names and casing come straight from the vendor
// contract captured in .omc/research/identomat-kyc-api-2026-08-20.md and
// .omc/research/identomat-aml-screening-2026-08-21.md.
//
// The two hosts use DIFFERENT casing conventions and this is not a typo:
//   external-api.identomat.com -> camelCase  ("companyKey")
//   widget.identomat.com       -> snake_case ("company_key")
// Each DTO below is annotated with the host it belongs to.

#nullable enable

using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Kyc;

// ── external-api.identomat.com (camelCase) ────────────────────────────────────

/// <summary>Request body for <c>POST /begin</c>.</summary>
public sealed class IdentomatBeginRequest
{
    [JsonPropertyName("companyKey")]
    public string CompanyKey { get; set; } = string.Empty;

    [JsonPropertyName("configId")]
    public string? ConfigId { get; set; }

    /// <summary>The AZOA user id — this is the user-to-person binding correlation.</summary>
    [JsonPropertyName("clientUserId")]
    public string? ClientUserId { get; set; }
}

/// <summary>Response body from <c>POST /begin</c>.</summary>
public sealed class IdentomatBeginResponse
{
    /// <summary>Parent session id; retained server-side, never tenant-projected.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Hosted widget redirect carrying the child session token.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Child session id; this is the <c>session_token</c> the result host wants.</summary>
    [JsonPropertyName("additionalSessionId")]
    public string? AdditionalSessionId { get; set; }

    [JsonPropertyName("additionalSessionUrl")]
    public string? AdditionalSessionUrl { get; set; }
}

/// <summary>Inbound callback body. An untrusted TRIGGER, never an authority.</summary>
public sealed class IdentomatCallbackPayload
{
    /// <summary>Shared secret carried in the body; compared in constant time only.</summary>
    [JsonPropertyName("callbackApiKey")]
    public string? CallbackApiKey { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>
    /// DELIBERATELY IGNORED. The adapter discards this and re-fetches
    /// <c>/result/</c>; a body-borne verdict authenticated only by a shared secret
    /// is not an authority. Kept as a field solely so the deserializer does not
    /// choke and so the discard is visible rather than implicit.
    /// </summary>
    [JsonPropertyName("result")]
    public string? UntrustedResult { get; set; }
}

// ── widget.identomat.com/external-api/ (snake_case, legacy) ───────────────────

/// <summary>Request body for the authoritative <c>POST /external-api/result/</c>.</summary>
public sealed class IdentomatResultRequest
{
    [JsonPropertyName("company_key")]
    public string CompanyKey { get; set; } = string.Empty;

    [JsonPropertyName("session_token")]
    public string SessionToken { get; set; } = string.Empty;
}

/// <summary>
/// Authoritative result. Only the fields AZOA is willing to consume are declared:
/// there is intentionally no property for address, personal_number, face images,
/// or any other media, so those values cannot reach a persistence path even by
/// accident.
/// </summary>
public sealed class IdentomatResultResponse
{
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("person")]
    public IdentomatPerson? Person { get; set; }

    [JsonPropertyName("reject_reason")]
    public IdentomatRejectReason? RejectReason { get; set; }
}

/// <summary>Person block of the result. See the omissions note on the parent type.</summary>
public sealed class IdentomatPerson
{
    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("birthday")]
    public string? Birthday { get; set; }

    [JsonPropertyName("citizenship")]
    public string? Citizenship { get; set; }

    /// <summary>Consumed only to derive the salted person key; never persisted raw.</summary>
    [JsonPropertyName("document_number")]
    public string? DocumentNumber { get; set; }

    /// <summary>Consumed only to derive the salted person key; never persisted raw.</summary>
    [JsonPropertyName("issuing_state")]
    public string? IssuingState { get; set; }
}

/// <summary>Structured rejection reason; treated as non-secret operator detail.</summary>
public sealed class IdentomatRejectReason
{
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

// ── AML screening (external-api.identomat.com, camelCase) ─────────────────────

/// <summary>Query shape shared by search and monitor registration.</summary>
public sealed class IdentomatScreeningQuery
{
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("birthdayTimeFrom")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BirthdayTimeFrom { get; set; }

    /// <summary>
    /// Present on search only. <c>monitor-screening-query</c> accepts no threshold,
    /// so it stays null there; the emitter omits nulls.
    /// </summary>
    [JsonPropertyName("scoreThreshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScoreThreshold { get; set; }
}

/// <summary>Request body for <c>POST /search-screening-person</c>.</summary>
public sealed class IdentomatScreeningSearchRequest
{
    [JsonPropertyName("companyKey")]
    public string CompanyKey { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public IdentomatScreeningQuery Query { get; set; } = new();

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 100;
}

/// <summary>Response body from <c>POST /search-screening-person</c>.</summary>
public sealed class IdentomatScreeningSearchResponse
{
    [JsonPropertyName("result")]
    public List<IdentomatScreeningHit>? Result { get; set; }

    [JsonPropertyName("argumentError")]
    public string? ArgumentError { get; set; }
}

/// <summary>One screening hit. <c>topics</c> is an OPEN string set, never an enum.</summary>
public sealed class IdentomatScreeningHit
{
    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("personId")]
    public string? PersonId { get; set; }

    [JsonPropertyName("entityType")]
    public string? EntityType { get; set; }

    [JsonPropertyName("topics")]
    public List<string>? Topics { get; set; }
}

/// <summary>Request body for <c>POST /get-screening-person-details</c>.</summary>
public sealed class IdentomatScreeningDetailsRequest
{
    [JsonPropertyName("companyKey")]
    public string CompanyKey { get; set; } = string.Empty;

    [JsonPropertyName("personId")]
    public string PersonId { get; set; } = string.Empty;
}

/// <summary>Request body for <c>POST /monitor-screening-query</c>.</summary>
public sealed class IdentomatMonitorRegisterRequest
{
    [JsonPropertyName("companyKey")]
    public string CompanyKey { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public IdentomatScreeningQuery Query { get; set; } = new();
}

/// <summary>
/// Response from <c>POST /monitor-screening-query</c>. An
/// <c>argumentError = "already-exists"</c> is the vendor's own dedup guard and is
/// benign idempotent success, NOT a failure.
/// </summary>
public sealed class IdentomatMonitorRegisterResponse
{
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("argumentError")]
    public string? ArgumentError { get; set; }
}

/// <summary>Request body for <c>POST /remove-screening-query-from-monitoring</c>.</summary>
public sealed class IdentomatMonitorRemoveRequest
{
    [JsonPropertyName("companyKey")]
    public string CompanyKey { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

/// <summary>Response from the monitor removal call.</summary>
public sealed class IdentomatMonitorRemoveResponse
{
    [JsonPropertyName("result")]
    public bool Result { get; set; }

    [JsonPropertyName("argumentError")]
    public string? ArgumentError { get; set; }
}

/// <summary>Request body for <c>POST /list-screening-monitoring-records</c>.</summary>
public sealed class IdentomatMonitorListRequest
{
    [JsonPropertyName("companyKey")]
    public string CompanyKey { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 100;

    /// <summary>ISO-8601 high-water mark; only records after it are returned.</summary>
    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Date { get; set; }
}

/// <summary>Response from the paginated monitoring list.</summary>
public sealed class IdentomatMonitorListResponse
{
    [JsonPropertyName("result")]
    public IdentomatMonitorListPage? Result { get; set; }

    [JsonPropertyName("argumentError")]
    public string? ArgumentError { get; set; }
}

/// <summary>One page of monitoring records.</summary>
public sealed class IdentomatMonitorListPage
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("data")]
    public List<IdentomatMonitorListRecord>? Data { get; set; }
}

/// <summary>
/// A monitoring record as returned by the LIST call. It carries no match detail,
/// which is exactly why a changed <c>lastUpdatedDate</c> forces a follow-up fetch.
/// </summary>
public sealed class IdentomatMonitorListRecord
{
    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("lastUpdatedDate")]
    public string? LastUpdatedDate { get; set; }
}
