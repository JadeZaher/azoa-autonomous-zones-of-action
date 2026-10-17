using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Users;

public sealed class SurrealGeneratedStatementScopeTests
{
    [Fact]
    public void Indexed_batch_scopes_keep_each_record_parameter_distinct()
    {
        var first = SurrealGeneratedStatementScope.Apply(SurrealQuery.Of("RETURN $id").WithParam("id", "first"), "grant_0");
        var second = SurrealGeneratedStatementScope.Apply(SurrealQuery.Of("RETURN $id").WithParam("id", "second"), "grant_1");
        var batch = SurrealQuery.Combine(first, second);
        batch.Params["grant_0_id"].Should().Be("first");
        batch.Params["grant_1_id"].Should().Be("second");
    }

    [Fact]
    public void CapturedTypedMutationRetainsItsScopedParameters()
    {
        var typed = SurrealWriter.UpdateOnly<FungibleCreationOperation>("operation")
            .Where(row => row.Version == 7).Set(row => row.State, "Completed").Build();
        var scoped = SurrealGeneratedStatementScope.Apply(typed, "attachment");
        var captured = SurrealGeneratedStatementScope.Assign(typed, "attachment", "_attachment_result");
        captured.Sql.Should().Be($"LET $_attachment_result = ({scoped.Sql})");
        captured.Params.Should().BeEquivalentTo(scoped.Params);
        Action invalid = () => SurrealGeneratedStatementScope.Assign(typed, "attachment", "x; RETURN true");
        invalid.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TwoTypedWritersRetainTheirOwnTableRecordAndFields()
    {
        var authority = SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new NftAssetAuthority
        { Id = "authority", NftId = "one", AssetId = "42", ChainType = "Algorand" }), "authority");
        var ownership = SurrealGeneratedStatementScope.Apply(SurrealWriter.Upsert(new NftOwnership
        { Id = "ownership", TokenId = "99", ChainType = "Ethereum" }), "ownership");
        var batch = SurrealQuery.Combine(authority, ownership);
        batch.Params["authority__t"].Should().Be("nft_asset_authority");
        batch.Params["ownership__t"].Should().Be("nft_ownership");
        batch.Params["authority__id"].Should().Be("authority");
        batch.Params["ownership__id"].Should().Be("ownership");
        batch.Params["authority__f_chain_type"].Should().Be("Algorand");
        batch.Params["ownership__f_chain_type"].Should().Be("Ethereum");
    }

    [Theory]
    [InlineData("RETURN [$p, '$p', 'escaped\\\' $p', 'doubled'' $p']", "RETURN [$scoped_p, '$p', 'escaped\\\' $p', 'doubled'' $p']")]
    [InlineData("RETURN [$p, \"$p\", `double`` $p`]", "RETURN [$scoped_p, \"$p\", `double`` $p`]")]
    public void QuotedContentIsNotRenamed(string sql, string expected)
    {
        var result = SurrealGeneratedStatementScope.Apply(SurrealQuery.Of(sql).WithParam("p", "value"), "scoped");
        result.Sql.Should().Be(expected);
        result.Params.Should().ContainKey("scoped_p");
    }

    [Theory]
    [InlineData("RETURN $missing", "ok")]
    [InlineData("RETURN $p -- comment", "ok")]
    [InlineData("RETURN $p /* comment */", "ok")]
    [InlineData("RETURN '$p", "ok")]
    [InlineData("RETURN $p", "bad-scope")]
    public void RejectsUntrustedStatementShapes(string sql, string scope)
    {
        var query = SurrealQuery.Of(sql).WithParam("p", "value");
        Action act = () => SurrealGeneratedStatementScope.Apply(query, scope);
        act.Should().Throw<ArgumentException>();
    }
}
