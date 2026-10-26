using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Services.Admin;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>Operator authority exercised by a named admin is re-proven inside the economic transaction: the signing fence locks the
/// live session and admin revisions exactly as it locks the legacy operator's credential revisions; root is refused outright
/// (Services/NodeAdmin/AGENTS.md §operator-actor).</summary>
public sealed class NodeAdminOperatorFenceTests(NodeAdminOperatorRoutesFactory factory)
    : NodeAdminHttpTestBase(factory), IClassFixture<NodeAdminOperatorRoutesFactory>
{
    [Fact]
    public async Task Admin_operator_witness_commits_while_the_session_lives_and_aborts_once_it_is_revoked()
    {
        var root = await RootLoginAsync();
        var admin = await CreateActiveAdminAsync(root, root, "fence-admin", "node-owner");

        var witness = await CaptureAsync(admin.Token);
        witness.ActorUserId.Should().Be(NodeOperatorIdentity.UserId, "a node admin acts as the operator subject");
        witness.TokenUse.Should().Be(NodeAdminClaims.TokenUseNodeAdmin);
        witness.NodeAdmin!.Principal.Should().Be("admin:" + admin.Id);
        OperatorActor.IsOperatorCredential(witness).Should().BeTrue();

        (await RunFenceAsync(witness)).Should().BeNull("a live session with current revisions satisfies the fence");

        (await SendAsync(HttpMethod.Post, "/auth/revoke", token: admin.Token)).Status.Should().Be(HttpStatusCode.OK);
        (await RunFenceAsync(witness)).Should().Contain(SigningAuthorityFence.FailureMarker, "a revoked session aborts the transaction");
    }

    [Fact]
    public async Task Root_never_yields_an_operator_witness_and_a_forged_root_witness_is_refused_by_the_fence()
    {
        var root = await RootLoginAsync();
        var admin = await CreateActiveAdminAsync(root, root, "fence-shape", "node-owner");

        // L-4: root holds no operator grant (M3), so the capture refuses it rather than relying on the route map alone.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(root).Claims, "Bearer"));
        using (var scope = Factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<SigningAdmissionAuthorityFactory>().CaptureCredentialAsync(principal))
                .IsError.Should().BeTrue("a root session never carries operator signing authority");

        // A root-shaped witness (e.g. hand-built or replayed from an older build) is refused by every operator-credential check.
        var adminWitness = await CaptureAsync(admin.Token);
        var rootWitness = adminWitness with
        {
            NodeAdmin = new AZOA.WebAPI.Models.Signing.NodeAdminAuthorityWitness("root:" + Factory.RootKey.Fingerprint,
                adminWitness.NodeAdmin!.SessionRecordId, null, null, 1, 0),
        };
        OperatorActor.IsOperatorCredential(rootWitness).Should().BeFalse();
        SigningAuthorityFence.ForRunAdmission(rootWitness, "fence_test").Should().BeNull("the fence refuses a root witness structurally");
        (await RunFenceAsync(adminWitness)).Should().BeNull("the same evidence under the admin principal still commits");
    }

    private async Task<AZOA.WebAPI.Models.Signing.SigningCredentialWitness> CaptureAsync(string token)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, "Bearer"));
        using var scope = Factory.Services.CreateScope();
        var captured = await scope.ServiceProvider.GetRequiredService<SigningAdmissionAuthorityFactory>().CaptureCredentialAsync(principal);
        captured.IsError.Should().BeFalse(captured.Message);
        return captured.Result!;
    }

    /// <summary>Runs the fence in its own transaction; null on commit, otherwise the abort message.</summary>
    private async Task<string?> RunFenceAsync(AZOA.WebAPI.Models.Signing.SigningCredentialWitness witness)
    {
        var fence = SigningAuthorityFence.ForRunAdmission(witness, "fence_test");
        fence.Should().NotBeNull("the witness is structurally valid");
        using var scope = Factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISurrealExecutor>();
        var response = await executor.ExecuteAsync(SurrealQuery.Combine([SurrealQuery.Of("BEGIN"), .. fence!, SurrealQuery.Of("COMMIT")]), CancellationToken.None);
        try
        {
            response.EnsureAllOk();
            return null;
        }
        catch (SurrealStatementException exception)
        {
            return exception.Message;
        }
    }
}
