// SPDX-License-Identifier: UNLICENSED
// AZOA.WebAPI.Tests -- focused unit coverage for the field/table DDL clauses
// added to the C#-first schema authoring surface:
//   [Required], [ReadOnly], [Value], [Comment] (DEFINE FIELD)
//   [Permissions], [ChangeFeed]                 (DEFINE TABLE)
//
// These tests scan ad-hoc POCOs declared inline (NOT the production models
// under Persistence/SurrealDb/Models), so they exercise the scanner + emitter
// without touching the committed .surql goldens that
// AttributePocoByteEquivalenceTests guards.

using System;
using FluentAssertions;
using SurrealForge.Client.Schema;
using SurrealForge.Schema.Generator;
using Xunit;

namespace AZOA.WebAPI.Tests.Persistence.SurrealDb
{
    public class AttributeSchemaScannerNewClausesTests
    {
        private static string EmitField(Type poco)
            => SurqlEmitter.Emit(AttributeSchemaScanner.ScanType(poco));

        // ─── [Required] ──────────────────────────────────────────────────

        [SurrealTable("req_default_nullable")]
        private sealed class RequiredOverridesNullablePoco
        {
            [Id] public string Id { get; set; } = "";
            // int? would infer option<int>; [Required] forces NOT NULL.
            [Column(Order = 1)] [Required] public int? Score { get; set; }
        }

        [Fact]
        public void Required_forces_not_null_on_nullable_value_type()
        {
            var surql = EmitField(typeof(RequiredOverridesNullablePoco));
            surql.Should().Contain("score ON TABLE req_default_nullable TYPE int");
            surql.Should().NotContain("option<int>");
        }

        [SurrealTable("opt_infers")]
        private sealed class InfersOptionWithoutRequiredPoco
        {
            [Id] public string Id { get; set; } = "";
            [Column(Order = 1)] public int? Score { get; set; }
        }

        [Fact]
        public void Nullable_value_type_still_infers_option_without_Required()
        {
            // Guards that [Required] is the ONLY thing suppressing the wrap --
            // the default inference must be unchanged.
            var surql = EmitField(typeof(InfersOptionWithoutRequiredPoco));
            surql.Should().Contain("TYPE option<int>");
        }

        [SurrealTable("opt_req_conflict")]
        private sealed class OptionalAndRequiredConflictPoco
        {
            [Id] public string Id { get; set; } = "";
            [Column(Order = 1)] [Optional] [Required] public int Score { get; set; }
        }

        [Fact]
        public void Optional_and_Required_together_throws()
        {
            Action act = () => EmitField(typeof(OptionalAndRequiredConflictPoco));
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*mutually exclusive*");
        }

        // ─── [ReadOnly] ──────────────────────────────────────────────────

        [SurrealTable("ro_table")]
        private sealed class ReadOnlyPoco
        {
            [Id] public string Id { get; set; } = "";
            [Column(Order = 1)] [ReadOnly] public string CreatedAt { get; set; } = "";
        }

        [Fact]
        public void ReadOnly_emits_readonly_modifier()
        {
            var surql = EmitField(typeof(ReadOnlyPoco));
            surql.Should().Contain("created_at ON TABLE ro_table TYPE string");
            surql.Should().Contain("READONLY");
        }

        // ─── [Value] ─────────────────────────────────────────────────────

        [SurrealTable("val_table")]
        private sealed class ValuePoco
        {
            [Id] public string Id { get; set; } = "";
            [Column(Order = 1)] [Value("time::now()")] public string Touched { get; set; } = "";
        }

        [Fact]
        public void Value_emits_computed_value_clause()
        {
            var surql = EmitField(typeof(ValuePoco));
            surql.Should().Contain("VALUE time::now()");
        }

        // ─── [Comment] ───────────────────────────────────────────────────

        [SurrealTable("cmt_table")]
        private sealed class CommentPoco
        {
            [Id] public string Id { get; set; } = "";
            [Column(Order = 1)] [Comment("the user's display name")] public string Name { get; set; } = "";
        }

        [Fact]
        public void Comment_emits_quoted_comment_clause()
        {
            var surql = EmitField(typeof(CommentPoco));
            surql.Should().Contain("COMMENT \"the user's display name\"");
        }

        // ─── [ChangeFeed] ────────────────────────────────────────────────

        [SurrealTable("cf_table")]
        [ChangeFeed("3d")]
        private sealed class ChangeFeedPoco
        {
            [Id] public string Id { get; set; } = "";
            [Column(Order = 1)] public string Name { get; set; } = "";
        }

        [Fact]
        public void ChangeFeed_emits_changefeed_clause_on_table()
        {
            var surql = EmitField(typeof(ChangeFeedPoco));
            surql.Should().Contain("cf_table");
            surql.Should().Contain("CHANGEFEED 3d");
            surql.Should().NotContain("INCLUDE ORIGINAL");
        }

        [SurrealTable("cf_orig_table")]
        [ChangeFeed("1h", IncludeOriginal = true)]
        private sealed class ChangeFeedWithOriginalPoco
        {
            [Id] public string Id { get; set; } = "";
            [Column(Order = 1)] public string Name { get; set; } = "";
        }

        [Fact]
        public void ChangeFeed_includes_original_when_requested()
        {
            var surql = EmitField(typeof(ChangeFeedWithOriginalPoco));
            surql.Should().Contain("CHANGEFEED 1h INCLUDE ORIGINAL");
        }

        // ─── [Permissions] ───────────────────────────────────────────────

        [SurrealTable("perm_full_table")]
        [Permissions(Full = true)]
        private sealed class PermissionsFullPoco
        {
            [Id] public string Id { get; set; } = "";
            [Column(Order = 1)] public string Name { get; set; } = "";
        }

        [Fact]
        public void Permissions_full_emits_full_shorthand()
        {
            var surql = EmitField(typeof(PermissionsFullPoco));
            surql.Should().Contain("PERMISSIONS FULL");
        }

        [SurrealTable("perm_ops_table")]
        [Permissions(Select = "$auth.id = id", Create = "NONE", Update = "$auth.id = id", Delete = "NONE")]
        private sealed class PermissionsPerOpPoco
        {
            [Id] public string Id { get; set; } = "";
            [Column(Order = 1)] public string Name { get; set; } = "";
        }

        [Fact]
        public void Permissions_per_operation_emits_for_clauses_in_stable_order()
        {
            var surql = EmitField(typeof(PermissionsPerOpPoco));
            surql.Should().Contain(
                "PERMISSIONS FOR select $auth.id = id FOR create NONE FOR update $auth.id = id FOR delete NONE");
        }

        [SurrealTable("perm_partial_table")]
        [Permissions(Select = "FULL")]
        private sealed class PermissionsPartialPoco
        {
            [Id] public string Id { get; set; } = "";
            [Column(Order = 1)] public string Name { get; set; } = "";
        }

        [Fact]
        public void Permissions_omits_unspecified_operations()
        {
            var surql = EmitField(typeof(PermissionsPartialPoco));
            surql.Should().Contain("PERMISSIONS FOR select FULL");
            surql.Should().NotContain("FOR create");
            surql.Should().NotContain("FOR update");
            surql.Should().NotContain("FOR delete");
        }
    }
}
