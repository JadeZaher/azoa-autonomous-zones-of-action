// SPDX-License-Identifier: UNLICENSED

using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Commercial;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Controllers;

[ApiController]
[Authorize]
[Route("api/commercial")]
public sealed class CommercialPrimitivesController : ControllerBase
{
    private const int MaxOptionalBodyBytes = 16384;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CommercialPrimitivesService _service;

    public CommercialPrimitivesController(CommercialPrimitivesService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    [HttpPost("access-links/mint")]
    public ActionResult<AZOAResult<SignedAccessLink>> MintAccessLink([FromBody] MintAccessLinkRequest request)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(_service.MintAccessLink(actorId, request));
    }

    [HttpGet("access-links/{linkId}/verify")]
    [AllowAnonymous]
    public ActionResult<AZOAResult<AccessLinkVerificationResult>> VerifyAccessLink(
        string linkId,
        [FromQuery] string? scope = null,
        [FromQuery] string? sub = null,
        [FromQuery] long exp = 0,
        [FromQuery] string? sig = null)
    {
        var result = _service.VerifyAccessLink(
            linkId,
            scope ?? string.Empty,
            sub ?? string.Empty,
            exp,
            sig ?? string.Empty);

        if (!result.IsError)
            return Ok(result);

        return result.Code switch
        {
            "INVALID_SIGNATURE" or "LINK_EXPIRED" => StatusCode(StatusCodes.Status401Unauthorized, result),
            _ => BadRequest(result),
        };
    }

    [HttpGet("identomat/status")]
    public async Task<ActionResult<AZOAResult<IdentomatSessionStatus>>> GetIdentomatStatus(CancellationToken ct = default)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(await _service.GetIdentomatStatusAsync(actorId, ct));
    }

    [HttpPost("identomat/session")]
    public async Task<ActionResult<AZOAResult<IdentomatSessionStatus>>> StartIdentomatSession(CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        var (ok, request, errorMessage) = await TryReadOptionalJsonBodyAsync<IdentomatSessionRequest>(ct);
        if (!ok)
            return BadRequest(AZOAResult<IdentomatSessionStatus>.FailureWithCode(
                errorMessage!, AzoaErrorCodes.InvalidRequest));

        return Translate(await _service.StartIdentomatSessionAsync(actorId, request, ct));
    }

    [HttpGet("invoices")]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<CommercialInvoice>>>> ListInvoices(CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(await _service.ListInvoicesAsync(actorId, ct));
    }

    [HttpPost("invoices")]
    public async Task<ActionResult<AZOAResult<CommercialInvoice>>> CreateInvoice(
        [FromBody] CreateCommercialInvoiceRequest request,
        CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(await _service.CreateInvoiceAsync(actorId, request, ct));
    }

    [HttpPost("invoices/{invoiceId}/pay")]
    public async Task<ActionResult<AZOAResult<CommercialInvoice>>> PayInvoice(
        string invoiceId,
        CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        var (ok, request, errorMessage) = await TryReadOptionalJsonBodyAsync<PayCommercialInvoiceRequest>(ct);
        if (!ok)
            return BadRequest(AZOAResult<CommercialInvoice>.FailureWithCode(
                errorMessage!, AzoaErrorCodes.InvalidRequest));

        return Translate(await _service.PayInvoiceAsync(actorId, invoiceId, request, ct));
    }

    [HttpPost("invoices/{invoiceId}/void")]
    public async Task<ActionResult<AZOAResult<CommercialInvoice>>> VoidInvoice(
        string invoiceId,
        CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        var (ok, request, errorMessage) = await TryReadOptionalJsonBodyAsync<VoidCommercialInvoiceRequest>(ct);
        if (!ok)
            return BadRequest(AZOAResult<CommercialInvoice>.FailureWithCode(
                errorMessage!, AzoaErrorCodes.InvalidRequest));

        return Translate(await _service.VoidInvoiceAsync(actorId, invoiceId, request, ct));
    }

    [HttpGet("payment-requests")]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<PaymentRequestRecord>>>> ListPaymentRequests(CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(await _service.ListPaymentRequestsAsync(actorId, ct));
    }

    [HttpPost("payment-requests")]
    public async Task<ActionResult<AZOAResult<PaymentRequestRecord>>> CreatePaymentRequest(
        [FromBody] CreatePaymentRequestInput request,
        CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(await _service.CreatePaymentRequestAsync(actorId, request, ct));
    }

    [HttpGet("ownership-receipts")]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<OwnershipReceiptDocument>>>> ListOwnershipReceipts(CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(await _service.ListOwnershipReceiptsAsync(actorId, ct));
    }

    [HttpPost("ownership-receipts/issue")]
    public async Task<ActionResult<AZOAResult<OwnershipReceiptDocument>>> IssueOwnershipReceipt(
        [FromBody] IssueOwnershipReceiptRequest request,
        CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(await _service.IssueOwnershipReceiptAsync(actorId, request, ct));
    }

    [HttpGet("account-documents")]
    public async Task<ActionResult<AZOAResult<IReadOnlyList<AccountDocumentReport>>>> ListAccountDocuments(CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(await _service.ListAccountDocumentsAsync(actorId, ct));
    }

    [HttpPost("account-documents/generate")]
    public async Task<ActionResult<AZOAResult<AccountDocumentReport>>> GenerateAccountDocument(
        [FromBody] GenerateAccountDocumentRequest request,
        CancellationToken ct)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(await _service.GenerateAccountDocumentAsync(actorId, request, ct));
    }

    [HttpGet("deferrals")]
    public ActionResult<AZOAResult<IReadOnlyList<ProviderDeferralEntry>>> ListDeferrals([FromQuery] string? provider = null)
    {
        if (!TryGetAuthenticatedActorId(out var actorId))
            return Unauthorized();

        return Translate(_service.ListDeferrals(actorId, provider));
    }

    private bool TryGetAuthenticatedActorId(out Guid actorId)
        => AzoaClaims.TryGetSubjectId(User, out actorId) && actorId != Guid.Empty;

    private async Task<(bool Ok, TRequest? Value, string? ErrorMessage)> TryReadOptionalJsonBodyAsync<TRequest>(
        CancellationToken ct)
    {
        if (Request.ContentLength == 0 || Request.Body is null || !Request.Body.CanRead)
            return (true, default, null);

        if (Request.ContentLength > MaxOptionalBodyBytes)
            return (false, default, "Request body exceeds maximum allowed size.");

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            if (buffer.Length + read > MaxOptionalBodyBytes)
                return (false, default, "Request body exceeds maximum allowed size.");
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
            return (true, default, null);

        var raw = Encoding.UTF8.GetString(buffer.ToArray());
        if (string.IsNullOrWhiteSpace(raw))
            return (true, default, null);

        try
        {
            var value = JsonSerializer.Deserialize<TRequest>(raw, JsonOptions);
            return (true, value, null);
        }
        catch (JsonException)
        {
            return (false, default, "Malformed JSON request body.");
        }
    }

    private ActionResult<AZOAResult<T>> Translate<T>(AZOAResult<T> result)
    {
        if (!result.IsError)
            return Ok(result);

        return result.Code switch
        {
            AzoaErrorCodes.NotFound => NotFound(result),
            AzoaErrorCodes.Conflict => Conflict(result),
            AzoaErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result),
            AzoaErrorCodes.InvalidRequest
                or AzoaErrorCodes.PolicyUnavailable
                or AzoaErrorCodes.OperationNotAllowed => BadRequest(result),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable, result),
        };
    }
}
