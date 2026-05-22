// SPDX-License-Identifier: UNLICENSED
// End-to-end driver of the OasisSurrealDbSchemaGenerator: feeds a synthetic
// .mermaid AdditionalText through the Roslyn CSharpGeneratorDriver and
// asserts the produced sources match expectations.

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Oasis.SurrealDb.SourceGen;
using System.Collections.Immutable;
using System.Text;

namespace Oasis.SurrealDb.SourceGen.Tests;

public class GeneratorIntegrationTests
{
    private static GeneratorDriverRunResult RunGenerator(
        IReadOnlyDictionary<string, string> mermaidFiles,
        IReadOnlyDictionary<string, string>? globalOptions = null)
    {
        var compilation = CSharpCompilation.Create(
            "GeneratorIntegrationTests.AssemblyUnderTest",
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            });

        var additionalTexts = mermaidFiles
            .Select(kv => (AdditionalText)new InMemoryAdditionalText(kv.Key, kv.Value))
            .ToImmutableArray();

        var optionsProvider = new InMemoryAnalyzerConfigOptionsProvider(
            globalOptions ?? new Dictionary<string, string>(StringComparer.Ordinal));

        var generator = new OasisSurrealDbSchemaGenerator();
        var driver = CSharpGeneratorDriver.Create(
            new[] { generator.AsSourceGenerator() },
            additionalTexts: additionalTexts,
            optionsProvider: optionsProvider);

        var result = driver.RunGenerators(compilation);
        return result.GetRunResult();
    }

    [Fact]
    public void Generator_emits_partial_class_per_entity()
    {
        var src = """
            erDiagram
                wallet {
                    string id
                    string avatar_id
                }
            """;
        var result = RunGenerator(new Dictionary<string, string>
        {
            { "010_wallet.mermaid", src }
        });

        var generated = result.Results.Single();
        generated.Exception.Should().BeNull();
        generated.GeneratedSources.Should().HaveCount(1);
        var source = generated.GeneratedSources[0].SourceText.ToString();
        source.Should().Contain("public partial class Wallet");
        source.Should().Contain("public const string SchemaNameConst = \"wallet\";");
    }

    [Fact]
    public void Generator_emits_one_file_per_entity_across_mermaid_sources()
    {
        var walletSrc = """
            erDiagram
                wallet {
                    string id
                }
            """;
        var swapSrc = """
            erDiagram
                swap_state {
                    string id
                }
            """;
        var result = RunGenerator(new Dictionary<string, string>
        {
            { "010_wallet.mermaid", walletSrc },
            { "030_swap_state.mermaid", swapSrc }
        });

        var generated = result.Results.Single();
        generated.GeneratedSources.Should().HaveCount(2);
        generated.GeneratedSources.Select(s => s.HintName).Should()
            .BeEquivalentTo(new[] { "Wallet.g.cs", "SwapState.g.cs" });
    }

    [Fact]
    public void Generator_respects_OasisSurrealDbModelsNamespace_MSBuild_property()
    {
        var src = """
            erDiagram
                wallet {
                    string id
                }
            """;
        var result = RunGenerator(
            new Dictionary<string, string> { { "010_wallet.mermaid", src } },
            globalOptions: new Dictionary<string, string>
            {
                { "build_property.OasisSurrealDbModelsNamespace", "OASIS.WebAPI.Generated.SurrealDb" }
            });

        var generated = result.Results.Single();
        var source = generated.GeneratedSources[0].SourceText.ToString();
        source.Should().Contain("namespace OASIS.WebAPI.Generated.SurrealDb");
    }

    [Fact]
    public void Generator_default_namespace_is_assembly_dot_Generated_SurrealDb()
    {
        var src = """
            erDiagram
                wallet {
                    string id
                }
            """;
        var result = RunGenerator(new Dictionary<string, string>
        {
            { "010_wallet.mermaid", src }
        });

        var generated = result.Results.Single();
        var source = generated.GeneratedSources[0].SourceText.ToString();
        source.Should().Contain("namespace GeneratorIntegrationTests.AssemblyUnderTest.Generated.SurrealDb");
    }

    [Fact]
    public void Generator_is_deterministic_two_runs_byte_identical()
    {
        var src = """
            erDiagram
                wallet {
                    string id
                    string avatar_id
                    option<string> label
                }
            """;
        var inputs = new Dictionary<string, string> { { "010_wallet.mermaid", src } };
        var r1 = RunGenerator(inputs);
        var r2 = RunGenerator(inputs);
        r1.Results.Single().GeneratedSources[0].SourceText.ToString()
            .Should().Be(r2.Results.Single().GeneratedSources[0].SourceText.ToString());
    }

    [Fact]
    public void Generator_reports_diagnostic_on_unparseable_mermaid()
    {
        var src = "this is not a valid mermaid file";
        var result = RunGenerator(new Dictionary<string, string>
        {
            { "010_invalid.mermaid", src }
        });

        var diags = result.Diagnostics;
        diags.Should().Contain(d => d.Id == "OSGSG001");
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly string _content;
        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _content = content;
        }
        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(_content, Encoding.UTF8);
    }

    private sealed class InMemoryAnalyzerConfigOptionsProvider : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider
    {
        private readonly InMemoryAnalyzerConfigOptions _options;
        public InMemoryAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> globalOptions)
        {
            _options = new InMemoryAnalyzerConfigOptions(globalOptions);
        }
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GlobalOptions => _options;
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }

    private sealed class InMemoryAnalyzerConfigOptions : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _options;
        public InMemoryAnalyzerConfigOptions(IReadOnlyDictionary<string, string> options) => _options = options;
        public override bool TryGetValue(string key, out string value)
        {
            if (_options.TryGetValue(key, out var v))
            {
                value = v;
                return true;
            }
            value = string.Empty;
            return false;
        }
    }
}
