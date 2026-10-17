// SPDX-License-Identifier: UNLICENSED

using System.Text.Json.Nodes;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>
/// Documented <c>POST /external-api/delete/</c> on the widget host (snake_case <c>company_key</c>,
/// <c>session_token</c>; answers <c>true</c> or <c>{"argumentError":"session-not-found"}</c>).
/// Source: <c>.omc/research/identomat-pending-result-find-2026-10-15.md</c> L914-L959.
/// </summary>
public static class IdentomatSessionDeletion
{
    /// <summary>Widget-host path, same casing family as <c>/external-api/result/</c>.</summary>
    public const string Path = "/external-api/delete/";

    /// <summary>Deletes one vendor session through the shared transport and budget.</summary>
    public static async Task<AZOAResult<bool>> DeleteAsync(IIdentomatHttpTransport transport, string resultBaseUrl,
        string companyKey, string providerSessionId, string operation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerSessionId) || providerSessionId.Length > 256)
            return AZOAResult<bool>.Failure("A provider session id is required.");
        var response = await transport.PostAsync<IdentomatResultRequest, JsonNode>(resultBaseUrl, Path,
            new IdentomatResultRequest { CompanyKey = companyKey.Trim(), SessionToken = providerSessionId.Trim() },
            operation, ct);
        return Interpret(response);
    }

    /// <summary><c>true</c> deletes; a session the vendor no longer has is already gone; anything else fails.</summary>
    public static AZOAResult<bool> Interpret(AZOAResult<JsonNode> response)
    {
        if (response.IsError || response.Result is null)
            return AZOAResult<bool>.FailureWithCode(response.Message, response.Code ?? AzoaErrorCodes.DependencyUnavailable);
        if (response.Result is JsonValue value && value.TryGetValue<bool>(out var deleted) && deleted)
            return AZOAResult<bool>.Success(true);
        if (response.Result is JsonObject body && body["argumentError"] is JsonValue error
            && error.TryGetValue<string>(out var code) && code == "session-not-found")
            return AZOAResult<bool>.Success(true);
        return AZOAResult<bool>.FailureWithCode(IdentomatHttpTransport.DependencyMessage, AzoaErrorCodes.DependencyUnavailable);
    }
}
