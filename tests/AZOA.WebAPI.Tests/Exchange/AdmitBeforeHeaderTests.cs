// SPDX-License-Identifier: UNLICENSED
// The Admit-Before edge: what the header accepts, that a malformed one is refused before anything runs, and the wire status of
// a deadline refusal. The deadline itself is proven against the real store in the integration project
// (SurrealHoldCommandTests.ExchangeAdmitBefore.cs); see Services/Reserve/AGENTS.md §admission-deadline.

using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Exchange;

public sealed class AdmitBeforeHeaderTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("2026-10-28T12:00:00Z", "2026-10-28T12:00:00.0000000+00:00")]
    [InlineData("2026-10-28T12:00:00.123Z", "2026-10-28T12:00:00.1230000+00:00")]
    [InlineData("2026-10-28T12:00:00.1234567+00:00", "2026-10-28T12:00:00.1234567+00:00")]
    [InlineData("2026-10-28t12:00:00z", "2026-10-28T12:00:00.0000000+00:00")]
    public void A_utc_rfc3339_deadline_or_none_is_accepted(string? header, string? expected)
    {
        AdmitBeforeHeader.TryParse(header, out var deadline).Should().BeTrue();

        deadline?.ToString("O").Should().Be(expected);
        (deadline is null).Should().Be(expected is null);
    }

    [Theory]
    [InlineData("2026-10-28T12:00:00", "no offset is a local time the node would have to guess")]
    [InlineData("2026-10-28T14:00:00+02:00", "the contract names a UTC instant")]
    [InlineData("Wed, 28 Oct 2026 12:00:00 GMT", "an HTTP-date is not RFC 3339")]
    [InlineData("1793188800", "epoch seconds are not RFC 3339")]
    [InlineData("2026-13-28T12:00:00Z", "there is no thirteenth month")]
    [InlineData("2026-10-28T12:00:00.12345678Z", "more precision than the node can hold")]
    [InlineData("2026-10-28T12:00:00Z,2026-10-28T12:00:01Z", "two deadlines are no deadline")]
    public void Anything_else_is_malformed(string header, string because)
    {
        AdmitBeforeHeader.TryParse(header, out var deadline).Should().BeFalse(because);
        deadline.Should().BeNull();
    }

    [Fact]
    public async Task A_malformed_deadline_is_refused_before_the_exchange_runs()
    {
        var manager = new Mock<IReserveExchangeManager>(MockBehavior.Strict);

        var response = await Controller(manager.Object).Exchange(Guid.NewGuid(), new ExchangeRequest(), "idem-1",
            "2026-10-28T14:00:00+02:00", CancellationToken.None);

        var refusal = response.Result.Should().BeOfType<BadRequestObjectResult>().Which;
        refusal.Value.Should().BeOfType<AZOAResult<ExchangeReceipt>>().Which.Code.Should().Be(AzoaErrorCodes.InvalidRequest,
            "a deadline the node cannot read must not silently become no deadline");
    }

    [Fact]
    public async Task A_passed_deadline_is_a_409_carrying_its_own_code()
    {
        var manager = new Mock<IReserveExchangeManager>();
        manager.Setup(candidate => candidate.ExchangeAsync(It.IsAny<ExchangeRequest>(), It.IsAny<ExchangeCallerContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ExchangeReceipt>.FailureWithCode(
                "This request arrived after its deadline and nothing was admitted.", ConsumerSafeErrorCodes.AdmissionDeadlinePassed));

        var response = await Controller(manager.Object).Exchange(Guid.NewGuid(), new ExchangeRequest { PreviewOnly = true },
            "idem-2", "2026-10-28T12:00:00Z", CancellationToken.None);

        var refusal = response.Result.Should().BeOfType<ObjectResult>().Which;
        refusal.StatusCode.Should().Be(StatusCodes.Status409Conflict, "the contract fixes 409, never 400");
        refusal.Value.Should().BeOfType<AZOAResult<ExchangeReceipt>>().Which.Code.Should().Be("ADMISSION_DEADLINE_PASSED");
    }

    /// <summary>An API-key principal carrying the exchange scope, with consent already given.</summary>
    private static ExchangeController Controller(IReserveExchangeManager manager)
    {
        var userId = Guid.NewGuid();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("UserId", userId.ToString()),
            new("AuthMethod", "ApiKey"),
            new("ApiKeyId", Guid.NewGuid().ToString()),
            new("scope", ReserveExchangeManager.ExchangeSigningScope),
        };
        var gate = new Mock<ITenantConsentGate>();
        gate.Setup(candidate => candidate.EnsureAllowedAsync(It.IsAny<SigningContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<bool> { Result = true });
        return new ExchangeController(manager, gate.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }
}
