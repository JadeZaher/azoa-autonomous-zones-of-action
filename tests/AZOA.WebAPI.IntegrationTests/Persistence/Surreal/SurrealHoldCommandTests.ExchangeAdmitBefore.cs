using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Reserve;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>
/// Admit-Before through the exchange controller, the real manager and the real Surreal store; only the node clock, the chain
/// and the gates outside the exchange are faked. See Services/Reserve/AGENTS.md §admission-deadline.
/// </summary>
public sealed partial class SurrealHoldCommandTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnexpiredOrAbsentAdmitBeforeAdmitsTheExchangeAsToday(bool withDeadline)
    {
        var clock = new SteppedClock(DateTimeOffset.UtcNow);
        var flow = await FlowAsync(clock: clock, governance: SlowGovernance(clock, TimeSpan.FromHours(1)));
        var header = withDeadline ? Rfc3339(clock.GetUtcNow().AddHours(2)) : null;

        var response = await Controller(flow).Exchange(flow.Participant, flow.Request(), "deadline-1", header, CancellationToken.None);

        var accepted = Answer(response, StatusCodes.Status202Accepted);
        accepted.Code.Should().Be(ConsumerSafeErrorCodes.SettlementPending, accepted.Message);
        (await Sources()).Should().ContainSingle().Which.Id.Should().Be(accepted.Result!.SourceId);
        (await Counters()).Held.Should().Be(400, "admission is followed by the commitment exactly as without a deadline");
        Answer(await Controller(flow).GetReceipt("deadline-1", CancellationToken.None), StatusCodes.Status200OK)
            .Result!.SourceId.Should().Be(accepted.Result.SourceId);
    }

    [Fact]
    public async Task AStuckRequestThatReachesAdmissionPastItsDeadlineAdmitsNothingAndItsReceiptStaysAbsent()
    {
        var clock = new SteppedClock(DateTimeOffset.UtcNow);
        var deadline = clock.GetUtcNow().AddMinutes(10);
        // The governance read hangs for an hour of node time: the request is stuck before it reaches admission.
        var flow = await FlowAsync(clock: clock, governance: SlowGovernance(clock, TimeSpan.FromHours(1)));

        var response = await Controller(flow).Exchange(flow.Participant, flow.Request(), "deadline-1", Rfc3339(deadline),
            CancellationToken.None);

        var refused = Answer(response, StatusCodes.Status409Conflict);
        JsonSerializer.Serialize(refused, WireJson).Should().Be(
            """{"isError":true,"message":"This request arrived after its deadline and nothing was admitted.","result":null,"code":"ADMISSION_DEADLINE_PASSED","detail":null}""");
        (await Sources()).Should().BeEmpty();
        (await Counters()).Held.Should().Be(0, "nothing was committed against the reserve");
        flow.Chain.Prepared.Should().Be(0);
        Answer(await Controller(flow).GetReceipt("deadline-1", CancellationToken.None), StatusCodes.Status404NotFound)
            .Code.Should().Be(AzoaErrorCodes.NotFound, "the later receipt read is the typed absence the consumer may treat as proof");
    }

    [Fact]
    public async Task ADeadlineOnlyTheDatabaseClockHasPassedIsStillEnforcedInsideTheAdmittingTransaction()
    {
        // The node clock lags the deadline, so only the time::now() condition inside the admitting write can refuse.
        var deadline = DateTimeOffset.UtcNow.AddDays(-1);
        var flow = await FlowAsync(clock: new SteppedClock(deadline.AddHours(-1)));

        var response = await Controller(flow).Exchange(flow.Participant, flow.Request(), "deadline-1", Rfc3339(deadline),
            CancellationToken.None);

        Answer(response, StatusCodes.Status409Conflict).Code.Should().Be(ConsumerSafeErrorCodes.AdmissionDeadlinePassed);
        (await Sources()).Should().BeEmpty();
        (await Counters()).Held.Should().Be(0);
    }

    [Fact]
    public async Task AReplayOfAnAdmittedKeyAnswersItsOriginalReceiptWhateverItsDeadline()
    {
        var clock = new SteppedClock(DateTimeOffset.UtcNow);
        var flow = await FlowAsync(clock: clock);
        var first = Answer(await Controller(flow).Exchange(flow.Participant, flow.Request(), "deadline-1",
            Rfc3339(clock.GetUtcNow().AddHours(2)), CancellationToken.None), StatusCodes.Status202Accepted);
        clock.Advance(TimeSpan.FromDays(3));

        var replay = await Controller(flow).Exchange(flow.Participant, flow.Request(), "deadline-1",
            Rfc3339(DateTimeOffset.UtcNow.AddDays(-1)), CancellationToken.None);

        var answered = replay.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        answered.StatusCode.Should().BeOneOf(StatusCodes.Status200OK, StatusCodes.Status202Accepted);
        answered.Value.Should().BeOfType<AZOAResult<ExchangeReceipt>>().Which.Result!.SourceId.Should().Be(first.Result!.SourceId);
        (await Sources()).Should().ContainSingle();
        flow.Chain.Prepared.Should().Be(1, "the replay resumed the original source; nothing was admitted or signed again");
    }

    [Fact]
    public async Task TheStoreAnswersAnIdenticalPriorSourceBeforeItConsultsTheDeadline()
    {
        var clock = new SteppedClock(DateTimeOffset.UtcNow);
        var store = new SurrealHoldCommandStore(_executor, new ReserveOptions(), clock);
        var (intent, authority) = await ExchangeAsync("deadline-1");
        var admitted = (await store.AdmitExchangeSourceAsync(intent, authority, clock.GetUtcNow().AddHours(2))).Source!;
        clock.Advance(TimeSpan.FromDays(3));
        var (late, lateAuthority) = await ExchangeAsync("deadline-2");

        var replay = await store.AdmitExchangeSourceAsync(intent, authority, DateTimeOffset.UtcNow.AddDays(-1));
        var refused = await store.AdmitExchangeSourceAsync(late, lateAuthority, DateTimeOffset.UtcNow.AddDays(-1));

        replay.DeadlinePassed.Should().BeFalse("a same-key race winner is a replay, not a late admission");
        replay.Source!.Id.Should().Be(admitted.Id);
        refused.Should().Be(ExchangeAdmission.Late);
        (await Sources()).Should().ContainSingle().Which.Id.Should().Be(admitted.Id);
    }

    private ExchangeController Controller(ExchangeFlow flow)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, _actor.ToString()), new Claim("UserId", _actor.ToString()),
            new Claim("AuthMethod", "ApiKey"), new Claim("ApiKeyId", flow.ApiKeyId.ToString()),
            new Claim("scope", AzoaScopes.SwapSign),
        ], "Test"));
        var consent = InterfaceFake<ITenantConsentGate>.Create(new()
        {
            ["EnsureAllowedAsync"] = (_, _) => Task.FromResult(AZOAResult<bool>.Success(true)),
        });
        return new ExchangeController(flow.Manager, consent, flow.Signing)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
    }

    private static AZOAResult<ExchangeReceipt> Answer(ActionResult<AZOAResult<ExchangeReceipt>> response, int status)
    {
        var result = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        var value = result.Value.Should().BeOfType<AZOAResult<ExchangeReceipt>>().Subject;
        result.StatusCode.Should().Be(status, value.Message);
        return value;
    }

    private async Task<IReadOnlyList<ExchangeHoldSource>> Sources()
        => await _executor.QueryAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.From());

    /// <summary>The shape a JavaScript or .NET consumer sends: <c>toISOString()</c>, millisecond precision, <c>Z</c>.</summary>
    private static string Rfc3339(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Node governance that allows the exchange but takes <paramref name="stall"/> of node time to answer.</summary>
    private static INodeGovernanceGuard SlowGovernance(SteppedClock clock, TimeSpan stall)
        => InterfaceFake<INodeGovernanceGuard>.Create(new()
        {
            ["EnsureAllowedAsync"] = (_, _) =>
            {
                clock.Advance(stall);
                return Task.FromResult(NodeGovernanceDecision.Allowed);
            },
        });

    /// <summary>A node clock the test moves by hand.</summary>
    private sealed class SteppedClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
