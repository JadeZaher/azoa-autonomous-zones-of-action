using System.Net;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>H-1/L-4: concurrent second-factor failures all count, lock the admin once, and a correct code cannot clear the lock.</summary>
public sealed class NodeAdminLockoutFlowTests(NodeAdminWebApplicationFactory factory)
    : NodeAdminHttpTestBase(factory), IClassFixture<NodeAdminWebApplicationFactory>
{
    [Fact]
    public async Task Ten_concurrent_bad_codes_lock_the_admin_and_a_correct_code_does_not_unlock_it()
    {
        var root = await RootLoginAsync();
        var (id, token) = await CreateAdminAsync(root, "lena", "auditor");
        var lena = await EnrollAsync(token);
        (await SendAsync(HttpMethod.Post, $"/admins/{id}/activate", new { fingerprint = lena.Key.Fingerprint }, root)).Status.Should().Be(HttpStatusCode.OK);

        var wrong = lena.CodeAt(Totp.StepAt(DateTimeOffset.UtcNow) + 5);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => AdminLoginAsync(lena.Key, wrong)));
        attempts.Should().OnlyContain(attempt => attempt.Code == "AUTH_FAILED");

        var correct = await AdminLoginAsync(lena.Key, lena.CurrentCode());
        correct.Code.Should().Be("ADMIN_LOCKED", "five failures must have landed despite the race");
        correct.Status.Should().Be((HttpStatusCode)423);
        (await AdminLoginAsync(lena.Key, lena.CurrentCode())).Code.Should().Be("ADMIN_LOCKED", "the lock persists");

        var locks = (await AuditRowsAsync(root)).Where(row => row.GetProperty("action").GetString() == "auth.lock").ToArray();
        locks.Should().ContainSingle("only the fifth counted failure locks; later racers see the lock and stop counting");
        locks[0].GetProperty("detail").GetString().Should().Contain("\"lockCount\":1").And.Contain("\"lockedForSeconds\":900");
    }
}
