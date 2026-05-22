// SPDX-License-Identifier: UNLICENSED
// One test per row of the CSharpTypeMapper deterministic table. The Mermaid
// type token in / C# type-ref out contract is the source of truth for the
// generated POCO field shapes; mis-mapping silently breaks every consumer of
// the source generator.

using FluentAssertions;
using Oasis.SurrealDb.SourceGen;

namespace Oasis.SurrealDb.SourceGen.Tests;

public class CSharpTypeMapperTests
{
    [Fact]
    public void String_maps_to_string()
    {
        var r = CSharpTypeMapper.Map("string");
        r.TypeName.Should().Be("string");
        r.IsNullable.Should().BeFalse();
        r.ToPropertyType().Should().Be("string");
    }

    [Fact]
    public void Int_maps_to_long()
    {
        var r = CSharpTypeMapper.Map("int");
        r.TypeName.Should().Be("long");
        r.IsNullable.Should().BeFalse();
    }

    [Fact]
    public void Decimal_maps_to_decimal()
    {
        var r = CSharpTypeMapper.Map("decimal");
        r.TypeName.Should().Be("decimal");
    }

    [Fact]
    public void Datetime_maps_to_DateTimeOffset()
    {
        var r = CSharpTypeMapper.Map("datetime");
        r.TypeName.Should().Be("global::System.DateTimeOffset");
    }

    [Fact]
    public void Duration_maps_to_TimeSpan()
    {
        var r = CSharpTypeMapper.Map("duration");
        r.TypeName.Should().Be("global::System.TimeSpan");
    }

    [Fact]
    public void Bool_maps_to_bool()
    {
        var r = CSharpTypeMapper.Map("bool");
        r.TypeName.Should().Be("bool");
    }

    [Fact]
    public void Object_maps_to_JsonElement()
    {
        var r = CSharpTypeMapper.Map("object");
        r.TypeName.Should().Be("global::System.Text.Json.JsonElement");
    }

    [Fact]
    public void Record_of_T_maps_to_RecordId_of_T_PascalCased()
    {
        var r = CSharpTypeMapper.Map("record<wallet>");
        r.TypeName.Should().Be("global::Oasis.SurrealDb.Client.RecordId<Wallet>");
    }

    [Fact]
    public void Record_of_snake_case_T_PascalCases_the_table_name()
    {
        var r = CSharpTypeMapper.Map("record<bridge_tx>");
        r.TypeName.Should().Be("global::Oasis.SurrealDb.Client.RecordId<BridgeTx>");
    }

    [Fact]
    public void Array_of_string_maps_to_IReadOnlyList_of_string()
    {
        var r = CSharpTypeMapper.Map("array<string>");
        r.TypeName.Should().Be("global::System.Collections.Generic.IReadOnlyList<string>");
    }

    [Fact]
    public void Array_of_int_maps_to_IReadOnlyList_of_long()
    {
        var r = CSharpTypeMapper.Map("array<int>");
        r.TypeName.Should().Be("global::System.Collections.Generic.IReadOnlyList<long>");
    }

    [Fact]
    public void Option_of_string_marks_nullable()
    {
        var r = CSharpTypeMapper.Map("option<string>");
        r.TypeName.Should().Be("string");
        r.IsNullable.Should().BeTrue();
        r.ToPropertyType().Should().Be("string?");
    }

    [Fact]
    public void Option_of_int_marks_nullable_long()
    {
        var r = CSharpTypeMapper.Map("option<int>");
        r.TypeName.Should().Be("long");
        r.IsNullable.Should().BeTrue();
        r.ToPropertyType().Should().Be("long?");
    }

    [Fact]
    public void Option_of_datetime_marks_nullable()
    {
        var r = CSharpTypeMapper.Map("option<datetime>");
        r.IsNullable.Should().BeTrue();
        r.ToPropertyType().Should().Be("global::System.DateTimeOffset?");
    }

    [Fact]
    public void Unknown_type_throws_with_descriptive_message()
    {
        var act = () => CSharpTypeMapper.Map("uuid");
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*uuid*")
            .WithMessage("*record<T>*");
    }

    [Theory]
    [InlineData("avatar_id", "AvatarId")]
    [InlineData("wallet", "Wallet")]
    [InlineData("bridge_tx", "BridgeTx")]
    [InlineData("idempotency_key_store", "IdempotencyKeyStore")]
    [InlineData("nft_ownership", "NftOwnership")]
    [InlineData("a", "A")]
    [InlineData("a_b_c_d", "ABCD")]
    public void ToPascalCase_collapses_snake_to_PascalCase(string snake, string expected)
    {
        CSharpTypeMapper.ToPascalCase(snake).Should().Be(expected);
    }
}
