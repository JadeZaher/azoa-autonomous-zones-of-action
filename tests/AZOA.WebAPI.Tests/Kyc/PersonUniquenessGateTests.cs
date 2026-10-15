// SPDX-License-Identifier: UNLICENSED
// D3/D23 per-operation uniqueness.
//
// Two failure modes are asserted here rather than assumed, because this repo has
// shipped both before:
//   1. "the adapter exists but the gate never consults it" — the AML decision was
//      INERT because nothing called it. Several tests below fail if the gate stops
//      consulting the identity store, the claim store, or global_person_key.
//   2. a refusal that varies by cause, which rebuilds the cross-tenant oracle that
//      per-tenant salting exists to hide.

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Mcp;
using AZOA.WebAPI.Mcp.Tools;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class PersonUniquenessGateTests
{
    private const string OperationKey = "governance:proposal:abc123";

    // ── The declaration: presence is the opt-in, Tenant is the default ──────

    [Fact]
    public void Scope_DefaultsToTenant_SoTheD23DefaultCannotBeReachedByForgetting()
    {
        var declared = new PersonUniquenessRequirement { OperationKey = OperationKey };
        var deserialized = JsonSerializer.Deserialize<PersonUniquenessRequirement>(
            """{"operationKey":"governance:proposal:abc123"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        declared.Scope.Should().Be(PersonUniquenessScope.Tenant);
        deserialized.Scope.Should().Be(PersonUniquenessScope.Tenant);
        ((int)PersonUniquenessScope.Tenant).Should().Be(0,
            "a default-constructed record and an omitted JSON field must both land "
            + "on per-tenant; Global has to be typed out to be reached");
    }

    [Fact]
    public void Scope_GlobalIsReachableOnlyByAnExplicitOptIn()
    {
        var deserialized = JsonSerializer.Deserialize<PersonUniquenessRequirement>(
            """{"operationKey":"governance:proposal:abc123","scope":"Global"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        deserialized.Scope.Should().Be(PersonUniquenessScope.Global);
    }

    [Theory]
    [InlineData("  Governance:Proposal:ABC  ", "governance:proposal:abc")]
    [InlineData("a.b_c-d:1", "a.b_c-d:1")]
    public void Canonicalize_TrimsAndLowercases(string input, string expected)
        => PersonUniquenessRequirement.Canonicalize(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has#hash")]
    public void Canonicalize_RejectsRatherThanStrips(string? input)
        => PersonUniquenessRequirement.Canonicalize(input).Should().BeNull(
            "stripping would collapse 'a b' and 'ab' into one uniqueness domain");

    [Fact]
    public void Canonicalize_RejectsRatherThanTruncates()
    {
        var tooLong = new string('a', PersonUniquenessRequirement.MaxOperationKeyLength + 1);

        PersonUniquenessRequirement.Canonicalize(tooLong).Should().BeNull(
            "truncation would merge two long keys sharing a 200-character prefix, "
            + "letting one operation silently consume another's claims");
    }

    // ── The claim itself ────────────────────────────────────────────────────

    [Fact]
    public async Task Claim_FirstUserWins_SecondUserForTheSamePersonIsRefused()
    {
        var tenantId = Guid.NewGuid();
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", firstUser, secondUser);
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims);

        var first = await gate.TryClaimAsync(Requirement(), firstUser, tenantId);
        var second = await gate.TryClaimAsync(Requirement(), secondUser, tenantId);

        first.Result!.IsEligible.Should().BeTrue();
        second.Result!.IsEligible.Should().BeFalse(
            "one human holds one claim per operation, however many verified users "
            + "they legitimately hold (D1 permits many, D3 constrains the claim)");
        second.Result.Cause.Should().Be(KycIneligibilityCause.PersonAlreadyClaimedOperation);
        second.IsError.Should().BeFalse("a refusal is never an error result");
    }

    [Fact]
    public async Task Claim_ByTheSameUserTwice_IsIdempotent()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", userId);
        var gate = NewGate(identities, new FakeClaimStore());

        var first = await gate.TryClaimAsync(Requirement(), userId, tenantId);
        var replay = await gate.TryClaimAsync(Requirement(), userId, tenantId);

        first.Result!.IsEligible.Should().BeTrue();
        replay.Result!.IsEligible.Should().BeTrue(
            "a retried node step must not lock the runner out of its own claim");
    }

    [Fact]
    public async Task Claim_ForADifferentOperation_IsIndependent()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", userId);
        var gate = NewGate(identities, new FakeClaimStore());

        var proposalOne = await gate.TryClaimAsync(Requirement("governance:proposal:one"), userId, tenantId);
        var proposalTwo = await gate.TryClaimAsync(Requirement("governance:proposal:two"), userId, tenantId);

        proposalOne.Result!.IsEligible.Should().BeTrue();
        proposalTwo.Result!.IsEligible.Should().BeTrue();
    }

    [Fact]
    public async Task Claim_InTenantScope_DoesNotSeeAnotherTenantsClaim()
    {
        // Per-tenant is the default and the tenant salt makes one human resolve to
        // two unlinkable keys, so the same operation key under two tenants is two
        // separate uniqueness domains.
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantA, "person-a-in-tenant-a", "global-shared", userA);
        identities.SeedPerson(tenantB, "person-a-in-tenant-b", "global-shared", userB);
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims);

        var inA = await gate.TryClaimAsync(Requirement(), userA, tenantA);
        var inB = await gate.TryClaimAsync(Requirement(), userB, tenantB);

        inA.Result!.IsEligible.Should().BeTrue();
        inB.Result!.IsEligible.Should().BeTrue();
        claims.Rows.Should().HaveCount(2);
    }

    // ── The first production wiring of global_person_key ────────────────────

    [Fact]
    public async Task Claim_InGlobalScope_UsesTheGlobalPersonKeyAndCrossesTenants()
    {
        // This test is the guard against the repeated defect: a global person key
        // that exists but is read by nothing. Remove the `Scope == Global ->
        // GlobalPersonKey` branch and the second claim starts succeeding.
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantA, "person-a-in-tenant-a", "global-shared", userA);
        identities.SeedPerson(tenantB, "person-a-in-tenant-b", "global-shared", userB);
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims);
        var requirement = Requirement(scope: PersonUniquenessScope.Global);

        var inA = await gate.TryClaimAsync(requirement, userA, tenantA);
        var inB = await gate.TryClaimAsync(requirement, userB, tenantB);

        inA.Result!.IsEligible.Should().BeTrue();
        inB.Result!.IsEligible.Should().BeFalse(
            "a node-global claim is the ONE case where one human is deliberately "
            + "linked across tenants, and it is what global_person_key is for");
        claims.Rows.Should().ContainSingle();
        claims.Rows.Single().PersonKey.Should().Be("global-shared");
        claims.Rows.Single().UniquenessScope
            .Should().Be(KycPersonKeyDeriver.GlobalScopeSentinel);
    }

    [Fact]
    public async Task Claim_InTenantScope_NeverStoresTheGlobalPersonKey()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", userId);
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims);

        await gate.TryClaimAsync(Requirement(), userId, tenantId);

        claims.Rows.Single().PersonKey.Should().Be("person-a");
        claims.Rows.Single().UniquenessScope.Should().Be(tenantId.ToString("N"));
    }

    // ── The gate is CONSULTED, not merely defined ───────────────────────────

    [Fact]
    public async Task Claim_ConsultsTheIdentityStoreAndTheClaimLedgerOnEveryCall()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", userId);
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims);

        await gate.TryClaimAsync(Requirement(), userId, tenantId);

        identities.GetByUserCallCount.Should().Be(1,
            "the acting user must be resolved to a person; a gate that skips this "
            + "is the 'adapter exists but nothing consults it' defect");
        claims.TryClaimCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Claim_DecidesFromTheWriteItReadsBack_NotFromAPrecedingRead()
    {
        // The store is told to hand the row to somebody else. A gate that decided
        // from a preceding "is it free?" read would still answer Eligible here.
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", userId);
        var claims = new FakeClaimStore { ForceWinner = Guid.NewGuid().ToString("N") };
        var gate = NewGate(identities, claims);

        var decision = await gate.TryClaimAsync(Requirement(), userId, tenantId);

        decision.Result!.IsEligible.Should().BeFalse();
        decision.Result.Cause.Should().Be(KycIneligibilityCause.PersonAlreadyClaimedOperation);
    }

    [Fact]
    public async Task Claim_UnderConcurrency_ProducesExactlyOneWinner()
    {
        // Ten distinct users, one human, one operation. The store decides the winner
        // inside one statement and every caller reads the result back; a preceding
        // read would be passed by all ten.
        var tenantId = Guid.NewGuid();
        var users = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", users);
        var claims = new FakeClaimStore { ClaimDelay = TimeSpan.FromMilliseconds(5) };
        var gate = NewGate(identities, claims);

        var decisions = await Task.WhenAll(users.Select(user =>
            gate.TryClaimAsync(Requirement(), user, tenantId)));

        decisions.Should().OnlyContain(decision => decision.IsError == false);
        decisions.Count(decision => decision.Result!.IsEligible).Should().Be(1,
            "the claim ledger is the Sybil chokepoint; two winners make it a tautology");
        claims.Rows.Should().ContainSingle();
    }

    // ── ONE SENTENCE, ALWAYS ────────────────────────────────────────────────

    [Fact]
    public async Task EveryRefusalPath_ReturnsExactlyOneMessageAndNeverAnError()
    {
        var refusals = await AllRefusalsAsync();

        refusals.Should().HaveCountGreaterThan(4);
        refusals.Should().OnlyContain(entry => entry.Decision.IsError == false,
            "an error result travels a different controller path (4xx/5xx vs a 200 "
            + "carrying IsEligible=false) and that status code is a usable oracle");
        refusals.Should().OnlyContain(entry => entry.Decision.Result!.IsEligible == false);

        refusals
            .Select(entry => entry.Decision.Result!.Message)
            .Distinct(StringComparer.Ordinal)
            .Should().ContainSingle()
            .Which.Should().Be(KycIdentityBindingService.IneligibleMessage);
    }

    [Fact]
    public async Task EveryRefusalPath_StillCarriesADiscriminatingOperatorCause()
    {
        var refusals = await AllRefusalsAsync();

        refusals.Should().OnlyContain(entry => entry.Decision.Result!.Cause != null);
        refusals
            .Select(entry => entry.Decision.Result!.Cause)
            .Distinct()
            .Should().HaveCountGreaterThan(1,
                "support still needs to tell 'never verified' from 'already claimed'; "
                + "only the CONSUMER-visible answer is flattened");
    }

    [Fact]
    public async Task Refusal_NeverSerializesTheOperatorCause()
    {
        var refusals = await AllRefusalsAsync();

        foreach (var entry in refusals)
        {
            var json = JsonSerializer.Serialize(entry.Decision.Result);
            json.Should().NotContain("Cause", $"path '{entry.Name}' must not project the cause");
            json.Should().NotContain(entry.Decision.Result!.Cause!.Value.ToString());
        }
    }

    [Fact]
    public async Task Unavailability_IsADenial_NotAnOutageSignal()
    {
        // A 503 that appears only for already-claimed people is the leak wearing a
        // hat, so a store fault answers exactly like an ordinary ineligibility.
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", userId);
        var gate = NewGate(identities, new FakeClaimStore { FailEverything = true });

        var decision = await gate.TryClaimAsync(Requirement(), userId, tenantId);

        decision.IsError.Should().BeFalse();
        decision.Code.Should().BeNull("no wire discriminator may distinguish this refusal");
        decision.Result!.IsEligible.Should().BeFalse();
        decision.Result.Message.Should().Be(KycIdentityBindingService.IneligibleMessage);
        decision.Result.Cause.Should().Be(KycIneligibilityCause.UniquenessUnavailable);
    }

    [Fact]
    public async Task ProgrammingErrors_AreTheOnlyThingThatReturnsIsError()
    {
        var gate = NewGate(new FakeIdentityStore(), new FakeClaimStore());

        var noUser = await gate.TryClaimAsync(Requirement(), Guid.Empty, Guid.NewGuid());

        noUser.IsError.Should().BeTrue(
            "an empty user id cannot depend on whether a subject exists elsewhere, "
            + "so it is safe — and correct — to surface it as an error");
    }

    // ── Timing: no early return on 'no identity' ────────────────────────────

    [Fact]
    public async Task Claim_WithNoIdentity_StillPerformsTheClaimRoundTrip()
    {
        // Without this, a stopwatch separates "no identity" from "already claimed":
        // one costs two round trips and the other one. This narrows the channel to
        // sub-round-trip noise; it does not close it.
        var identities = new FakeIdentityStore();
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims);

        var decision = await gate.TryClaimAsync(Requirement(), Guid.NewGuid(), Guid.NewGuid());

        decision.Result!.Cause.Should().Be(KycIneligibilityCause.NoVerifiedIdentity);
        identities.GetByUserCallCount.Should().Be(1);
        claims.TryClaimCallCount.Should().Be(1,
            "the round-trip count must not depend on whether the subject resolved");
    }

    [Fact]
    public async Task Claim_WithNoIdentity_UsesASentinelKeyThatCannotCollideWithARealPerson()
    {
        var identities = new FakeIdentityStore();
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims);

        await gate.TryClaimAsync(Requirement(), Guid.NewGuid(), Guid.NewGuid());

        var written = claims.Rows.Single();
        written.PersonKey.Should().StartWith(PersonUniquenessGate.SentinelPersonKeyPrefix);
        // Real person keys are unpadded base64url of an HMAC, so '!' can never occur.
        PersonUniquenessGate.SentinelPersonKeyPrefix.Should().Contain("!");
        NewDeriver().DerivePersonKey("scope", "AB123456", "GB")
            .Should().NotContain("!");
    }

    [Fact]
    public async Task Claim_ForAnUnresolvedPerson_IsNeverEligibleEvenThoughTheSentinelClaimSucceeds()
    {
        // The sentinel is per-user, so the sentinel claim always "wins". The
        // resolution cause, not the claim, is what decides.
        var identities = new FakeIdentityStore();
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims);
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var first = await gate.TryClaimAsync(Requirement(), userId, tenantId);
        var second = await gate.TryClaimAsync(Requirement(), userId, tenantId);

        first.Result!.IsEligible.Should().BeFalse();
        second.Result!.IsEligible.Should().BeFalse();
    }

    [Fact]
    public async Task Claim_ForAnErasedIdentity_FailsClosed()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", userId);
        identities.EraseAll();
        var gate = NewGate(identities, new FakeClaimStore());

        var decision = await gate.TryClaimAsync(Requirement(), userId, tenantId);

        decision.Result!.IsEligible.Should().BeFalse();
        decision.Result.Cause.Should().Be(KycIneligibilityCause.IdentityErased);
    }

    [Fact]
    public async Task Claim_WithoutAConfiguredRootSecret_FailsClosed()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", userId);
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims, secret: null);

        var decision = await gate.TryClaimAsync(Requirement(), userId, tenantId);

        decision.IsError.Should().BeFalse();
        decision.Result!.IsEligible.Should().BeFalse();
        decision.Result.Cause.Should().Be(KycIneligibilityCause.UniquenessUnavailable);
        claims.Rows.Should().BeEmpty("an unconfigured node must not write a claim it cannot key");
    }

    // ── Preview and compensation ────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_WritesNothing()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", userId);
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims);

        var preview = await gate.EvaluateAsync(Requirement(), userId, tenantId);

        preview.Result!.IsEligible.Should().BeTrue();
        claims.TryClaimCallCount.Should().Be(0);
        claims.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Evaluate_AfterAnotherUserClaimed_IsIneligibleWithTheSameSentence()
    {
        var tenantId = Guid.NewGuid();
        var holder = Guid.NewGuid();
        var other = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", holder, other);
        var claims = new FakeClaimStore();
        var gate = NewGate(identities, claims);
        await gate.TryClaimAsync(Requirement(), holder, tenantId);

        var preview = await gate.EvaluateAsync(Requirement(), other, tenantId);

        preview.Result!.IsEligible.Should().BeFalse();
        preview.Result.Message.Should().Be(KycIdentityBindingService.IneligibleMessage);
    }

    [Fact]
    public async Task Release_FreesTheClaimSoTheVoterIsNotLockedOutByAFailedSideEffect()
    {
        var tenantId = Guid.NewGuid();
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", firstUser, secondUser);
        var gate = NewGate(identities, new FakeClaimStore());

        await gate.TryClaimAsync(Requirement(), firstUser, tenantId);
        var released = await gate.ReleaseAsync(Requirement(), firstUser, tenantId);
        var retry = await gate.TryClaimAsync(Requirement(), secondUser, tenantId);

        released.Result.Should().BeTrue();
        retry.Result!.IsEligible.Should().BeTrue(
            "a claim that bought nothing must not permanently lock the person out");
    }

    [Fact]
    public async Task Release_ByANonHolder_DoesNotFreeTheClaim()
    {
        var tenantId = Guid.NewGuid();
        var holder = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", holder, attacker);
        var gate = NewGate(identities, new FakeClaimStore());
        await gate.TryClaimAsync(Requirement(), holder, tenantId);

        var released = await gate.ReleaseAsync(Requirement(), attacker, tenantId);

        released.Result.Should().BeFalse();
        (await gate.EvaluateAsync(Requirement(), attacker, tenantId))
            .Result!.IsEligible.Should().BeFalse();
    }

    [Fact]
    public async Task Release_WhenNothingWasHeld_IsIdempotent()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identities = new FakeIdentityStore();
        identities.SeedPerson(tenantId, "person-a", "global-a", userId);
        var gate = NewGate(identities, new FakeClaimStore());

        var released = await gate.ReleaseAsync(Requirement(), userId, tenantId);

        released.IsError.Should().BeFalse();
        released.Result.Should().BeFalse();
    }

    // ── The claim row is the OTHER half of the leak ─────────────────────────

    /// <summary>Tables whose exposure would reveal cross-tenant existence.</summary>
    public static TheoryData<string> OperatorOnlyTables()
    {
        var data = new TheoryData<string>();
        data.Add(KycIdentity.SchemaNameConst);
        data.Add(PersonOperationClaim.SchemaNameConst);
        return data;
    }

    [Theory]
    [MemberData(nameof(OperatorOnlyTables))]
    public async Task McpUserScopedQuery_RefusesTheClaimLedger(string table)
    {
        var tool = new UserScopedQueryTool();
        var args = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { table }));
        var context = new ToolCallContext(
            Guid.NewGuid(),
            Moq.Mock.Of<SurrealForge.Client.Query.ISurrealExecutor>(),
            Moq.Mock.Of<IServiceProvider>());

        var response = await tool.ExecuteAsync(context, args, CancellationToken.None);

        response.TryGetProperty("error", out var error).Should().BeTrue(
            $"a perfect denial message is worthless if a consumer can list '{table}'");
        error.GetString().Should().Be("table_not_allowed");
    }

    [Theory]
    [MemberData(nameof(OperatorOnlyTables))]
    public void McpInputSchemaEnum_DoesNotAdvertiseTheClaimLedger(string table)
    {
        var advertised = new UserScopedQueryTool().InputSchema
            .GetProperty("properties")
            .GetProperty("table")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();

        advertised.Should().NotContain(table);
    }

    [Fact]
    public void SearchableEntityType_HasNoClaimMember()
    {
        Enum.GetNames<SearchableEntityType>().Should().NotContain(name =>
            name.Contains("Claim", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Uniqueness", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Person", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HolonQueryRequest_CannotBePointedAtTheClaimLedger()
    {
        typeof(HolonQueryRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().NotContain(name =>
                name.Contains("Table", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Schema", StringComparison.OrdinalIgnoreCase)
                || name.Contains("PersonKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClaimLedger_CarriesNoAttributeAConsumerCouldJoinOn()
    {
        var forbidden = new[] { "FirstName", "LastName", "Birthday", "Citizenship", "DocumentNumber", "IssuingState" };

        typeof(PersonOperationClaim)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().NotIntersectWith(forbidden,
                "the ledger stores a person KEY; a joinable personal attribute here "
                + "would re-identify the pseudonymous claim trail");
    }

    [Fact]
    public void ClaimRecordId_IsDeterministicAndUnambiguous()
    {
        var first = PersonOperationClaim.RecordIdFor("scope", "operation", "person");

        first.Should().Be(PersonOperationClaim.RecordIdFor("scope", "operation", "person"),
            "an upsert must converge on one row");
        first.Should().MatchRegex("^[0-9a-f]{32}$");
        first.Should().NotBe(PersonOperationClaim.RecordIdFor("scope", "operationperso", "n"),
            "a bare concatenation would merge two operations' uniqueness domains");
        first.Should().NotBe(PersonOperationClaim.RecordIdFor("scope", "operation", "person2"));
        first.Should().NotBe(PersonOperationClaim.RecordIdFor("scope2", "operation", "person"));
    }

    [Fact]
    public void FrozenContract_KeepsTheThreeSignaturesSiblingLanesCodeAgainst()
    {
        // Four lanes code against these verbatim. A rename or a reordered parameter
        // is a silent break for whichever lane has not merged yet.
        var contract = typeof(AZOA.WebAPI.Interfaces.Managers.IPersonUniquenessGate);

        typeof(PersonUniquenessGate).Should().BeAssignableTo(contract);
        contract.GetMethods().Select(method => method.Name)
            .Should().BeEquivalentTo("TryClaimAsync", "EvaluateAsync", "ReleaseAsync");

        foreach (var name in new[] { "TryClaimAsync", "EvaluateAsync" })
        {
            var method = contract.GetMethod(name)!;
            method.ReturnType.Should().Be(typeof(Task<AZOAResult<KycEligibilityDecision>>),
                "a refusal must be a two-valued decision, not an error result");
            method.GetParameters().Select(parameter => parameter.ParameterType)
                .Should().Equal(
                    typeof(PersonUniquenessRequirement),
                    typeof(Guid),
                    typeof(Guid?),
                    typeof(CancellationToken));
        }

        contract.GetMethod("ReleaseAsync")!.ReturnType.Should().Be(typeof(Task<AZOAResult<bool>>));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed record RefusalPath(string Name, AZOAResult<KycEligibilityDecision> Decision);

    /// <summary>
    /// Drives EVERY refusal path the gate has. A cause added without a message
    /// review shows up here as a second distinct sentence.
    /// </summary>
    private static async Task<IReadOnlyList<RefusalPath>> AllRefusalsAsync()
    {
        var tenantId = Guid.NewGuid();
        var holder = Guid.NewGuid();
        var other = Guid.NewGuid();
        var results = new List<RefusalPath>();

        // 1. No verified identity in scope.
        results.Add(new RefusalPath(
            "no-identity",
            await NewGate(new FakeIdentityStore(), new FakeClaimStore())
                .TryClaimAsync(Requirement(), Guid.NewGuid(), tenantId)));

        // 2. Identity erased.
        var erased = new FakeIdentityStore();
        erased.SeedPerson(tenantId, "person-a", "global-a", holder);
        erased.EraseAll();
        results.Add(new RefusalPath(
            "identity-erased",
            await NewGate(erased, new FakeClaimStore()).TryClaimAsync(Requirement(), holder, tenantId)));

        // 3. Person already claimed through another user.
        var shared = new FakeIdentityStore();
        shared.SeedPerson(tenantId, "person-a", "global-a", holder, other);
        var claimed = NewGate(shared, new FakeClaimStore());
        await claimed.TryClaimAsync(Requirement(), holder, tenantId);
        results.Add(new RefusalPath(
            "already-claimed",
            await claimed.TryClaimAsync(Requirement(), other, tenantId)));

        // 4. Deriver unconfigured.
        var configured = new FakeIdentityStore();
        configured.SeedPerson(tenantId, "person-a", "global-a", holder);
        results.Add(new RefusalPath(
            "unconfigured-secret",
            await NewGate(configured, new FakeClaimStore(), secret: null)
                .TryClaimAsync(Requirement(), holder, tenantId)));

        // 5. Unusable operation key.
        results.Add(new RefusalPath(
            "unusable-operation-key",
            await NewGate(configured, new FakeClaimStore())
                .TryClaimAsync(Requirement("not a key"), holder, tenantId)));

        // 6. Claim ledger unreachable.
        results.Add(new RefusalPath(
            "claim-store-fault",
            await NewGate(configured, new FakeClaimStore { FailEverything = true })
                .TryClaimAsync(Requirement(), holder, tenantId)));

        // 7. Identity store unreachable.
        var faulting = new FakeIdentityStore { FailEverything = true };
        results.Add(new RefusalPath(
            "identity-store-fault",
            await NewGate(faulting, new FakeClaimStore()).TryClaimAsync(Requirement(), holder, tenantId)));

        // 8. Preview of an already-claimed operation.
        results.Add(new RefusalPath(
            "preview-already-claimed",
            await claimed.EvaluateAsync(Requirement(), other, tenantId)));

        return results;
    }

    private static PersonUniquenessRequirement Requirement(
        string operationKey = OperationKey,
        PersonUniquenessScope scope = PersonUniquenessScope.Tenant)
        => new() { OperationKey = operationKey, Scope = scope };

    private static KycPersonKeyDeriver NewDeriver(string? secret = "default")
        => new(Options.Create(new KycSettings
        {
            Identity = new KycIdentitySettings
            {
                PersonKeySecret = secret == "default" ? new string('s', 48) : secret,
            },
        }));

    private static PersonUniquenessGate NewGate(
        IKycIdentityStore identities,
        IPersonOperationClaimStore claims,
        string? secret = "default")
        => new(
            identities,
            claims,
            NewDeriver(secret),
            NullLogger<PersonUniquenessGate>.Instance);

    /// <summary>Identity store stub exposing only what the gate consults.</summary>
    private sealed class FakeIdentityStore : IKycIdentityStore
    {
        private readonly List<KycIdentity> _rows = new();
        private int _getByUserCalls;

        public bool FailEverything { get; init; }

        public int GetByUserCallCount => Volatile.Read(ref _getByUserCalls);

        public void SeedPerson(
            Guid tenantId,
            string personKey,
            string globalPersonKey,
            params Guid[] userIds)
            => _rows.Add(new KycIdentity
            {
                Id = KycPersonKeyDeriver.RecordId(tenantId.ToString("N"), personKey),
                TenantScope = tenantId.ToString("N"),
                TenantId = tenantId.ToString("N"),
                PersonKey = personKey,
                GlobalPersonKey = globalPersonKey,
                UserIds = userIds.Select(id => id.ToString("N")).ToList(),
            });

        public void EraseAll()
        {
            foreach (var row in _rows)
                row.ErasedAt = DateTimeOffset.UtcNow;
        }

        public Task<AZOAResult<KycIdentity?>> GetByUserAsync(
            string tenantScope,
            string userId,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _getByUserCalls);
            if (FailEverything)
                return Task.FromResult(AZOAResult<KycIdentity?>.Failure("unavailable"));

            var found = _rows.FirstOrDefault(row =>
                row.TenantScope == tenantScope
                && (row.UserIds ?? []).Contains(userId, StringComparer.Ordinal));
            return Task.FromResult(new AZOAResult<KycIdentity?> { Result = found });
        }

        public Task<AZOAResult<KycIdentity>> BindUserAsync(KycIdentity candidate, CancellationToken ct = default)
            => throw new NotSupportedException("The uniqueness gate must not bind identities.");

        public Task<AZOAResult<KycIdentity>> GetByPersonKeyAsync(
            string tenantScope,
            string personKey,
            CancellationToken ct = default)
            => throw new NotSupportedException("The uniqueness gate resolves by USER, not by person key.");

        public Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetByGlobalPersonKeyAsync(
            string globalPersonKey,
            CancellationToken ct = default)
            => throw new NotSupportedException(
                "GetByGlobalPersonKeyAsync stays node-operator support/audit only: the "
                + "gate needs a claim ledger, not a person -> users fan-out.");

        public Task<AZOAResult<IReadOnlyList<KycIdentity>>> GetErasureDueAsync(
            DateTimeOffset asOf,
            int limit,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AZOAResult<KycIdentity>> EraseAsync(
            string recordId,
            string reason,
            DateTimeOffset erasedAt,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Claim ledger that mirrors the SurrealQL single-winner semantics: the winner
    /// is decided INSIDE the critical section and the caller only learns the outcome
    /// by reading the returned row back.
    /// </summary>
    private sealed class FakeClaimStore : IPersonOperationClaimStore
    {
        private readonly ConcurrentDictionary<string, PersonOperationClaim> _rows = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private int _tryClaimCalls;

        public bool FailEverything { get; init; }

        /// <summary>Forces the ledger to report a different holder, exercising the read-back.</summary>
        public string? ForceWinner { get; init; }

        /// <summary>Widens the race window so a preceding-read implementation loses.</summary>
        public TimeSpan ClaimDelay { get; init; }

        public int TryClaimCallCount => Volatile.Read(ref _tryClaimCalls);

        public IReadOnlyList<PersonOperationClaim> Rows => _rows.Values.ToList();

        public async Task<AZOAResult<PersonOperationClaim>> TryClaimAsync(
            PersonOperationClaim candidate,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _tryClaimCalls);
            if (FailEverything)
                return AZOAResult<PersonOperationClaim>.Failure("unavailable");

            if (ClaimDelay > TimeSpan.Zero)
                await Task.Delay(ClaimDelay, ct);

            lock (_gate)
            {
                if (!_rows.TryGetValue(candidate.Id, out var existing))
                {
                    existing = Clone(candidate);
                    existing.UserId = ForceWinner ?? candidate.UserId;
                    _rows[candidate.Id] = existing;
                    return AZOAResult<PersonOperationClaim>.Success(Clone(existing));
                }

                var free = string.IsNullOrWhiteSpace(existing.UserId) || existing.ReleasedAt is not null;
                var mine = string.Equals(existing.UserId, candidate.UserId, StringComparison.Ordinal);
                if (free || mine)
                {
                    if (free)
                        existing.ClaimedAt = candidate.ClaimedAt;
                    existing.UserId = ForceWinner ?? candidate.UserId;
                    existing.TenantId = candidate.TenantId;
                    existing.ReleasedAt = null;
                    existing.ModifiedDate = candidate.ModifiedDate;
                }

                return AZOAResult<PersonOperationClaim>.Success(Clone(existing));
            }
        }

        /// <summary>D39: every live (unreleased) claim in one uniqueness domain.</summary>
        public Task<AZOAResult<IReadOnlyList<PersonOperationClaim>>> ListLiveByOperationAsync(
            string uniquenessScope, string operationKey, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<IReadOnlyList<PersonOperationClaim>>
            {
                Result = _rows.Values
                    .Where(r => string.Equals(r.UniquenessScope, uniquenessScope, StringComparison.Ordinal)
                        && string.Equals(r.OperationKey, operationKey, StringComparison.Ordinal)
                        && r.ReleasedAt is null)
                    .ToList(),
            });

        public Task<AZOAResult<PersonOperationClaim?>> GetAsync(
            string uniquenessScope,
            string operationKey,
            string personKey,
            CancellationToken ct = default)
        {
            if (FailEverything)
                return Task.FromResult(AZOAResult<PersonOperationClaim?>.Failure("unavailable"));

            var id = PersonOperationClaim.RecordIdFor(uniquenessScope, operationKey, personKey);
            var found = _rows.TryGetValue(id, out var row) ? Clone(row) : null;
            return Task.FromResult(new AZOAResult<PersonOperationClaim?> { Result = found });
        }

        public Task<AZOAResult<bool>> ReleaseAsync(
            string uniquenessScope,
            string operationKey,
            string personKey,
            string userId,
            DateTimeOffset releasedAt,
            CancellationToken ct = default)
        {
            if (FailEverything)
                return Task.FromResult(AZOAResult<bool>.Failure("unavailable"));

            var id = PersonOperationClaim.RecordIdFor(uniquenessScope, operationKey, personKey);
            lock (_gate)
            {
                if (!_rows.TryGetValue(id, out var row)
                    || row.ReleasedAt is not null
                    || !string.Equals(row.UserId, userId, StringComparison.Ordinal))
                {
                    return Task.FromResult(AZOAResult<bool>.Success(false));
                }

                row.ReleasedAt = releasedAt;
                row.ModifiedDate = releasedAt;
                return Task.FromResult(AZOAResult<bool>.Success(true));
            }
        }

        private static PersonOperationClaim Clone(PersonOperationClaim claim) => new()
        {
            Id = claim.Id,
            UniquenessScope = claim.UniquenessScope,
            OperationKey = claim.OperationKey,
            PersonKey = claim.PersonKey,
            UserId = claim.UserId,
            TenantId = claim.TenantId,
            ClaimedAt = claim.ClaimedAt,
            ReleasedAt = claim.ReleasedAt,
            CreatedDate = claim.CreatedDate,
            ModifiedDate = claim.ModifiedDate,
        };
    }
}
