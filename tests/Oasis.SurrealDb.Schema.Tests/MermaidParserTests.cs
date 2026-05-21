// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Schema.Tests -- Mermaid parser + annotation DSL behavior.
//
// Covers the strict-namespacing contract from plan.md task 20:
//   - Known directives parse + attach to the correct AST node.
//   - Unknown @surreal.* directives fail with file:line:col.
//   - Required arguments are enforced.
//   - Orphan annotations at end-of-document fail.

using System.Linq;
using FluentAssertions;
using Oasis.SurrealDb.Schema.Mermaid;

namespace Oasis.SurrealDb.Schema.Tests
{
    public class MermaidParserTests
    {
        [Fact]
        public void Parses_minimal_entity_with_schemafull_annotation()
        {
            var src = @"erDiagram
    %% @surreal.schemafull
    foo {
        string id
    }
";
            var model = MermaidParser.Parse(src, "in-memory");
            model.Entities.Should().HaveCount(1);
            model.Entities[0].Name.Should().Be("foo");
            model.Entities[0].Annotations.Should().ContainSingle(a => a.Directive == "schemafull");
            model.Entities[0].Attributes.Should().ContainSingle(a => a.Name == "id" && a.Type == "string");
        }

        [Fact]
        public void Unknown_surreal_directive_throws_with_file_line_col()
        {
            var src = @"erDiagram
    %% @surreal.nonsense
    foo {
        string id
    }
";
            var act = () => MermaidParser.Parse(src, "test.mermaid");
            act.Should().Throw<MermaidParseException>()
                .Where(ex => ex.File == "test.mermaid" && ex.Line == 2 && ex.Diagnostic.Contains("unknown @surreal.nonsense"));
        }

        [Fact]
        public void Attribute_assert_annotation_attaches_to_following_attribute()
        {
            var src = @"erDiagram
    foo {
        %% @surreal.assert ""$value != NONE""
        string id
    }
";
            var model = MermaidParser.Parse(src, "in-memory");
            var attr = model.Entities[0].Attributes.Single();
            attr.Name.Should().Be("id");
            attr.Annotations.Should().ContainSingle(a => a.Directive == "assert");
        }

        [Fact]
        public void Index_annotation_attaches_to_entity_not_attribute()
        {
            var src = @"erDiagram
    foo {
        string a
        string b
        %% @surreal.index unique fields=[a,b] name=foo_ab
    }
";
            var model = MermaidParser.Parse(src, "in-memory");
            var entity = model.Entities.Single();
            entity.Indexes.Should().ContainSingle(i => i.Name == "foo_ab" && i.IsUnique);
            entity.Indexes[0].Fields.Should().Equal("a", "b");
        }

        [Fact]
        public void Index_missing_fields_arg_throws()
        {
            var src = @"erDiagram
    foo {
        string a
        %% @surreal.index name=foo_a
    }
";
            var act = () => MermaidParser.Parse(src, "test.mermaid");
            act.Should().Throw<MermaidParseException>()
                .Where(ex => ex.Diagnostic.Contains("fields=[a,b,...]"));
        }

        [Fact]
        public void Index_missing_name_arg_throws()
        {
            var src = @"erDiagram
    foo {
        string a
        %% @surreal.index fields=[a]
    }
";
            var act = () => MermaidParser.Parse(src, "test.mermaid");
            act.Should().Throw<MermaidParseException>()
                .Where(ex => ex.Diagnostic.Contains("name=<identifier>"));
        }

        [Fact]
        public void Orphan_annotation_at_eof_throws()
        {
            var src = @"erDiagram
    foo {
        string id
    }
    %% @surreal.schemafull
";
            var act = () => MermaidParser.Parse(src, "test.mermaid");
            act.Should().Throw<MermaidParseException>()
                .Where(ex => ex.Diagnostic.Contains("orphan"));
        }

        [Fact]
        public void Missing_erDiagram_header_throws()
        {
            var src = @"foo {
        string id
    }";
            var act = () => MermaidParser.Parse(src, "test.mermaid");
            act.Should().Throw<MermaidParseException>()
                .Where(ex => ex.Diagnostic.Contains("erDiagram"));
        }

        [Fact]
        public void Type_token_supports_angle_bracket_generics()
        {
            var src = @"erDiagram
    foo {
        option<string> name
        option<int> count
    }
";
            var model = MermaidParser.Parse(src, "in-memory");
            model.Entities[0].Attributes.Select(a => a.Type).Should().Equal("option<string>", "option<int>");
        }

        [Fact]
        public void Parses_relationship_with_cardinality_and_label()
        {
            var src = @"erDiagram
    wallet ||--o{ holon : owns
";
            var model = MermaidParser.Parse(src, "in-memory");
            model.Relationships.Should().HaveCount(1);
            var rel = model.Relationships[0];
            rel.FromEntity.Should().Be("wallet");
            rel.ToEntity.Should().Be("holon");
            rel.Cardinality.Should().Be("||--o{");
            rel.Label.Should().Be("owns");
        }

        [Fact]
        public void Plain_comment_lines_are_ignored()
        {
            var src = @"erDiagram
    %% just a doc comment, not an annotation
    foo {
        string id
    }
";
            var model = MermaidParser.Parse(src, "in-memory");
            model.Entities.Should().HaveCount(1);
        }

        [Fact]
        public void Aggregate_and_note_directives_are_known()
        {
            var src = @"erDiagram
    %% @surreal.schemafull
    %% @surreal.aggregate ""Foo (Models/Foo.cs)""
    %% @surreal.note ""multi-paragraph note""
    foo {
        string id
    }
";
            var model = MermaidParser.Parse(src, "in-memory");
            var anns = model.Entities[0].Annotations;
            anns.Should().Contain(a => a.Directive == "aggregate");
            anns.Should().Contain(a => a.Directive == "note");
        }

        [Fact]
        public void Attribute_default_annotation_is_recognized()
        {
            var src = @"erDiagram
    foo {
        %% @surreal.default ""false""
        bool is_active
    }
";
            var model = MermaidParser.Parse(src, "in-memory");
            var attr = model.Entities[0].Attributes.Single();
            attr.Annotations.Should().ContainSingle(a => a.Directive == "default");
        }
    }
}
