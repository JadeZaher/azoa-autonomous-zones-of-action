// SPDX-License-Identifier: UNLICENSED
// Executable guard for the "node-operator-only" claim on the identity binding.
//
// This repo has leaked cross-tenant data through a GENERIC read surface more than
// once, so the exclusion is asserted rather than assumed. These tests fail the
// moment someone adds an identity table to a generic reader, which is exactly the
// change that would silently expose "this person also exists under another tenant".

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Mcp;
using AZOA.WebAPI.Mcp.Tools;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class KycIdentityReadSurfaceExclusionTests
{
    /// <summary>Tables that must never be reachable through a generic consumer reader.</summary>
    public static TheoryData<string> OperatorOnlyTables()
    {
        var data = new TheoryData<string>();
        data.Add(KycIdentity.SchemaNameConst);
        data.Add(KycScreeningResult.SchemaNameConst);
        data.Add(KycScreeningMonitor.SchemaNameConst);
        data.Add(KycScreeningPollState.SchemaNameConst);
        data.Add(HostedSession.SchemaNameConst);
        return data;
    }

    [Theory]
    [MemberData(nameof(OperatorOnlyTables))]
    public async Task McpUserScopedQuery_RejectsOperatorOnlyTables(string table)
    {
        var tool = new UserScopedQueryTool();
        var args = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(new { table }));
        var context = new ToolCallContext(
            Guid.NewGuid(),
            Moq.Mock.Of<SurrealForge.Client.Query.ISurrealExecutor>(),
            Moq.Mock.Of<IServiceProvider>());

        var response = await tool.ExecuteAsync(context, args, CancellationToken.None);

        response.TryGetProperty("error", out var error).Should().BeTrue(
            $"the MCP generic reader must refuse '{table}' outright");
        error.GetString().Should().Be("table_not_allowed");
    }

    [Theory]
    [MemberData(nameof(OperatorOnlyTables))]
    public void McpInputSchemaEnum_DoesNotAdvertiseOperatorOnlyTables(string table)
    {
        var tool = new UserScopedQueryTool();

        var advertised = tool.InputSchema
            .GetProperty("properties")
            .GetProperty("table")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToList();

        advertised.Should().NotContain(table);
    }

    /// <summary>
    /// The search surface is a CLOSED flags enum over five entity types. A new
    /// identity entity would have to be added here to become searchable, and this
    /// assertion is what makes that addition a deliberate, visible act.
    /// </summary>
    [Fact]
    public void SearchableEntityType_RemainsAClosedSetWithNoIdentityMember()
    {
        var members = Enum.GetNames<SearchableEntityType>();

        members.Should().BeEquivalentTo(
            "None", "User", "Holon", "Wallet", "BlockchainOperation", "ActionGraphBundle", "All");
        members.Should().NotContain(name =>
            name.Contains("Identity", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Kyc", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Screening", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Person", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// HolonQuery is structurally holon-shaped: it exposes no table selector at all,
    /// so it cannot be pointed at another table even by a caller who tries.
    /// </summary>
    [Fact]
    public void HolonQueryRequest_ExposesNoTableSelectorAndNoIdentityField()
    {
        var properties = typeof(HolonQueryRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        properties.Should().NotContain(name =>
            name.Contains("Table", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Schema", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Entity", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PersonKey", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The link from a person to their users is what would reveal cross-tenant
    /// existence. It must not be projected by any consumer-facing DTO, so no such
    /// DTO may carry the field at all.
    /// </summary>
    [Fact]
    public void NoConsumerFacingModelProjectsThePersonToUserLink()
    {
        var assembly = typeof(KycIdentity).Assembly;
        var leaking = assembly.GetTypes()
            .Where(type => type.IsClass
                && type.Namespace is not null
                && (type.Namespace.StartsWith("AZOA.WebAPI.Models", StringComparison.Ordinal)
                    || type.Namespace.StartsWith("AZOA.WebAPI.Controllers", StringComparison.Ordinal)))
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => new { Type = type, Property = property }))
            .Where(entry =>
                entry.Property.Name is "PersonKey" or "GlobalPersonKey" or "UserIds"
                || entry.Property.Name.Contains("PersonKey", StringComparison.Ordinal))
            .Select(entry => $"{entry.Type.FullName}.{entry.Property.Name}")
            .ToList();

        leaking.Should().BeEmpty(
            "person keys and the person->users link are node-operator-only; a "
            + "response DTO carrying either would expose cross-tenant existence");
    }
}
