// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class KycIdentityBindingTests
{
    private const string DocumentNumber = "AB123456";
    private const string IssuingState = "GB";

    // ── D23: per-tenant salting makes one human unlinkable across tenants ───

    [Fact]
    public void PersonKey_ForTheSameDocument_DiffersPerTenant()
    {
        var deriver = NewDeriver();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var keyA = deriver.DerivePersonKey(deriver.ScopeFor(tenantA), DocumentNumber, IssuingState);
        var keyB = deriver.DerivePersonKey(deriver.ScopeFor(tenantB), DocumentNumber, IssuingState);

        keyA.Should().NotBeNullOrEmpty();
        keyB.Should().NotBeNullOrEmpty();
        keyA.Should().NotBe(keyB,
            "one human must resolve to an unlinkable key in different tenants, so a "
            + "tenant cannot learn its subject also exists elsewhere on the node");
    }

    [Fact]
    public void PersonKey_IsStableWithinOneTenant()
    {
        var deriver = NewDeriver();
        var scope = deriver.ScopeFor(Guid.NewGuid());

        var first = deriver.DerivePersonKey(scope, DocumentNumber, IssuingState);
        var second = deriver.DerivePersonKey(scope, DocumentNumber, IssuingState);

        first.Should().Be(second);
    }

    [Fact]
    public void PersonKey_IgnoresDocumentNumberFormatting()
    {
        var deriver = NewDeriver();
        var scope = deriver.ScopeFor(Guid.NewGuid());

        var plain = deriver.DerivePersonKey(scope, "AB123456", "GB");
        var formatted = deriver.DerivePersonKey(scope, " ab-123 456 ", "gb");

        formatted.Should().Be(plain,
            "the same physical document must key identically whether or not the "
            + "vendor happens to return it with separators");
    }

    [Fact]
    public void PersonKey_ChangesWithTheIssuingState()
    {
        var deriver = NewDeriver();
        var scope = deriver.ScopeFor(Guid.NewGuid());

        var gb = deriver.DerivePersonKey(scope, DocumentNumber, "GB");
        var ge = deriver.DerivePersonKey(scope, DocumentNumber, "GE");

        gb.Should().NotBe(ge,
            "document numbers are only unique within an issuing state");
    }

    [Fact]
    public void PersonKey_PreimageIsUnambiguous()
    {
        // A naive concatenation would collide these two: "AB1"+"23GB" vs "AB123"+"GB".
        var deriver = NewDeriver();
        var scope = deriver.ScopeFor(Guid.NewGuid());

        var first = deriver.DerivePersonKey(scope, "AB1", "23GB");
        var second = deriver.DerivePersonKey(scope, "AB123", "GB");

        first.Should().NotBe(second);
    }

    [Fact]
    public void GlobalScope_IsNotEqualToAnyTenantScope()
    {
        var deriver = NewDeriver();

        var global = deriver.ScopeFor(null);
        var empty = deriver.ScopeFor(Guid.Empty);
        var tenant = deriver.ScopeFor(Guid.NewGuid());

        global.Should().Be(KycPersonKeyDeriver.GlobalScopeSentinel);
        empty.Should().Be(KycPersonKeyDeriver.GlobalScopeSentinel);
        global.Should().NotBe(tenant);
        Guid.TryParseExact(global, "N", out _).Should().BeFalse(
            "the sentinel must not be able to collide with a real tenant id");
    }

    [Theory]
    [InlineData(null, "GB")]
    [InlineData("AB123456", null)]
    [InlineData("", "GB")]
    [InlineData("   ", "GB")]
    [InlineData("!!!", "GB")]
    public void PersonKey_WithUnusableInputs_FailsClosed(string? document, string? state)
    {
        var deriver = NewDeriver();

        var key = deriver.DerivePersonKey(deriver.ScopeFor(Guid.NewGuid()), document, state);

        key.Should().BeNull("a partially-keyed identity is worse than none");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short-secret")]
    public void PersonKey_WithoutAStrongRootSecret_FailsClosed(string? secret)
    {
        var deriver = NewDeriver(secret);

        deriver.IsConfigured.Should().BeFalse();
        deriver.DerivePersonKey("scope", DocumentNumber, IssuingState).Should().BeNull();
    }

    [Fact]
    public void RecordId_IsDeterministicPerScopeAndKey()
    {
        var first = KycPersonKeyDeriver.RecordId("tenant-a", "person-key-1");
        var second = KycPersonKeyDeriver.RecordId("tenant-a", "person-key-1");
        var other = KycPersonKeyDeriver.RecordId("tenant-b", "person-key-1");

        first.Should().Be(second, "an upsert must converge on the same row");
        first.Should().NotBe(other);
        first.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    // ── Binding ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bind_PersistsOnlyTheFourRetainedFields()
    {
        var store = new CapturingIdentityStore();
        var service = NewService(store);
        var avatarId = Guid.NewGuid();

        var bound = await service.BindAsync(
            avatarId,
            Guid.NewGuid(),
            KycProvider.IDENTOMAT,
            "identomat",
            new HostedSessionSubjectAttributes
            {
                FirstName = "Ada",
                LastName = "Lovelace",
                Birthday = "1815-12-10",
                Citizenship = "GB",
                DocumentNumber = DocumentNumber,
                IssuingState = IssuingState,
            });

        bound.IsError.Should().BeFalse();
        var written = store.LastCandidate!;
        written.FirstName.Should().Be("Ada");
        written.LastName.Should().Be("Lovelace");
        written.Birthday.Should().Be("1815-12-10");
        written.Citizenship.Should().Be("GB");
        written.AvatarIds.Should().ContainSingle().Which.Should().Be(avatarId.ToString("N"));

        // The raw document identifiers must not survive anywhere on the row.
        var serialized = System.Text.Json.JsonSerializer.Serialize(written);
        serialized.Should().NotContain(DocumentNumber);
        written.PersonKey.Should().NotContain(DocumentNumber);
        written.GlobalPersonKey.Should().NotContain(DocumentNumber);
    }

    [Fact]
    public async Task Bind_WithoutARootSecret_FailsClosedAndWritesNothing()
    {
        var store = new CapturingIdentityStore();
        var service = NewService(store, secret: null);

        var bound = await service.BindAsync(
            Guid.NewGuid(),
            null,
            KycProvider.IDENTOMAT,
            "identomat",
            new HostedSessionSubjectAttributes
            {
                DocumentNumber = DocumentNumber,
                IssuingState = IssuingState,
            });

        bound.IsError.Should().BeTrue();
        store.BindCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Bind_SetsARetentionDeadline()
    {
        var store = new CapturingIdentityStore();
        var service = NewService(store, retentionDays: 30);

        await service.BindAsync(
            Guid.NewGuid(),
            null,
            KycProvider.IDENTOMAT,
            "identomat",
            new HostedSessionSubjectAttributes
            {
                DocumentNumber = DocumentNumber,
                IssuingState = IssuingState,
            });

        store.LastCandidate!.RetentionExpiresAt.Should().NotBeNull();
        store.LastCandidate.RetentionExpiresAt!.Value
            .Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(30), TimeSpan.FromMinutes(1));
    }

    // ── Eligibility denials are indistinguishable ───────────────────────────

    [Fact]
    public async Task Eligibility_DenialForAnotherAvatar_LooksExactlyLikeOrdinaryIneligibility()
    {
        var store = new CapturingIdentityStore();
        var service = NewService(store);
        var tenantId = Guid.NewGuid();
        var deriver = NewDeriver();
        var personKey = deriver.DerivePersonKey(deriver.ScopeFor(tenantId), DocumentNumber, IssuingState)!;
        store.Seed(new KycIdentity
        {
            Id = KycPersonKeyDeriver.RecordId(deriver.ScopeFor(tenantId), personKey),
            TenantScope = deriver.ScopeFor(tenantId),
            PersonKey = personKey,
            GlobalPersonKey = "global",
            AvatarIds = [Guid.NewGuid().ToString("N")],
        });

        var alreadyClaimed = await service.EvaluateEligibilityAsync(
            Guid.NewGuid(), tenantId, personKey);
        var neverVerified = await service.EvaluateEligibilityAsync(
            Guid.NewGuid(), tenantId, "some-other-person-key");

        alreadyClaimed.Result!.IsEligible.Should().BeFalse();
        neverVerified.Result!.IsEligible.Should().BeFalse();
        alreadyClaimed.Result.Message.Should().Be(neverVerified.Result.Message,
            "'already claimed by another avatar' leaks that the person exists under "
            + "a different avatar or tenant; it must read exactly like 'not verified'");

        // The operator-side cause still distinguishes them.
        alreadyClaimed.Result.Cause
            .Should().Be(KycIneligibilityCause.PersonAlreadyBoundToAnotherAvatar);
        neverVerified.Result.Cause.Should().Be(KycIneligibilityCause.NoVerifiedIdentity);
    }

    [Fact]
    public void EligibilityDecision_NeverSerializesTheOperatorCause()
    {
        var decision = KycEligibilityDecision.Ineligible(
            KycIneligibilityCause.PersonAlreadyBoundToAnotherAvatar);

        var json = System.Text.Json.JsonSerializer.Serialize(decision);

        json.Should().NotContain("Cause");
        json.Should().NotContain(nameof(KycIneligibilityCause.PersonAlreadyBoundToAnotherAvatar));
    }

    [Fact]
    public async Task Eligibility_ForTheBoundAvatar_IsEligible()
    {
        var store = new CapturingIdentityStore();
        var service = NewService(store);
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        var deriver = NewDeriver();
        var scope = deriver.ScopeFor(tenantId);
        var personKey = deriver.DerivePersonKey(scope, DocumentNumber, IssuingState)!;
        store.Seed(new KycIdentity
        {
            Id = KycPersonKeyDeriver.RecordId(scope, personKey),
            TenantScope = scope,
            PersonKey = personKey,
            GlobalPersonKey = "global",
            AvatarIds = [avatarId.ToString("N")],
        });

        var decision = await service.EvaluateEligibilityAsync(avatarId, tenantId, personKey);

        decision.Result!.IsEligible.Should().BeTrue();
    }

    // ── D25: erasure keeps the key, drops the personal fields ───────────────

    [Fact]
    public async Task Erasure_ClearsThePersonalFieldsAndPreservesThePersonKey()
    {
        var store = new CapturingIdentityStore();
        var screening = new InMemoryKycScreeningStore();
        store.Seed(new KycIdentity
        {
            Id = "record-1",
            TenantScope = "tenant-a",
            PersonKey = "person-key-1",
            GlobalPersonKey = "global-key-1",
            AvatarIds = ["avatar-1"],
            FirstName = "Ada",
            LastName = "Lovelace",
            Birthday = "1815-12-10",
            Citizenship = "GB",
            CreatedDate = DateTimeOffset.UtcNow.AddYears(-6),
        });
        var service = new KycIdentityErasureService(
            store, screening, NullLogger<KycIdentityErasureService>.Instance);

        var erased = await service.EraseAsync("record-1", "subject-request");

        erased.IsError.Should().BeFalse();
        var row = erased.Result!;
        row.FirstName.Should().BeNull();
        row.LastName.Should().BeNull();
        row.Birthday.Should().BeNull();
        row.Citizenship.Should().BeNull();

        row.PersonKey.Should().Be("person-key-1",
            "the salted key must survive so uniqueness and the KYC audit trail hold "
            + "after an Art. 17 erasure");
        row.GlobalPersonKey.Should().Be("global-key-1");
        row.AvatarIds.Should().ContainSingle().Which.Should().Be("avatar-1");
        row.ErasedAt.Should().NotBeNull();
        row.ErasureReason.Should().Be("subject-request");
    }

    [Fact]
    public async Task RetentionSweep_ErasesOnlyBindingsPastTheirDeadline()
    {
        var store = new CapturingIdentityStore();
        var screening = new InMemoryKycScreeningStore();
        store.Seed(new KycIdentity
        {
            Id = "expired",
            PersonKey = "k1",
            GlobalPersonKey = "g1",
            FirstName = "Ada",
            RetentionExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        store.Seed(new KycIdentity
        {
            Id = "current",
            PersonKey = "k2",
            GlobalPersonKey = "g2",
            FirstName = "Grace",
            RetentionExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        var service = new KycIdentityErasureService(
            store, screening, NullLogger<KycIdentityErasureService>.Instance);

        var swept = await service.RunRetentionSweepAsync();

        swept.Result.Should().Be(1);
        store.Peek("expired")!.FirstName.Should().BeNull();
        store.Peek("expired")!.PersonKey.Should().Be("k1");
        store.Peek("current")!.FirstName.Should().Be("Grace");
    }

    [Fact]
    public async Task Erasure_IsIdempotent()
    {
        var store = new CapturingIdentityStore();
        var screening = new InMemoryKycScreeningStore();
        store.Seed(new KycIdentity
        {
            Id = "record-1",
            PersonKey = "k1",
            GlobalPersonKey = "g1",
            FirstName = "Ada",
        });
        var service = new KycIdentityErasureService(
            store, screening, NullLogger<KycIdentityErasureService>.Instance);

        var first = await service.EraseAsync("record-1", "subject-request");
        var second = await service.EraseAsync("record-1", "subject-request");

        first.IsError.Should().BeFalse();
        second.IsError.Should().BeFalse();
        second.Result!.PersonKey.Should().Be("k1");
    }

    // ── D3/D23: a second avatar cannot claim a person already bound ─────────

    [Fact]
    public async Task Bind_RefusesASecondAvatarForAPersonAlreadyBoundInTheSameScope()
    {
        // Sybil resistance lives HERE, at the write. An unconditional append adds the
        // caller to every existing binding, after which "already bound to another
        // avatar" is unobservable by construction and the check is a tautology.
        var store = new CapturingIdentityStore();
        var service = NewService(store);
        var tenantId = Guid.NewGuid();
        var firstAvatar = Guid.NewGuid();
        var secondAvatar = Guid.NewGuid();
        var attributes = new HostedSessionSubjectAttributes
        {
            DocumentNumber = DocumentNumber,
            IssuingState = IssuingState,
        };

        var first = await service.BindAsync(
            firstAvatar, tenantId, KycProvider.IDENTOMAT, "identomat", attributes);
        var second = await service.BindAsync(
            secondAvatar, tenantId, KycProvider.IDENTOMAT, "identomat", attributes);

        first.IsError.Should().BeFalse(first.Message);
        second.IsError.Should().BeTrue(
            "one human must not hold two verified avatars inside one tenant");
        second.Message.Should().Be(KycIdentityBindingService.IneligibleMessage,
            "the refusal reads exactly like every other refusal; naming the cause "
            + "would tell the tenant its subject already exists");

        var deriver = NewDeriver();
        var scope = deriver.ScopeFor(tenantId);
        var personKey = deriver.DerivePersonKey(scope, DocumentNumber, IssuingState)!;
        store.Peek(KycPersonKeyDeriver.RecordId(scope, personKey))!
            .AvatarIds.Should().ContainSingle().Which.Should().Be(firstAvatar.ToString("N"));
    }

    [Fact]
    public async Task Bind_ForTheSameAvatarTwice_IsIdempotent()
    {
        var store = new CapturingIdentityStore();
        var service = NewService(store);
        var tenantId = Guid.NewGuid();
        var avatarId = Guid.NewGuid();
        var attributes = new HostedSessionSubjectAttributes
        {
            DocumentNumber = DocumentNumber,
            IssuingState = IssuingState,
        };

        await service.BindAsync(avatarId, tenantId, KycProvider.IDENTOMAT, "identomat", attributes);
        var replayed = await service.BindAsync(
            avatarId, tenantId, KycProvider.IDENTOMAT, "identomat", attributes);

        replayed.IsError.Should().BeFalse(
            "a redelivered callback re-binds the SAME avatar and must not be refused");
        replayed.Result!.AvatarIds.Should().ContainSingle();
    }

    [Fact]
    public async Task Bind_ForTheSamePersonUnderADifferentTenant_IsAllowed()
    {
        // The refusal is per uniqueness SCOPE. A global one would reject a second
        // tenant's legitimate verification and, by failing, disclose that the human
        // exists elsewhere on the node (D23).
        var store = new CapturingIdentityStore();
        var service = NewService(store);
        var attributes = new HostedSessionSubjectAttributes
        {
            DocumentNumber = DocumentNumber,
            IssuingState = IssuingState,
        };

        var tenantA = await service.BindAsync(
            Guid.NewGuid(), Guid.NewGuid(), KycProvider.IDENTOMAT, "identomat", attributes);
        var tenantB = await service.BindAsync(
            Guid.NewGuid(), Guid.NewGuid(), KycProvider.IDENTOMAT, "identomat", attributes);

        tenantA.IsError.Should().BeFalse(tenantA.Message);
        tenantB.IsError.Should().BeFalse(tenantB.Message);
    }

    // ── D25: erasure covers the AML trail, not just the binding ─────────────

    [Fact]
    public async Task Erasure_AlsoTombstonesTheNameAndBirthdayHeldOnTheScreeningTrail()
    {
        var avatarId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var store = new CapturingIdentityStore();
        var screening = new InMemoryKycScreeningStore();
        store.Seed(new KycIdentity
        {
            Id = "record-1",
            TenantScope = tenantId.ToString("N"),
            TenantId = tenantId.ToString("N"),
            PersonKey = "person-key-1",
            GlobalPersonKey = "global-key-1",
            AvatarIds = [avatarId.ToString("N")],
            FirstName = "Ada",
        });
        screening.SeedResult(avatarId, tenantId, KycScreeningDecision.NO_HIT);
        screening.SeedMonitor(
            "Ada Lovelace", "1815-12-10", "monitor-1",
            tenantId: tenantId, subjectAvatarId: avatarId);
        var service = new KycIdentityErasureService(
            store, screening, NullLogger<KycIdentityErasureService>.Instance);

        var erased = await service.EraseAsync("record-1", "subject-request");

        erased.IsError.Should().BeFalse(erased.Message);
        screening.SavedResults.Should().OnlyContain(result => result.QueryFullName == null);
        screening.SavedMonitors.Should().OnlyContain(monitor => monitor.QueryFullName == null);
        screening.SavedMonitors.Should().OnlyContain(monitor => monitor.QueryBirthdayTimeFrom == null);
        screening.SavedResults.Should().OnlyContain(result => result.ErasedAt != null);
        screening.SavedResults.Should().OnlyContain(
            result => result.Decision == KycScreeningDecision.NO_HIT,
            "the AML verdict is record-keeping and survives; only the PII is tombstoned");
    }

    [Fact]
    public async Task RetentionSweep_AlsoErasesScreeningRowsPastTheirOwnDeadline()
    {
        // A screening row outlives the binding it came from: the poll writes new
        // results for years. It therefore needs its own retention stamp swept, not
        // just erasure in the slipstream of a binding.
        var store = new CapturingIdentityStore();
        var screening = new InMemoryKycScreeningStore();
        screening.SeedMonitor(
            "Ada Lovelace", "1815-12-10", "monitor-expired",
            retentionExpiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        screening.SeedMonitor(
            "Grace Hopper", "1906-12-09", "monitor-current",
            retentionExpiresAt: DateTimeOffset.UtcNow.AddDays(30));
        var service = new KycIdentityErasureService(
            store, screening, NullLogger<KycIdentityErasureService>.Instance);

        var swept = await service.RunRetentionSweepAsync();

        swept.Result.Should().Be(1);
        screening.SavedMonitors.Single(monitor => monitor.ProviderMonitorId == "monitor-expired")
            .QueryFullName.Should().BeNull();
        screening.SavedMonitors.Single(monitor => monitor.ProviderMonitorId == "monitor-current")
            .QueryFullName.Should().Be("Grace Hopper");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="secret"/> defaults to the sentinel "default" so a test can
    /// pass an explicit null to exercise the unconfigured path; a plain
    /// null-coalescing default would silently substitute a valid secret instead.
    /// </summary>
    private static KycPersonKeyDeriver NewDeriver(string? secret = "default")
        => new(Options.Create(new KycSettings
        {
            Identity = new KycIdentitySettings
            {
                PersonKeySecret = secret == "default" ? new string('s', 48) : secret,
            },
        }));

    private static KycIdentityBindingService NewService(
        IKycIdentityStore store,
        string? secret = "default",
        int retentionDays = 1825)
    {
        var options = Options.Create(new KycSettings
        {
            Identity = new KycIdentitySettings
            {
                PersonKeySecret = secret == "default" ? new string('s', 48) : secret,
                RetentionDays = retentionDays,
            },
        });
        return new KycIdentityBindingService(
            store,
            new KycPersonKeyDeriver(options),
            options,
            NullLogger<KycIdentityBindingService>.Instance);
    }

    /// <summary>
    /// Minimal identity store that keeps the erasure semantics honest: it clears the
    /// four personal fields and leaves everything else alone, exactly as the
    /// SurrealQL explicit-SET clause does.
    /// </summary>
    private sealed class CapturingIdentityStore : IKycIdentityStore
    {
        private readonly Dictionary<string, KycIdentity> _rows = new(StringComparer.Ordinal);

        public int BindCallCount { get; private set; }
        public KycIdentity? LastCandidate { get; private set; }

        public void Seed(KycIdentity identity) => _rows[identity.Id] = identity;

        public KycIdentity? Peek(string id) => _rows.GetValueOrDefault(id);

        public Task<AZOAResult<KycIdentity>> BindAvatarAsync(
            KycIdentity candidate,
            CancellationToken ct = default)
        {
            BindCallCount++;
            LastCandidate = candidate;
            if (_rows.TryGetValue(candidate.Id, out var existing))
            {
                // Mirrors the store's conditional SET: the caller is appended only when
                // the binding is empty or already contains it. An unconditional append
                // here would make the Sybil refusal untestable, which is exactly how it
                // went unnoticed in production code.
                var held = existing.AvatarIds ?? [];
                var incoming = candidate.AvatarIds ?? [];
                if (held.Count == 0 || incoming.All(avatar => held.Contains(avatar, StringComparer.Ordinal)))
                {
                    existing.AvatarIds = held
                        .Concat(incoming)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                }

                return Task.FromResult(AZOAResult<KycIdentity>.Success(existing));
            }

            _rows[candidate.Id] = candidate;
            return Task.FromResult(AZOAResult<KycIdentity>.Success(candidate));
        }

        public Task<AZOAResult<KycIdentity>> GetByPersonKeyAsync(
            string tenantScope,
            string personKey,
            CancellationToken ct = default)
        {
            var found = _rows.Values.FirstOrDefault(row =>
                row.TenantScope == tenantScope && row.PersonKey == personKey);
            return Task.FromResult(new AZOAResult<KycIdentity> { Result = found });
        }

        public Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetByGlobalPersonKeyAsync(
            string globalPersonKey,
            CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<KycIdentity>>.Success(
                _rows.Values.Where(row => row.GlobalPersonKey == globalPersonKey).ToList()));

        public Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetErasureDueAsync(
            DateTimeOffset asOf,
            int limit,
            CancellationToken ct = default)
            => Task.FromResult(AZOAResult<IReadOnlyList<KycIdentity>>.Success(
                _rows.Values
                    .Where(row => row.ErasedAt is null
                        && row.RetentionExpiresAt is not null
                        && row.RetentionExpiresAt <= asOf)
                    .Take(limit)
                    .ToList()));

        public Task<AZOAResult<KycIdentity>> EraseAsync(
            string recordId,
            string reason,
            DateTimeOffset erasedAt,
            CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(recordId, out var row))
                return Task.FromResult(AZOAResult<KycIdentity>.Failure("not found"));

            row.FirstName = null;
            row.LastName = null;
            row.Birthday = null;
            row.Citizenship = null;
            row.ErasedAt = erasedAt;
            row.ErasureReason = reason;
            row.ModifiedDate = erasedAt;
            return Task.FromResult(AZOAResult<KycIdentity>.Success(row));
        }
    }
}
