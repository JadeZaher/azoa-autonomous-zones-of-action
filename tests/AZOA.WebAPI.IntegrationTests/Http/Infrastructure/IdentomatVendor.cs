using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>The verdict the fake vendor reports from its authoritative result endpoints.</summary>
public enum IdentomatVerdict
{
    /// <summary>Not decided (e.g. not yet submitted): no documented verdict value, so no <c>result</c> is reported.</summary>
    Pending,

    /// <summary>Submitted and held for a human decision: the documented <c>MANUAL_CHECK</c>.</summary>
    ManualCheck,
    Approved,
    Rejected,
}

/// <summary>How the vendor configuration behind a <c>configId</c> answers <c>/begin</c>.</summary>
public enum IdentomatSessionMode
{
    /// <summary>Documented default: <c>{ id, url: "?session_token=&lt;id&gt;" }</c>; the id IS the session token.</summary>
    Standard,

    /// <summary>
    /// <c>additionalInformationRequest</c> enabled: parent <c>id</c> plus a child
    /// <c>additionalSessionId</c> that the <c>url</c> carries. Which token the legacy
    /// <c>/external-api/result/</c> accepts for such a pair is NOT documented; this variant assumes
    /// the applicant-facing (url) token and must be confirmed with the vendor before a test relies on it.
    /// </summary>
    AdditionalInformation,
}

/// <summary>One hosted session the fake vendor created.</summary>
public sealed class IdentomatSession
{
    /// <summary>The (parent) session id: what callbacks name and what Manage tracks.</summary>
    public required string Id { get; init; }

    /// <summary>The <c>session_token</c> the applicant-facing <c>url</c> carries.</summary>
    public required string SessionToken { get; init; }

    /// <summary>Child id; present only in <see cref="IdentomatSessionMode.AdditionalInformation"/>.</summary>
    public string? AdditionalSessionId { get; init; }

    public required string ClientUserId { get; init; }
    public required bool IsBusiness { get; init; }
    public IdentomatVerdict Verdict { get; set; } = IdentomatVerdict.Pending;

    /// <summary>Set by <c>POST /external-api/delete/</c>; a deleted session no longer answers the result endpoints.</summary>
    public bool Deleted { get; set; }
}

/// <summary>
/// Behavioral fake of the Identomat hosts the adapters call: modern <c>/begin</c>, KYB
/// <c>/result</c>, AML screening on the API host and legacy <c>/external-api/result/</c> on the
/// widget host. Response shapes follow the vendor documentation captured in
/// <c>.omc/research/identomat-api-2026-10-15.md</c>; see <c>Http/AGENTS.md</c> §identomat-fake.
/// </summary>
public sealed class IdentomatVendor
{
    public const string ApiHost = "identomat-api.vendor.test";
    public const string WidgetHost = "identomat-widget.vendor.test";

    private readonly ConcurrentQueue<IdentomatSession> _sessions = new();
    private readonly Lock _beginGate = new();
    private int _beginResponsesToLose;

    public IdentomatVendor(VendorEdge edge, string companyKey, string kycConfigId, string kybConfigId,
        IdentomatSessionMode mode = IdentomatSessionMode.Standard)
    {
        CompanyKey = companyKey;
        KycConfigId = kycConfigId;
        KybConfigId = kybConfigId;
        Mode = mode;
        edge.Register(ApiHost, RespondFromApiHost);
        edge.Register(WidgetHost, RespondFromWidgetHost);
    }

    public string CompanyKey { get; }
    public string KycConfigId { get; }
    public string KybConfigId { get; }
    public IdentomatSessionMode Mode { get; }

    /// <summary>Sessions in creation order.</summary>
    public IReadOnlyList<IdentomatSession> Sessions => _sessions.ToArray();

    /// <summary>The newest business (KYB) session, or null.</summary>
    public IdentomatSession? LatestBusinessSession => Sessions.LastOrDefault(session => session.IsBusiness);

