using System.Text.Json;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealDeploymentRecoveryLinkTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealDeploymentStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "deployment_operation", "native_quote", "deployment_recovery_audit", "deployment_recovery_audit_entry");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor);
    }

    public async Task DisposeAsync() { _connection?.Dispose(); await SurrealTestSchema.DropAsync(_namespace); }

    [Fact]
    public async Task WinningChildFencesDelayedParentAndCompetingChild()
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var first = await ChildAsync(parent);
        (await _store.ClaimRecoveryChildAsync(parent, first)).Should().NotBeNull();
        (await _store.ClaimRecoveryChildAsync(parent, first)).Should().NotBeNull();
        var second = await ChildAsync(parent);
        (await _store.ClaimRecoveryChildAsync(parent, second)).Should().BeNull();
        (await _store.GetAsync(second.Id)).Should().BeNull();
        parent.State = "Submitted"; parent.SubmissionAuthorized = true;
        (await _store.UpdateAsync(parent, parent.Version)).Should().BeNull();
        var current = (await _store.GetAsync(parent.Id))!;
        current.SubmissionAuthorized = true;
        (await _store.UpdateAsync(current, current.Version)).Should().BeNull();
    }

    [Fact]
    public async Task AncestryAuditResumesAcrossPagesWithImmutableVisitedEvidence()
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var head = (await _store.ClaimRecoveryChildAsync(parent, await ChildAsync(parent)))!;
        parent = (await _store.GetAsync(parent.Id))!;
        var ids = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        var link = await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<DeploymentOperation>(head.Id)
            .Set(row => row.ReplacesOperationId, ids[0]).Build());
        link.EnsureAllOk(); head = (await _store.GetAsync(head.Id))!;
        for (var index = 0; index < ids.Length; index++)
            await _store.ClaimAsync(new DeploymentOperation { Id = ids[index], ParentOperationId = parent.Id,
                ActorUserId = parent.ActorUserId, TenantId = parent.TenantId, State = "Prepared", RequestJson = "{}",
                IntentHash = "retained-intent-" + index, PreparedJson = "never-authorized-candidate",
                ReplacedByOperationId = index == 0 ? head.Id : ids[index - 1],
                ReplacesOperationId = index + 1 == ids.Length ? null : ids[index + 1] });
        var audits = new SurrealDeploymentRecoveryAuditStore(_executor);
        var audit = await audits.ClaimAsync(parent, head);
        var page = (await audits.AdvanceAsync(audit.Id, audit.Version))!;
        page.State.Should().Be("Running"); page.InspectedCount.Should().Be(25);
        page.NextOperationId.Should().Be(ids[25]);
        (await audits.AdvanceAsync(audit.Id, audit.Version)).Should().BeNull();
        var restarted = new SurrealDeploymentRecoveryAuditStore(_executor);
        var complete = (await restarted.AdvanceAsync(page.Id, page.Version))!;
        complete.State.Should().Be("Complete"); complete.InspectedCount.Should().Be(30);
        complete.NextOperationId.Should().BeNull(); complete.Digest.Should().NotBe(page.Digest);
        (await restarted.ClaimAsync(parent, head)).Digest.Should().Be(complete.Digest);
        var entries = await _executor.QueryAsync<DeploymentRecoveryAuditEntry>(SurrealQuery<DeploymentRecoveryAuditEntry>.From()
            .Where(row => row.AuditId == audit.Id));
        entries.Should().HaveCount(30);
        entries.Select(row => row.OperationId).Distinct().Should().HaveCount(30);
    }

    [Fact]
    public async Task BrokenAncestryIsPersistentlyInvalidInsteadOfSilentlyTruncated()
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var head = (await _store.ClaimRecoveryChildAsync(parent, await ChildAsync(parent)))!;
        parent = (await _store.GetAsync(parent.Id))!;
        var link = await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<DeploymentOperation>(head.Id)
            .Set(row => row.ReplacesOperationId, head.Id).Build());
        link.EnsureAllOk(); head = (await _store.GetAsync(head.Id))!;
        var audits = new SurrealDeploymentRecoveryAuditStore(_executor);
        var audit = await audits.ClaimAsync(parent, head);
        var invalid = (await audits.AdvanceAsync(audit.Id, audit.Version))!;
        invalid.State.Should().Be("Invalid"); invalid.InspectedCount.Should().Be(0);
        (await new SurrealDeploymentRecoveryAuditStore(_executor).GetAsync(audit.Id))!.State.Should().Be("Invalid");
    }

    [Fact]
    public async Task ReplacementPermanentlySealsUnsentChildAndPreservesItsEvidence()
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var old = (await _store.ClaimRecoveryChildAsync(parent, await ChildAsync(parent)))!;
        old.State = "Prepared"; old.PreparedJson = "never-exported-candidate";
        old = (await _store.UpdateAsync(old, old.Version))!;
        parent = (await _store.GetAsync(parent.Id))!;
        var replacement = await ChildAsync(parent);
        replacement.ReplacesOperationId = old.Id;
        var saved = await _store.ReplaceRecoveryChildAsync(parent, old, replacement);
        saved.Should().NotBeNull();
        (await _store.GetAsync(parent.Id))!.RecoveryChildId.Should().Be(replacement.Id);
        var sealedOld = (await _store.GetAsync(old.Id))!;
        sealedOld.ReplacedByOperationId.Should().Be(replacement.Id);
        sealedOld.PreparedJson.Should().Be("never-exported-candidate");
        sealedOld.SubmissionAuthorized = true;
        (await _store.UpdateAsync(sealedOld, sealedOld.Version)).Should().BeNull();
        (await _store.ReplaceRecoveryChildAsync(parent, old, replacement)).Should().NotBeNull();
        var competitor = await ChildAsync(parent); competitor.ReplacesOperationId = old.Id;
        (await _store.ReplaceRecoveryChildAsync(parent, old, competitor)).Should().BeNull();
        (await _store.GetAsync(competitor.Id)).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplacementQuoteWithdrawalPreservesChildAndExcludesStaleReplacement(bool withdrawFirst)
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var old = (await _store.ClaimRecoveryChildAsync(parent, await ChildAsync(parent)))!;
        parent = (await _store.GetAsync(parent.Id))!;
        var replacement = await ChildAsync(parent); replacement.ReplacesOperationId = old.Id;
        if (withdrawFirst)
        {
            var withdrawn = (await _store.InvalidateReplacementQuoteAsync(parent, old))!;
            withdrawn.Version.Should().Be(parent.Version + 1);
            withdrawn.RecoveryChildId.Should().Be(old.Id);
            (await _store.GetAsync(old.Id))!.Version.Should().Be(old.Version);
            (await _store.ReplaceRecoveryChildAsync(parent, old, replacement)).Should().BeNull();
            (await _store.GetAsync(old.Id))!.ReplacedByOperationId.Should().BeNull();
        }
        else
        {
            (await _store.ReplaceRecoveryChildAsync(parent, old, replacement)).Should().NotBeNull();
            (await _store.InvalidateReplacementQuoteAsync(parent, old)).Should().BeNull();
            (await _store.GetAsync(parent.Id))!.RecoveryChildId.Should().Be(replacement.Id);
        }
    }

    [Fact]
    public async Task ResumedPredecessorPreventsWithdrawalAgainstItsOldSnapshot()
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var old = (await _store.ClaimRecoveryChildAsync(parent, await ChildAsync(parent)))!;
        parent = (await _store.GetAsync(parent.Id))!;
        var resumed = (await _store.GetAsync(old.Id))!;
        resumed.SubmissionAuthorized = true; resumed.State = "Submitted"; resumed.PreparedJson = "authorized-wire";
        (await _store.UpdateAsync(resumed, resumed.Version)).Should().NotBeNull();
        (await _store.InvalidateReplacementQuoteAsync(parent, old)).Should().BeNull();
        (await _store.GetAsync(parent.Id))!.Version.Should().Be(parent.Version);
    }

    [Fact]
    public async Task PredecessorAuthorizationWinningFirstRollsBackReplacement()
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var old = (await _store.ClaimRecoveryChildAsync(parent, await ChildAsync(parent)))!;
        parent = (await _store.GetAsync(parent.Id))!;
        var replacement = await ChildAsync(parent); replacement.ReplacesOperationId = old.Id;
        var signing = (await _store.GetAsync(old.Id))!;
        signing.SubmissionAuthorized = true; signing.State = "Submitted"; signing.PreparedJson = "authorized-wire";
        (await _store.UpdateAsync(signing, signing.Version)).Should().NotBeNull();
        (await _store.ReplaceRecoveryChildAsync(parent, old, replacement)).Should().BeNull();
        (await _store.GetAsync(parent.Id))!.RecoveryChildId.Should().Be(old.Id);
        (await _store.GetAsync(old.Id))!.ReplacedByOperationId.Should().BeNull();
        (await _store.GetAsync(replacement.Id)).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithdrawalAndChildAdmissionCannotBothWinTheSameParentVersion(bool withdrawFirst)
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var child = await ChildAsync(parent);
        var withdrawal = (await _store.GetAsync(parent.Id))!;
        withdrawal.Stage = "RecoveryQuoteInvalidated";
        if (withdrawFirst)
        {
            (await _store.UpdateAsync(withdrawal, parent.Version)).Should().NotBeNull();
            (await _store.ClaimRecoveryChildAsync(parent, child)).Should().BeNull();
            (await _store.GetAsync(child.Id)).Should().BeNull();
            (await _store.GetAsync(parent.Id))!.RecoveryChildId.Should().BeNull();
        }
        else
        {
            (await _store.ClaimRecoveryChildAsync(parent, child)).Should().NotBeNull();
            (await _store.UpdateAsync(withdrawal, parent.Version)).Should().BeNull();
            (await _store.GetAsync(parent.Id))!.RecoveryChildId.Should().Be(child.Id);
            (await _store.GetAsync(child.Id)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task ParentAuthorizationWinningFirstBlocksStaleRecoverySnapshot()
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var child = await ChildAsync(parent);
        var stale = (await _store.GetAsync(parent.Id))!;
        parent.PreparedJson = "saved-wire"; parent.SubmissionAuthorized = true; parent.State = "Submitted";
        (await _store.UpdateAsync(parent, parent.Version)).Should().NotBeNull();
        (await _store.ClaimRecoveryChildAsync(stale, child)).Should().BeNull();
        (await _store.GetAsync(child.Id)).Should().BeNull();
    }

    [Fact]
    public async Task ChildAuthorizationRequiresLiveExactParentBinding()
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var child = (await _store.ClaimRecoveryChildAsync(parent, await ChildAsync(parent)))!;
        var change = await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<DeploymentOperation>(parent.Id)
            .Set(row => row.RecoveryBindingHash, "different-binding").Build());
        change.EnsureAllOk();
        child.SubmissionAuthorized = true; child.State = "Submitted";
        (await _store.UpdateAsync(child, child.Version)).Should().BeNull();
        (await _store.GetAsync(child.Id))!.SubmissionAuthorized.Should().BeFalse();
    }

    [Fact]
    public async Task CompletionPreservesOriginalEvidenceAndCannotBeReopened()
    {
        var original = Parent();
        original.ResourceId = "funded-buffer"; original.EvidenceJson = "original-chain-proof";
        var parent = (await _store.ClaimAsync(original))!;
        var child = (await _store.ClaimRecoveryChildAsync(parent, await ChildAsync(parent)))!;
        child.State = "Confirmed"; child.EvidenceJson = "exact-close-proof"; child.HistoryJson = "[\"close-receipt\"]";
        child = (await _store.UpdateAsync(child, child.Version))!;
        parent = (await _store.GetAsync(parent.Id))!;
        var completed = (await _store.CompleteRecoveryChildAsync(child.Id, child.Version, parent.Version))!;
        completed.State.Should().Be("AbortedClean");
        completed.EvidenceJson.Should().Be("original-chain-proof");
        completed.ResourceId.Should().Be("funded-buffer");
        completed.RecoveryEvidenceJson.Should().Contain("exact-close-proof");
        var replay = (await _store.CompleteRecoveryChildAsync(child.Id, child.Version, parent.Version))!;
        replay.Version.Should().Be(completed.Version);
        var projected = (await _store.GetAsync(child.Id))!;
        projected.SubmissionAuthorized = true;
        (await _store.UpdateAsync(projected, projected.Version)).Should().BeNull();
    }

    [Fact]
    public async Task CompletionRollsBackChildProjectionWhenParentVersionChanged()
    {
        var parent = (await _store.ClaimAsync(Parent()))!;
        var child = (await _store.ClaimRecoveryChildAsync(parent, await ChildAsync(parent)))!;
        child.State = "Confirmed"; child.EvidenceJson = "proof";
        child = (await _store.UpdateAsync(child, child.Version))!;
        (await _store.CompleteRecoveryChildAsync(child.Id, child.Version, parent.Version)).Should().BeNull();
        (await _store.GetAsync(child.Id))!.RecoveryProjected.Should().BeFalse();
    }

    private static DeploymentOperation Parent() => new()
    {
        Id = Guid.NewGuid().ToString("N"), ActorUserId = Guid.NewGuid().ToString("N"), ArtifactId = "artifact",
        ArtifactHash = "artifact-hash", IntentHash = "original-intent", RequestJson = "original-request",
        State = "Failed", HistoryJson = "[]", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<DeploymentOperation> ChildAsync(DeploymentOperation parent)
    {
        var quote = new NativeOperationQuote(Guid.NewGuid().ToString("N"), "quote-hash", "solana:genesis", "1", "0", "0", "{}", DateTimeOffset.UtcNow.AddMinutes(10));
        await _store.AddQuoteAsync(new NativeQuote { Id = quote.QuoteId, ActorUserId = parent.ActorUserId, TenantId = parent.TenantId, IntentHash = quote.IntentHash, QuoteJson = JsonSerializer.Serialize(quote), ExpiresAt = quote.ExpiresAt });
        var request = new DeploymentStartRequest(parent.ArtifactId, parent.ArtifactHash, new("NativeContract", "Solana", "Devnet"), new(1, "child", "Recovery", "Solana", "Devnet", "solana:genesis", parent.ArtifactHash, JsonSerializer.SerializeToElement(new { }), [], new("1", "0", "0", quote.QuoteId), quote));
        return new DeploymentOperation()
        {
            Id = Guid.NewGuid().ToString("N"), ActorUserId = parent.ActorUserId, TenantId = parent.TenantId,
            ArtifactId = parent.ArtifactId, ArtifactHash = parent.ArtifactHash, IntentHash = "recovery-intent", RequestJson = JsonSerializer.Serialize(request),
            ParentOperationId = parent.Id, RecoveryMode = "AbortCleanup", RecoveryBindingHash = "binding",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
    }
}

