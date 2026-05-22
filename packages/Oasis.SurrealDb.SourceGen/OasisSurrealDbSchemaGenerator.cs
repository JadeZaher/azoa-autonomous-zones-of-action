// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.SourceGen -- Roslyn IIncrementalGenerator that translates
// Mermaid ER schema sources (.mermaid AdditionalFiles) into strongly-typed
// C# POCOs at compile time. Eliminates hand-maintained drift between the
// schema source of truth and the application domain layer.
//
// Wiring (consumer-side):
//
//   <ItemGroup>
//     <ProjectReference Include="...\Oasis.SurrealDb.SourceGen.csproj"
//                       OutputItemType="Analyzer"
//                       ReferenceOutputAssembly="false" />
//     <AdditionalFiles Include="Persistence/SurrealDb/Schemas/source/*.mermaid" />
//   </ItemGroup>
//
//   <PropertyGroup>
//     <OasisSurrealDbModelsNamespace>OASIS.WebAPI.Generated.SurrealDb</OasisSurrealDbModelsNamespace>
//   </PropertyGroup>
//
// Stub here is replaced by the full generator in Phase 2.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Oasis.SurrealDb.Schema.Mermaid;

namespace Oasis.SurrealDb.SourceGen
{
    /// <summary>
    /// Roslyn incremental source generator. Reads <c>.mermaid</c> schema sources
    /// supplied via <c>AdditionalFiles</c> and emits one C# <c>partial class</c>
    /// per entity. Output files are named <c>{Entity}.g.cs</c>.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class OasisSurrealDbSchemaGenerator : IIncrementalGenerator
    {
        /// <summary>
        /// MSBuild property the consumer sets to override the namespace into which
        /// the generated POCOs are emitted. Default: assembly root namespace +
        /// <c>.Generated.SurrealDb</c>.
        /// </summary>
        public const string NamespaceMsBuildProperty = "OasisSurrealDbModelsNamespace";

        /// <summary>The AdditionalFiles extension this generator consumes.</summary>
        public const string MermaidFileExtension = ".mermaid";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // 1. AdditionalFiles -> only .mermaid files.
            var mermaidFiles = context.AdditionalTextsProvider
                .Where(static file =>
                    file.Path != null
                    && file.Path.EndsWith(MermaidFileExtension, StringComparison.OrdinalIgnoreCase));

            // 2. Combine with the analyzer-config so the consumer can override
            //    the target namespace via <OasisSurrealDbModelsNamespace>. The
            //    global config is also exposed so we can fall back to
            //    rootnamespace+.Generated.SurrealDb when no override is set.
            var configCombined = mermaidFiles
                .Combine(context.AnalyzerConfigOptionsProvider)
                .Combine(context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Generated"));

            // 3. Parse + emit.
            context.RegisterSourceOutput(configCombined, (spc, tuple) =>
            {
                var ((additionalText, configProvider), assemblyName) = tuple;
                EmitForFile(spc, additionalText, configProvider, assemblyName);
            });
        }

        private static void EmitForFile(
            SourceProductionContext spc,
            AdditionalText additionalText,
            AnalyzerConfigOptionsProvider configProvider,
            string assemblyName)
        {
            var text = additionalText.GetText(spc.CancellationToken);
            if (text is null) return;

            var sourceText = text.ToString();

            MermaidSchemaModel model;
            try
            {
                model = MermaidParser.Parse(sourceText, additionalText.Path);
            }
            catch (MermaidParseException ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(MermaidParseDescriptor,
                    Location.None, additionalText.Path, ex.Line, ex.Column, ex.Message));
                return;
            }

            // Determine target namespace.
            string defaultNs = (assemblyName ?? "Generated") + ".Generated.SurrealDb";
            string? configuredNs = null;
            if (configProvider.GlobalOptions.TryGetValue(
                    $"build_property.{NamespaceMsBuildProperty}", out var nsValue)
                && !string.IsNullOrWhiteSpace(nsValue))
            {
                configuredNs = nsValue;
            }

            foreach (var entity in model.Entities.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                // Per-entity namespace override (annotation directive).
                string entityNs = configuredNs ?? defaultNs;
                foreach (var ann in entity.Annotations)
                {
                    if (ann.Directive == "csharp")
                    {
                        // @surreal.csharp.namespace <ns> is parsed as a sub-directive
                        // by the extended Mermaid annotation DSL (see Schema package).
                        if (ann.Arguments.TryGetValue("namespace", out var nsOverride)
                            && !string.IsNullOrWhiteSpace(nsOverride))
                        {
                            entityNs = nsOverride;
                        }
                    }
                }

                var emitter = new CSharpEmitter(entityNs);
                string source;
                try
                {
                    source = emitter.Emit(entity);
                }
                catch (NotSupportedException ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(UnsupportedTypeDescriptor,
                        Location.None, entity.Name, ex.Message));
                    continue;
                }

                // Hint name follows the C# class name (PascalCase) so two
                // entities `wallet` and `Wallet` would collide deterministically
                // and the generated file path inside obj/Debug/.../generated/
                // matches the class users see in error diagnostics.
                var className = CSharpTypeMapper.ToPascalCase(entity.Name);
                var fileName = className + ".g.cs";
                spc.AddSource(fileName, SourceText.From(source, Encoding.UTF8));
            }
        }

        // ─── Diagnostics ─────────────────────────────────────────────────────

        internal static readonly DiagnosticDescriptor MermaidParseDescriptor =
            new DiagnosticDescriptor(
                id: "OSGSG001",
                title: "Mermaid schema parse error",
                messageFormat: "Mermaid schema '{0}' (line {1}, col {2}) failed to parse: {3}",
                category: "Oasis.SurrealDb.SourceGen",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description:
                    "The Oasis.SurrealDb source generator could not parse the supplied .mermaid file. "
                    + "Re-check the file's syntax against the Mermaid ER + @surreal.* annotation grammar.");

        internal static readonly DiagnosticDescriptor UnsupportedTypeDescriptor =
            new DiagnosticDescriptor(
                id: "OSGSG002",
                title: "Unsupported SurrealDB type",
                messageFormat: "Entity '{0}' contains an unsupported SurrealDB type: {1}",
                category: "Oasis.SurrealDb.SourceGen",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description:
                    "The Oasis.SurrealDb source generator does not recognize the supplied "
                    + "SurrealDB type. Extend CSharpTypeMapper with an explicit mapping or "
                    + "use @surreal.csharp.skip on the field.");
    }
}
