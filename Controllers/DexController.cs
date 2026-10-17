using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Dex;
using AZOA.WebAPI.Services.Signing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Route("api/dex")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DexController(DexQuoteService quotes, SwapOperationService swaps,
    SigningAdmissionAuthorityFactory authority) : ControllerBase
{
    [HttpPost("quotes")]
    [EnableRateLimiting("financial")]
    public async Task<IActionResult> Quote([FromBody] DexQuoteRequest request, CancellationToken ct)
    {
        var credential = await CredentialAsync(ct);
        if (credential.Result is null) return Forbidden(credential.Message);
        var result = await quotes.QuoteAsync(credential.Result, request, ct);
        return result.IsError ? Failure(result) : Ok(result);
    }

    [HttpGet("quotes/{id}")]
    public async Task<IActionResult> GetQuote(string id, CancellationToken ct)
    {
        var credential = await CredentialAsync(ct);
        if (credential.Result is null) return Forbidden(credential.Message);
        var quote = await quotes.GetAsync(id, credential.Result, ct);
        return quote is null ? NotFound() : Ok(AZOAResult<DexQuoteView>.Success(quote));
    }

    [HttpPost("operations")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Start([FromBody] SwapAdmissionRequest request, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.StartAsync(credential, request, ct: ct), ct);

    [HttpGet("operations/{id}")]
    public Task<IActionResult> Get(string id, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.GetAsync(id, credential, ct), ct, read: true);

    [HttpPost("operations/{id}/prepare")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Prepare(string id, [FromBody] SwapVersionCommand command, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.PrepareAsync(id, command.ExpectedVersion, credential, ct: ct), ct);

    [HttpPost("operations/{id}/sign")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Sign(string id, [FromBody] SwapStageCommand command, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.SignWithCustodyAsync(id, command, credential, ct: ct), ct);

    [HttpPost("operations/{id}/external-signing")]
    [EnableRateLimiting("financial")]
    public async Task<IActionResult> Expose(string id, [FromBody] SwapExposureCommand command, CancellationToken ct)
    {
        var credential = await CredentialAsync(ct);
        if (credential.Result is null) return Forbidden(credential.Message);
        var result = await swaps.ExposeAsync(id, command, credential.Result, ct: ct);
        return result.IsError ? Failure(result) : Ok(result);
    }

    [HttpPost("operations/{id}/external-signature")]
    [EnableRateLimiting("financial")]
    [RequestSizeLimit(1100000)]
    public Task<IActionResult> AcceptBrowser(string id, [FromBody] SwapBrowserStageCommand command, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.AcceptBrowserAsync(id, command, credential, ct: ct), ct);

    [HttpPost("operations/{id}/withdraw-submission")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> WithdrawSubmission(string id, [FromBody] SwapStageCommand command, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.WithdrawSubmissionAsync(id, command, credential, ct), ct);

    [HttpPost("operations/{id}/submit")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Submit(string id, [FromBody] SwapStageCommand command, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.SubmitAsync(id, command, credential, ct: ct), ct);

    [HttpPost("operations/{id}/refresh")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Refresh(string id, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.RefreshAsync(id, credential, ct), ct);

    [HttpPost("operations/{id}/cancel")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Cancel(string id, [FromBody] SwapVersionCommand command, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.CancelUnsentAsync(id, command.ExpectedVersion, credential, ct), ct);

    private Task<AZOAResult<SigningCredentialWitness>> CredentialAsync(CancellationToken ct)
        => authority.CaptureCredentialAsync(User, ct);

    [HttpPost("operations/{id}/cleanup")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> Cleanup(string id, [FromBody] SwapCleanupRequest request, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.BeginCleanupAsync(id, request, credential, ct), ct);

    [HttpPost("operations/{id}/cleanup/prepare")]
    [EnableRateLimiting("financial")]
    public Task<IActionResult> PrepareCleanup(string id, [FromBody] SwapCleanupStageCommand command, CancellationToken ct)
        => WithCredentialAsync(credential => swaps.PrepareCleanupAsync(id, command, credential, ct), ct);

    private async Task<IActionResult> WithCredentialAsync(Func<SigningCredentialWitness, Task<AZOAResult<SwapOperationView>>> work,
        CancellationToken ct, bool read = false)
    {
        var credential = await CredentialAsync(ct);
        if (credential.Result is null) return Forbidden(credential.Message);
        var result = await work(credential.Result);
        if (result.IsError) return Failure(result);
        return read || result.Result?.State is "Completed" or "Cancelled" or "CleanupCompleted" ? Ok(result) : Accepted(result);
    }

    private ObjectResult Failure<T>(AZOAResult<T> result)
        => StatusCode(result.Code == "Forbidden" ? StatusCodes.Status403Forbidden : result.Code == "Conflict"
            ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest, result);
    private ObjectResult Forbidden(string message)
        => StatusCode(StatusCodes.Status403Forbidden, AZOAResult<object>.FailureWithCode(message, "Forbidden"));
}
