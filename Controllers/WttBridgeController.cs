using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

/// <summary>Authenticated Wormhole Token Transfer journeys; private wire never leaves the server.</summary>
[ApiController]
[Route("api/bridge/wtt")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WttBridgeController(WttLifecycleService lifecycle, SigningAdmissionAuthorityFactory authority) : ControllerBase
{
    [HttpPost("quotes")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Quote([FromBody] WttQuoteRequest request, CancellationToken ct)
        => RunAsync(credential => lifecycle.QuoteAsync(credential, request, ct), ct, read: true);

    [HttpPost("operations")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Start([FromBody] WttStartRequest request, [FromHeader(Name = "Idempotency-Key")] string? key,
        CancellationToken ct)
        => RunAsync(credential => lifecycle.StartAsync(credential, request, key, ct), ct);

    [HttpGet("operations")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int limit, CancellationToken ct)
        => RunAsync(credential => lifecycle.ListAsync(credential, cursor, limit == 0 ? 20 : limit, ct), ct, read: true);

    [HttpGet("operations/by-request-key")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Lookup([FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
        => RunAsync(credential => lifecycle.LookupAsync(key, credential, ct), ct, read: true);

    [HttpPost("operations/cancel")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Cancel([FromBody] WttCancelRequest request, [FromHeader(Name = "Idempotency-Key")] string? key,
        CancellationToken ct)
        => RunAsync(credential => lifecycle.CancelAsync(credential, request, key, ct), ct);

    [HttpGet("operations/{id}")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Get(string id, CancellationToken ct)
        => RunAsync(credential => lifecycle.GetAsync(id, credential, ct), ct, read: true);

    [HttpPost("operations/{id}/continue")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Continue(string id, [FromBody] WttContinueCommand command, CancellationToken ct)
        => RunAsync(credential => lifecycle.ContinueAsync(id, command, credential, ct), ct);

    [HttpPost("operations/{id}/submit")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Submit(string id, [FromBody] WttStageCommand command, CancellationToken ct)
        => RunAsync(credential => lifecycle.SubmitAsync(id, command, credential, ct), ct);

    [HttpPost("operations/{id}/participant")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Participant(string id, [FromBody] WttStageCommand command, CancellationToken ct)
        => RunAsync(credential => lifecycle.ExposeParticipantAsync(id, command, credential, ct), ct, read: true);

    [HttpPost("operations/{id}/participant-signature")]
    [EnableRateLimiting("financial")]
    [RequestSizeLimit(1100000)]
    public Task<IActionResult> ParticipantSignature(string id, [FromBody] WttParticipantSignatureCommand command, CancellationToken ct)
        => RunAsync(credential => lifecycle.AcceptParticipantAsync(id, command, credential, ct), ct);

    [HttpPost("operations/{id}/refresh")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Refresh(string id, CancellationToken ct)
        => RunAsync(credential => lifecycle.RefreshAsync(id, credential, ct), ct, read: true);

    [HttpGet("operations/{id}/native-output/quote")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> NativeOutputQuote(string id, CancellationToken ct)
        => RunAsync(credential => lifecycle.QuoteNativeOutputAsync(id, credential, ct), ct, read: true);

    [HttpPost("operations/{id}/native-output")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> NativeOutput(string id, [FromBody] WttNativeOutputRequest request, CancellationToken ct)
        => RunAsync(credential => lifecycle.StartNativeOutputAsync(id, request, credential, ct), ct);

    [HttpGet("operations/{id}/cleanup/{stageId}/quote")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> CleanupQuote(string id, string stageId, CancellationToken ct)
        => RunAsync(credential => lifecycle.QuoteCleanupAsync(id, stageId, credential, ct), ct, read: true);

    [HttpPost("operations/{id}/cleanup")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Cleanup(string id, [FromBody] WttCleanupRequest request, CancellationToken ct)
        => RunAsync(credential => lifecycle.StartCleanupAsync(id, request, credential, ct), ct);

    private async Task<IActionResult> RunAsync<T>(Func<SigningCredentialWitness, Task<AZOAResult<T>>> work, CancellationToken ct,
        bool read = false)
    {
        var credential = await authority.CaptureCredentialAsync(User, ct);
        if (credential.IsError || credential.Result is null)
            return StatusCode(StatusCodes.Status403Forbidden, AZOAResult<object>.FailureWithCode(credential.Message, AzoaErrorCodes.Forbidden));
        var result = await work(credential.Result);
        if (result.IsError)
            return StatusCode(result.Code switch
            {
                AzoaErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
                AzoaErrorCodes.Conflict => StatusCodes.Status409Conflict,
                AzoaErrorCodes.NotFound => StatusCodes.Status404NotFound,
                AzoaErrorCodes.DependencyUnavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest,
            }, result);
        return read || result.Result is WttOperationView { State: "Settled" or "Cancelled" } ? Ok(result) : Accepted(result);
    }
}