    /// <summary>The newest identity (KYC) session for <paramref name="subjectUserId"/>, or null.</summary>
    public IdentomatSession? LatestIdentitySessionFor(Guid subjectUserId)
        => Sessions.LastOrDefault(session => !session.IsBusiness && session.ClientUserId == subjectUserId.ToString("N"));

    /// <summary>
    /// Creates the next session at the vendor but drops the response, as a timeout after the
    /// vendor committed would. This is the ambiguous-creation case operator reconciliation exists for.
    /// </summary>
    public void LoseNextBeginResponse()
    {
        lock (_beginGate) _beginResponsesToLose++;
    }

    /// <summary>The session-change callback body Identomat posts to the node.</summary>
    public static string CallbackBody(string callbackApiKey, string sessionId, string claimedResult)
        => new JsonObject
        {
            ["callbackApiKey"] = callbackApiKey,
            ["type"] = "session-change",
            ["sessionId"] = sessionId,
            ["result"] = claimedResult,
        }.ToJsonString();

    private HttpResponseMessage RespondFromApiHost(VendorRequest request)
    {
        if (request.Method != HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        var body = JsonNode.Parse(request.Body)?.AsObject();
        if (body is null || body["companyKey"]?.GetValue<string>() != CompanyKey)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        return request.Path switch
        {
            "/begin" => Begin(body),
            "/result" => BusinessResult(body["sessionId"]?.GetValue<string>()),
            "/search-screening-person" => VendorEdge.Json(HttpStatusCode.OK, """{"result":[]}"""),
            "/monitor-screening-query" => VendorEdge.Json(HttpStatusCode.OK,
                new JsonObject { ["result"] = "monitor-" + Guid.NewGuid().ToString("N") }.ToJsonString()),
            "/remove-screening-query-from-monitoring" => VendorEdge.Json(HttpStatusCode.OK, """{"result":true}"""),
            "/list-screening-monitoring-records" => VendorEdge.Json(HttpStatusCode.OK,
                """{"result":{"start":0,"limit":100,"total":0,"data":[]}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage Begin(JsonObject body)
    {
        var configId = body["configId"]?.GetValue<string>();
        if (configId != KycConfigId && configId != KybConfigId)
            return VendorEdge.Json(HttpStatusCode.OK, """{"argumentError":"unknown configId"}""");

        var id = "idm-session-" + Guid.NewGuid().ToString("N");
        var child = Mode == IdentomatSessionMode.AdditionalInformation ? "idm-child-" + Guid.NewGuid().ToString("N") : null;
        var session = new IdentomatSession
        {
            Id = id,
            SessionToken = child ?? id,
            AdditionalSessionId = child,
            ClientUserId = body["clientUserId"]?.GetValue<string>() ?? string.Empty,
            IsBusiness = configId == KybConfigId,
        };
        _sessions.Enqueue(session);

        lock (_beginGate)
        {
            if (_beginResponsesToLose > 0)
            {
                _beginResponsesToLose--;
                return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
            }
        }

        var widgetUrl = $"https://{WidgetHost}/?session_token={session.SessionToken}";
        var response = new JsonObject { ["id"] = session.Id, ["url"] = widgetUrl };
        if (child is not null)
        {
            response["additionalSessionId"] = child;
            response["additionalSessionUrl"] = widgetUrl;
        }
        return VendorEdge.Json(HttpStatusCode.OK, response.ToJsonString());
    }

    /// <summary>KYB authoritative result (modern host, camelCase, keyed by the session id).</summary>
    private HttpResponseMessage BusinessResult(string? sessionId)
    {
        var session = Sessions.FirstOrDefault(candidate => candidate.IsBusiness && candidate.Id == sessionId);
        if (session is null)
            return VendorEdge.Json(HttpStatusCode.OK, """{"argumentError":"session not found"}""");

        var review = session.Verdict switch
        {
            IdentomatVerdict.Approved => "approved",
            IdentomatVerdict.Rejected => "rejected",
            _ => "in-review",
        };
        var steps = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "company",
                ["status"] = "approved",
                ["result"] = new JsonObject
                {
                    ["companyName"] = "Flow Test Holdings",
                    ["registrationNumber"] = "FT-0001",
                    ["registrationCountry"] = "US",
                },
            },
            new JsonObject
            {
                ["type"] = "beneficiaries",
                ["status"] = "approved",
                ["result"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["entityType"] = "individual",
                        ["positions"] = new JsonArray("director"),
                        ["_id"] = "participant-" + session.Id,
                        ["sessionIds"] = new JsonArray("beneficiary-" + session.Id),
                    },
                },
            },
            new JsonObject { ["type"] = "kyb-review", ["status"] = review },
        };
        return VendorEdge.Json(HttpStatusCode.OK,
            new JsonObject { ["result"] = new JsonObject { ["stepsResults"] = steps } }.ToJsonString());
    }

    /// <summary>Legacy <c>/external-api/result/</c> calls received so far (budget assertions).</summary>
    public int IdentityResultRequests => Volatile.Read(ref _identityResultRequests);

    private int _identityResultRequests;

    /// <summary>Session tokens the node asked the vendor to delete, in request order.</summary>
    public IReadOnlyList<string> DeleteRequests => _deleteRequests.ToArray();

    private readonly ConcurrentQueue<string> _deleteRequests = new();

    private HttpResponseMessage RespondFromWidgetHost(VendorRequest request)
    {
        if (request.Method != HttpMethod.Post || request.Path is not ("/external-api/result/" or "/external-api/delete/"))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (request.Path == "/external-api/result/")
            Interlocked.Increment(ref _identityResultRequests);
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        if (!root.TryGetProperty("company_key", out var key) || key.GetString() != CompanyKey)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        // Keyed by the session token the applicant URL carries: in standard mode that is the id.
        var token = root.TryGetProperty("session_token", out var tokenElement) ? tokenElement.GetString() : null;
        var session = Sessions.FirstOrDefault(candidate => !candidate.IsBusiness && candidate.SessionToken == token && !candidate.Deleted);
        if (request.Path == "/external-api/delete/")
            return Delete(token, session);
        if (session is null)
            return VendorEdge.Json(HttpStatusCode.OK, """{"result":null}""");

        return VendorEdge.Json(HttpStatusCode.OK, session.Verdict switch
        {
            IdentomatVerdict.Approved => new JsonObject
            {
                ["result"] = "approved",
                ["person"] = new JsonObject
                {
                    ["first_name"] = "Flow",
                    ["last_name"] = "Tester",
                    ["birthday"] = "1990-04-01",
                    ["citizenship"] = "US",
                    ["document_number"] = "D" + session.Id[^8..],
                    ["issuing_state"] = "US",
                },
            }.ToJsonString(),
            IdentomatVerdict.Rejected => """{"result":"rejected","reject_reason":{"value":"DOCUMENT_EXPIRED","description":"Document expired"}}""",
            IdentomatVerdict.ManualCheck => """{"result":"MANUAL_CHECK"}""",
            // The docs define no undecided shape; the fake reports no verdict (see Http/AGENTS.md §identomat-fake).
            _ => """{"result":null}""",
        });
    }

    // Documented answers: `true` when deleted, `{"argumentError":"session-not-found"}` otherwise (IdentomatSessionDeletion).
    private HttpResponseMessage Delete(string? token, IdentomatSession? session)
    {
        _deleteRequests.Enqueue(token ?? string.Empty);
        if (session is null)
            return VendorEdge.Json(HttpStatusCode.OK, """{"argumentError":"session-not-found"}""");
        session.Deleted = true;
        return VendorEdge.Json(HttpStatusCode.OK, "true");
    }
}
