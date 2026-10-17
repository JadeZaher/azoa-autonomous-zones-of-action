// SPDX-License-Identifier: UNLICENSED

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.HostedSessions;

namespace AZOA.WebAPI.Controllers;

/// <summary>
/// Inbound provider callback surface.
///
/// <para>The callback authenticates with a shared secret carried IN THE BODY, which
/// makes this endpoint both unauthenticated to ASP.NET and a brute-force target, so
/// it gets its OWN rate-limit policy rather than sharing the global one: a
/// per-user or per-key partition is meaningless here because there is no
/// principal.</para>
///
/// <para>The response is a fixed, contentless acknowledgement. It never echoes the
/// secret, never reports whether the secret was wrong versus the session unknown,
/// and never carries the resulting verification outcome — each of those would turn
/// the endpoint into an oracle for an unauthenticated caller.</para>
/// </summary>
[ApiController]
[Route("api/kyc/callback")]
[AllowAnonymous]
[EnableRateLimiting(KycCallbackController.RateLimitPolicy)]
public sealed class KycCallbackController : ControllerBase
{
    /// <summary>Dedicated rate-limit policy name; registered in <c>Program.cs</c>.</summary>
    public const string RateLimitPolicy = "kyc-callback";

    /// <summary>Body cap; the documented payload is a handful of short fields.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    private readonly IHostedSessionFlow _flow;
    private readonly IdentomatKycProviderService _identomat;
    private readonly ILogger<KycCallbackController> _logger;
    private readonly AZOA.WebAPI.Interfaces.Managers.IKybService? _kyb;

    public KycCallbackController(
        IHostedSessionFlow flow,
        IdentomatKycProviderService identomat,
        ILogger<KycCallbackController> logger,
        AZOA.WebAPI.Interfaces.Managers.IKybService? kyb = null)
    {
        _flow = flow;
        _identomat = identomat;
        _logger = logger;
        _kyb = kyb;
    }

    /// <summary>
    /// Accepts an Identomat session-change callback. The body is an untrusted
    /// TRIGGER: its own <c>result</c> is discarded and the authoritative
    /// <c>/result/</c> endpoint is re-fetched server-side.
    /// </summary>
    [HttpPost("identomat")]
    [RequestSizeLimit(MaxBodyBytes)]
    [Consumes("application/json")]
    public async Task<IActionResult> Identomat(CancellationToken ct)
    {
        if (!_identomat.GetHostedCapabilities().Available && _kyb?.GetCapabilities().Available != true)
        {
            // 202, not 503. A distinct status code answers "are this node's Identomat
            // credentials installed?" for any anonymous caller — the same class of
            // oracle the opaque failure message exists to close, just spelled in the
            // status line. The server-side log is where unavailability is reported.
            _logger.LogWarning("An Identomat callback arrived while the adapter is not configured.");
            return Accepted(Acknowledgement());
        }

        var body = await ReadBoundedBodyAsync(ct);
        if (body is null)
            return Accepted(Acknowledgement());

        if (_identomat.GetHostedCapabilities().Available)
            _ = await _flow.HandleCallbackAsync(_identomat, body, ct);
        if (_kyb?.GetCapabilities().Available == true)
            await _kyb.HandleCallbackAsync(body, ct);

        // ONE status code, ONE body, on EVERY path: unconfigured adapter, unreadable
        // body, rejected secret, unknown session, parked verdict, applied verdict. An
        // unauthenticated caller learns nothing from this endpoint except that it
        // exists. The server-side log records which path it actually was.
        return Accepted(Acknowledgement());
    }

    /// <summary>Fixed acknowledgement; identical on every path.</summary>
    private static AZOAResult<string> Acknowledgement()
        => AZOAResult<string>.Success("accepted", "Accepted.");

    /// <summary>
    /// Reads at most <see cref="MaxBodyBytes"/> so an oversized body cannot be
    /// buffered into memory before the size limit filter runs.
    /// </summary>
    private async Task<string?> ReadBoundedBodyAsync(CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(
                Request.Body,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            var buffer = new char[MaxBodyBytes];
            var read = await reader.ReadBlockAsync(buffer, ct);
            if (read == 0 || read >= MaxBodyBytes)
                return null;
            return new string(buffer, 0, read);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "An inbound KYC callback body could not be read.");
            return null;
        }
    }
}
