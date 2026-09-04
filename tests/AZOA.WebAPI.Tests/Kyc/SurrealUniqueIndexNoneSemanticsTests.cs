// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// AUTHORITATIVE record of SurrealDB's UNIQUE-index NONE semantics, and the schema
/// invariants that depend on them.
///
/// <para><b>The answer, verified empirically against SurrealDB v3.1.4 — the version
/// pinned in <c>docker-compose.dev.yml</c> and used by CI:</b></para>
/// <list type="number">
///   <item><description>A UNIQUE index does NOT collide NONE components. Two rows
///   whose indexed tuple differs only by a NONE both CREATE successfully. Reproduced
///   with a two-column <c>UNIQUE(a, b)</c> where <c>b</c> is <c>option&lt;string&gt;</c>
///   and omitted, and again with the real <c>hosted_session</c> schema and three
///   un-attached claims.</description></item>
///   <item><description>A non-NONE duplicate DOES collide, with
///   <c>Database index `…` already contains ['z', 's']</c> — so the index is doing
///   its job; only NONE is exempt.</description></item>
///   <item><description>Explicit SQL <c>NULL</c> is not reachable at all on these
///   columns. A SCHEMAFULL <c>option&lt;string&gt;</c> field refuses it outright:
///   <c>Couldn't coerce value for field `b`: Expected `none | string` but found
///   `NULL`</c>. The "NULL-equals-NULL collision semantics" phrasing carried by
///   <c>api_key.surql</c> and <c>avatar.surql</c> names a hazard that cannot arise
///   in this schema; <c>bridge_tx.surql</c>'s "NULL rows do NOT collide" is the
///   correct statement of the behaviour.</description></item>
/// </list>
///
/// <para>Consequence for the suspected node-wide-brick scenario: it does not exist.
/// One avatar holding an un-attached claim (<c>provider_session_id</c> NONE, which is
/// exactly what a failed <c>/begin</c> deliberately leaves behind for retry) cannot
/// make another avatar's <c>ClaimAdmissionAsync</c> CREATE fail. The unique index is
/// therefore RETAINED: it is a real guarantee that two AZOA sessions never bind one
/// vendor session, and the argument for removing it rested on a false premise.</para>
///
/// <para>These tests pin the schema-side consequences so the contradiction stops
/// costing time. The empirical run itself is not re-executed here — it needs a live
/// server — but the note it produced now lives on the POCO and travels into the
/// generated <c>.surql</c>, where the next reader will find it.</para>
/// </summary>
public sealed class SurrealUniqueIndexNoneSemanticsTests
{
    [Fact]
    public void HostedSession_KeepsItsProviderSessionUniqueIndex()
    {
        var index = IndexesOf<HostedSession>()
            .Single(item => item.Name == "hosted_session_provider_session");

        index.Unique.Should().BeTrue(
            "NONE does not collide on v3.1.4, so many un-attached claims coexist and "
            + "the index costs nothing while still preventing two AZOA sessions from "
            + "binding one vendor session");
        index.Fields.Should().ContainInOrder("flow_kind", "provider_key", "provider_session_id");
    }

    [Fact]
    public void EveryUniqueIndexOverANullableColumn_CarriesTheB3NullableCollisionNote()
    {
        // The repo convention: a UNIQUE index spanning an option<...> column must say
        // in the schema why NONE rows are safe. Every other table in this tree does;
        // the two KYC tables added here were the exceptions.
        AssertB3Note<HostedSession>();
        AssertB3Note<KycScreeningMonitor>();
    }

    [Fact]
    public void ScreeningMonitor_UniquenessCarriesATenantComponent()
    {
        var index = IndexesOf<KycScreeningMonitor>()
            .Single(item => item.Name == "kyc_screening_monitor_query");

        index.Unique.Should().BeTrue();
        index.Fields.Should().ContainInOrder(
            "tenant_scope", "query_full_name", "query_birthday_time_from");
        index.Fields.First().Should().Be("tenant_scope",
            "mirrors kyc_identity's (tenant_scope, person_key); a name-and-birthday key "
            + "without a tenant is shared across tenants by nature");
    }

    [Fact]
    public void ScreeningMonitor_ProviderIndexIsNotUnique()
        => IndexesOf<KycScreeningMonitor>()
            .Single(item => item.Name == "kyc_screening_monitor_provider")
            .Unique.Should().BeFalse(
                "one vendor company key serves the whole node, so ONE upstream monitor "
                + "legitimately backs one local row per tenant");

    private static void AssertB3Note<T>()
    {
        var notes = typeof(T)
            .GetCustomAttributes(typeof(SurrealForge.Client.Schema.SurrealNoteAttribute), false)
            .Cast<SurrealForge.Client.Schema.SurrealNoteAttribute>()
            .Select(note => note.Text)
            .ToList();

        notes.Should().Contain(
            note => note.Contains("B3", StringComparison.Ordinal),
            $"{typeof(T).Name} has a UNIQUE index over a nullable column and every "
            + "other such table in this tree records a B3 nullable-collision review");
    }

    private static IEnumerable<(string Name, bool Unique, IReadOnlyList<string> Fields)> IndexesOf<T>()
        => typeof(T)
            .GetCustomAttributes(typeof(SurrealForge.Client.Schema.IndexAttribute), false)
            .Cast<SurrealForge.Client.Schema.IndexAttribute>()
            .Select(index => (
                index.Name,
                index.Unique,
                (IReadOnlyList<string>)(index.Fields ?? [])));
}
