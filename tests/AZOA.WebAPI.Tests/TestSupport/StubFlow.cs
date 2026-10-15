// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.HostedSessions;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// Recording stub for <see cref="IHostedSessionFlow"/>. Call counts matter as much
/// as return values here: several tests assert that a refusal happens BEFORE the
/// flow is entered, which is the difference between failing closed and failing
/// after an external side effect.
/// </summary>
public sealed class StubFlow : IHostedSessionFlow
{
    private readonly AZOAResult<HostedSessionStartResult> _beginResult;
    private readonly AZOAResult<HostedCallbackResult>? _callbackResult;

    public StubFlow(
        AZOAResult<HostedSessionStartResult> beginResult,
        AZOAResult<HostedCallbackResult>? callbackResult = null)
    {
        _beginResult = beginResult;
        _callbackResult = callbackResult;
    }

    public int BeginCallCount { get; private set; }
    public int CallbackCallCount { get; private set; }
    public HostedSessionRequest? LastRequest { get; private set; }

    public Task<AZOAResult<HostedSessionStartResult>> BeginAsync(
        IHostedSessionProvider provider,
        HostedSessionRequest request,
        CancellationToken ct = default)
    {
        BeginCallCount++;
        LastRequest = request;
        return Task.FromResult(_beginResult);
    }

    public Task<AZOAResult<HostedCallbackResult>> HandleCallbackAsync(
        IHostedSessionProvider provider,
        string rawBody,
        CancellationToken ct = default)
    {
        CallbackCallCount++;
        return Task.FromResult(
            _callbackResult
            ?? AZOAResult<HostedCallbackResult>.Failure("no callback result configured"));
    }
}
