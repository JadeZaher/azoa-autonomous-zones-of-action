using System.Net;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>Security spot-check follow-ups: a challenge can never outlive its issuance window, and consumed markers survive their
/// expiry by a clock-skew margin before the sweep removes them (Services/NodeAdmin/AGENTS.md §auth).</summary>
public sealed class NodeAdminChallengeHardeningTests(NodeAdminWebApplicationFactory factory)
    : NodeAdminHttpTestBase(factory), IClassFixture<NodeAdminWebApplicationFactory>
{
    [Fact]
    public async Task A_correctly_signed_challenge_whose_expiry_exceeds_the_lifetime_is_refused()
    {
        var codec = Factory.Services.GetRequiredService<NodeAdminChallengeCodec>();
        var payload = new NodeAdminChallengePayload(NodeAdminChallengeCodec.CurrentVersion, Guid.NewGuid().ToString("N"), "root_login",
            Factory.RootKey.Fingerprint, NodeAdminWebApplicationFactory.Audience, NodeAdminCrypto.RandomToken(),
            NodeAdminCrypto.FormatTimestamp(DateTimeOffset.UtcNow.AddMinutes(10)));
        var challengeId = codec.Encode(payload);
        var message = NodeAdminCrypto.LoginMessage(payload.Purpose, payload.Audience, challengeId, payload.Nonce, payload.ExpiresAt);

        var session = await SendAsync(HttpMethod.Post, "/auth/session",
            new { challengeId, publicKey = Factory.RootKey.PublicKey, signature = Factory.RootKey.Sign(message) });

        session.Status.Should().Be(HttpStatusCode.Unauthorized, "exp − now may not exceed the 60 s lifetime plus 30 s skew");
        session.Code.Should().Be(NodeAdminErrorCodes.AuthFailed);
    }

    [Fact]
    public async Task Consumed_markers_outlive_their_expiry_by_the_skew_margin_before_the_sweep_removes_them()
    {
        var challenge = await RequestChallengeAsync(Factory.RootKey, "root_login");
        (await SignChallengeAsync(challenge, Factory.RootKey, null, null, "root_login")).Status.Should().Be(HttpStatusCode.OK);
        Factory.Services.GetRequiredService<NodeAdminChallengeCodec>()
            .TryDecode(challenge.GetProperty("challengeId").GetString(), out var payload).Should().BeTrue();
        var expiresAt = DateTimeOffset.Parse(payload.ExpiresAt, System.Globalization.CultureInfo.InvariantCulture);

        using var scope = Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SurrealNodeAdminStore>();
        var executor = scope.ServiceProvider.GetRequiredService<ISurrealExecutor>();

        await store.SweepExpiredAsync(expiresAt.AddMinutes(4), CancellationToken.None);
        (await executor.QuerySingleAsync<NodeAdminChallenge>(SurrealQuery<NodeAdminChallenge>.Key(payload.Id), CancellationToken.None))
            .Should().NotBeNull("4 minutes past expiry is inside the 5-minute skew margin");

        await store.SweepExpiredAsync(expiresAt.AddMinutes(6), CancellationToken.None);
        (await executor.QuerySingleAsync<NodeAdminChallenge>(SurrealQuery<NodeAdminChallenge>.Key(payload.Id), CancellationToken.None))
            .Should().BeNull("past the margin the marker is swept");
    }
}
