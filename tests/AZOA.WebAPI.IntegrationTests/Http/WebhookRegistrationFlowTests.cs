using System.Net;
using System.Net.Http.Json;
using AZOA.WebAPI.IntegrationTests.Http.Infrastructure;
using AZOA.WebAPI.Models;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Http;

/// <summary>Tenant webhook registration through the real controller and store. See <c>Http/AGENTS.md</c> §flows.</summary>
public sealed class WebhookRegistrationFlowTests(HttpFlowWebApplicationFactory factory) : HttpFlowTestBase(factory)
{
    private const string Route = "api/tenant/webhook-registration";

    [Fact]
    public async Task A_lost_response_is_replayed_from_the_receipt_without_redisclosing_the_secret()
    {
        using var tenant = await OnboardTenantAsync();
        var command = new WebhookRegistrationCommand(Guid.NewGuid(), Guid.NewGuid(), 0,
            "https://receiver.flow.test/azoa", IsActive: true, RotateSecret: false, "Register the flow test receiver.");

        using var created = await tenant.Client.PutAsJsonAsync(Route, command, JsonOptions);
        var original = await ReadOkAsync<WebhookRegistrationMutationResult>(created);
        original.Replayed.Should().BeFalse();
        original.NewSecret.Should().NotBeNullOrWhiteSpace("the first registration always mints a signing secret");
        original.Receipt.Version.Should().Be(1);

        // The client never saw the answer and retries the identical command.
        using var retried = await tenant.Client.PutAsJsonAsync(Route, command, JsonOptions);
        var replay = await ReadOkAsync<WebhookRegistrationMutationResult>(retried);
        replay.Replayed.Should().BeTrue();
        replay.NewSecret.Should().BeNull();
        replay.Receipt.Should().BeEquivalentTo(original.Receipt);

        // A new request still claiming version 0 lost the race and must not overwrite version 1.
        using var stale = await tenant.Client.PutAsJsonAsync(Route, command with { RequestId = Guid.NewGuid() }, JsonOptions);
        await ShouldReturnAsync(stale, HttpStatusCode.Conflict);

        using var read = await tenant.Client.GetAsync(Route);
        var current = await ReadOkAsync<WebhookRegistrationStatus>(read);
        current.Version.Should().Be(1);
        current.Url.Should().Be(command.Url);

        FrontendFixtureContract.ShouldMatchShape(await ResultElementAsync(read),
            FrontendFixtureContract.Load("webhook-registration.json", "status"));
        FrontendFixtureContract.ShouldMatchShape(await ResultElementAsync(created),
            FrontendFixtureContract.Load("webhook-registration.json", "mutationResult"));
    }

    [Fact]
    public async Task A_withdrawn_change_can_never_apply_later()
    {
        using var tenant = await OnboardTenantAsync();
        var first = new WebhookRegistrationCommand(Guid.NewGuid(), Guid.NewGuid(), 0,
            "https://receiver.flow.test/azoa", IsActive: true, RotateSecret: false, "Register the flow test receiver.");
        using (var registered = await tenant.Client.PutAsJsonAsync(Route, first, JsonOptions))
            await ReadOkAsync<WebhookRegistrationMutationResult>(registered);

        // The browser saved a rotation but never learned whether it was sent; it withdraws it.
        var rotation = first with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, RotateSecret = true, Reason = "Rotate the receiver signing secret." };
        using var withdrawn = await tenant.Client.PostAsJsonAsync(Route + "/withdraw", rotation, JsonOptions);
        var withdrawal = await ReadOkAsync<WebhookRegistrationWithdrawal>(withdrawn);
        withdrawal.AlreadyApplied.Should().BeFalse();
        withdrawal.Receipt.Should().BeNull();

        using var late = await tenant.Client.PutAsJsonAsync(Route, rotation, JsonOptions);
        await ShouldReturnAsync(late, HttpStatusCode.Conflict);
        using var read = await tenant.Client.GetAsync(Route);
        var current = await ReadOkAsync<WebhookRegistrationStatus>(read);
        current.Version.Should().Be(1);
        current.SecretRevision.Should().Be(1, "the withdrawn rotation never happened");

        FrontendFixtureContract.ShouldMatchShape(await ResultElementAsync(withdrawn),
            FrontendFixtureContract.Load("webhook-registration.json", "withdrawal"));
    }
}
