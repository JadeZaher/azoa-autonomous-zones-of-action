// SPDX-License-Identifier: UNLICENSED

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IEcosystemStore"/> (final-hardening-cutover D2).
/// Maps between the decorated <see cref="Ecosystem"/> / <see cref="EcosystemNode"/>
/// POCOs and inline wire POCOs (record-link FK encoding via
/// <see cref="SurrealLink"/>), mirroring <see cref="SurrealConsentGrantStore"/>.
///
/// <para><b>No-throw.</b> Every method captures exceptions into an
/// <see cref="AZOAResult{T}"/> rather than throwing.</para>
/// </summary>
/// <remarks>See <c>Providers/Stores/Surreal/AGENTS.md</c> §ecosystem-tree.</remarks>
public sealed class SurrealEcosystemStore : IEcosystemStore
{
    private const string EcosystemTable = "ecosystem";
    private const string NodeTable = "ecosystem_node";

    private readonly ISurrealExecutor _executor;

    public SurrealEcosystemStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    // ── Ecosystem root ────────────────────────────────────────────────────────

    public async Task<AZOAResult<Ecosystem>> GetByStarOdkAsync(Guid starOdkId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE star_odk_id = $_star LIMIT 1")
                .WithParam("_t", EcosystemTable)
                .WithParam("_star", SurrealLink.ToLink("star_odk", SurrealId.ToSurrealId(starOdkId)));
            var row = await _executor.QuerySingleAsync<EcosystemPoco>(q, ct);
            return new AZOAResult<Ecosystem>
            {
                IsError = false,
                Message = row == null ? "No ecosystem for STAR ODK." : "Success",
                Result = row == null ? null : ToDecorated(row),
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<Ecosystem>().CaptureException(ex, $"SurrealEcosystemStore.GetByStarOdkAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<Ecosystem>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(EcosystemTable, SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<EcosystemPoco>(q, ct);
            return new AZOAResult<Ecosystem>
            {
                IsError = row == null,
                Message = row == null ? "Ecosystem not found." : "Success",
                Result = row == null ? null : ToDecorated(row),
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<Ecosystem>().CaptureException(ex, $"SurrealEcosystemStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<Ecosystem>> UpsertAsync(Ecosystem ecosystem, CancellationToken ct = default)
    {
        try
        {
            var poco = FromDecorated(ecosystem);
            var q = SurrealWriter.Upsert(poco);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            var saved = resp.GetValues<EcosystemPoco>(0).FirstOrDefault();
            return new AZOAResult<Ecosystem> { Result = saved is not null ? ToDecorated(saved) : ecosystem, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<Ecosystem>().CaptureException(ex, $"SurrealEcosystemStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid ecosystemId, CancellationToken ct = default)
    {
        try
        {
            // Delete children first, then the root. Node -> ecosystem FK means orphan
            // rows would otherwise dangle; a single DELETE ... WHERE handles the fan-out.
            var delNodes = SurrealQuery
                .Of("DELETE type::table($_t) WHERE ecosystem_id = $_eco")
                .WithParam("_t", NodeTable)
                .WithParam("_eco", SurrealLink.ToLink("ecosystem", SurrealId.ToSurrealId(ecosystemId)));
            await _executor.ExecuteAsync(delNodes, ct);

            var delRoot = SurrealQuery.DeleteById(EcosystemTable, SurrealId.ToSurrealId(ecosystemId));
            await _executor.ExecuteAsync(delRoot, ct);
            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealEcosystemStore.DeleteAsync failed: {ex.Message}");
        }
    }

    // ── Nodes ─────────────────────────────────────────────────────────────────

    public async Task<AZOAResult<IEnumerable<EcosystemNode>>> GetNodesAsync(Guid ecosystemId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE ecosystem_id = $_eco")
                .WithParam("_t", NodeTable)
                .WithParam("_eco", SurrealLink.ToLink("ecosystem", SurrealId.ToSurrealId(ecosystemId)));
            var rows = await _executor.QueryAsync<EcosystemNodePoco>(q, ct);
            return new AZOAResult<IEnumerable<EcosystemNode>>
            {
                Result = rows.Select(ToDecorated).ToList(),
                Message = "Success",
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<EcosystemNode>>().CaptureException(ex, $"SurrealEcosystemStore.GetNodesAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<EcosystemNode>> UpsertNodeAsync(EcosystemNode node, CancellationToken ct = default)
    {
        try
        {
            var poco = FromDecorated(node);
            var q = SurrealWriter.Upsert(poco);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            var saved = resp.GetValues<EcosystemNodePoco>(0).FirstOrDefault();
            return new AZOAResult<EcosystemNode> { Result = saved is not null ? ToDecorated(saved) : node, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<EcosystemNode>().CaptureException(ex, $"SurrealEcosystemStore.UpsertNodeAsync failed: {ex.Message}");
        }
    }

    // ── Mapping (decorated POCO <-> inline wire POCO) ──────────────────────────

    private static EcosystemPoco FromDecorated(Ecosystem e) => new()
    {
        Id = string.IsNullOrEmpty(e.Id) ? SurrealId.ToSurrealId(Guid.NewGuid()) : e.Id,
        Name = e.Name,
        Description = e.Description,
        StarOdkId = SurrealLink.ToLink("star_odk", e.StarOdkId) ?? string.Empty,
        AvatarId = SurrealLink.ToLink("avatar", e.AvatarId) ?? string.Empty,
        TargetChain = e.TargetChain,
        CreatedDate = e.CreatedDate == default ? DateTimeOffset.UtcNow : e.CreatedDate,
        ModifiedDate = e.ModifiedDate,
    };

    private static Ecosystem ToDecorated(EcosystemPoco p) => new()
    {
        Id = SurrealLink.FromLink(p.Id) ?? p.Id,
        Name = p.Name,
        Description = p.Description,
        StarOdkId = SurrealLink.FromLink(p.StarOdkId) ?? p.StarOdkId,
        AvatarId = SurrealLink.FromLink(p.AvatarId) ?? p.AvatarId,
        TargetChain = p.TargetChain,
        CreatedDate = p.CreatedDate,
        ModifiedDate = p.ModifiedDate,
    };

    private static EcosystemNodePoco FromDecorated(EcosystemNode n) => new()
    {
        Id = string.IsNullOrEmpty(n.Id) ? SurrealId.ToSurrealId(Guid.NewGuid()) : n.Id,
        EcosystemId = SurrealLink.ToLink("ecosystem", n.EcosystemId) ?? string.Empty,
        ParentNodeId = string.IsNullOrEmpty(n.ParentNodeId) ? null : SurrealLink.ToLink("ecosystem_node", n.ParentNodeId),
        RefKind = n.RefKind.ToString(),
        RefId = n.RefId,
        Label = n.Label,
        CreatedDate = n.CreatedDate == default ? DateTimeOffset.UtcNow : n.CreatedDate,
    };

    private static EcosystemNode ToDecorated(EcosystemNodePoco p) => new()
    {
        Id = SurrealLink.FromLink(p.Id) ?? p.Id,
        EcosystemId = SurrealLink.FromLink(p.EcosystemId) ?? p.EcosystemId,
        ParentNodeId = string.IsNullOrEmpty(p.ParentNodeId) ? null : (SurrealLink.FromLink(p.ParentNodeId) ?? p.ParentNodeId),
        RefKind = Enum.TryParse<EcosystemNode.RefKindValue>(p.RefKind, ignoreCase: true, out var k) ? k : EcosystemNode.RefKindValue.DappSeries,
        RefId = p.RefId,
        Label = p.Label,
        CreatedDate = p.CreatedDate,
    };

    // ── Inline wire POCOs (record-link FK columns as strings) ──────────────────

    private sealed class EcosystemPoco : ISurrealRecord
    {
        public string SchemaName => EcosystemTable;

        [JsonPropertyName("id")]            public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")]          public string Name { get; set; } = string.Empty;
        [JsonPropertyName("description")]   public string? Description { get; set; }
        [JsonPropertyName("star_odk_id")]   public string StarOdkId { get; set; } = string.Empty;
        [JsonPropertyName("avatar_id")]     public string AvatarId { get; set; } = string.Empty;
        [JsonPropertyName("target_chain")]  public string? TargetChain { get; set; }
        [JsonPropertyName("created_date")]  public DateTimeOffset CreatedDate { get; set; }
        [JsonPropertyName("modified_date")] public DateTimeOffset? ModifiedDate { get; set; }
    }

    private sealed class EcosystemNodePoco : ISurrealRecord
    {
        public string SchemaName => NodeTable;

        [JsonPropertyName("id")]             public string Id { get; set; } = string.Empty;
        [JsonPropertyName("ecosystem_id")]   public string EcosystemId { get; set; } = string.Empty;
        [JsonPropertyName("parent_node_id")] public string? ParentNodeId { get; set; }
        [JsonPropertyName("ref_kind")]       public string RefKind { get; set; } = "DappSeries";
        [JsonPropertyName("ref_id")]         public string RefId { get; set; } = string.Empty;
        [JsonPropertyName("label")]          public string? Label { get; set; }
        [JsonPropertyName("created_date")]   public DateTimeOffset CreatedDate { get; set; }
    }
}
