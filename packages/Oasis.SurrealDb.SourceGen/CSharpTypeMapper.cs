// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.SourceGen -- deterministic SurrealDB type -> C# type table.
//
// One pure function, no I/O. Tested per-row in
// tests/Oasis.SurrealDb.SourceGen.Tests/CSharpTypeMapperTests.cs.

#nullable enable

using System;
using System.Text;

namespace Oasis.SurrealDb.SourceGen
{
    /// <summary>
    /// Mapping table: Mermaid attribute type token -> C# type reference.
    /// </summary>
    /// <remarks>
    /// Recognized tokens (case-sensitive, as written in .mermaid):
    /// <list type="bullet">
    /// <item><c>string</c> -> <c>string</c></item>
    /// <item><c>int</c> -> <c>long</c> (SurrealDB ints are 64-bit signed)</item>
    /// <item><c>decimal</c> -> <c>decimal</c></item>
    /// <item><c>datetime</c> -> <c>DateTimeOffset</c></item>
    /// <item><c>duration</c> -> <c>TimeSpan</c></item>
    /// <item><c>bool</c> -> <c>bool</c></item>
    /// <item><c>object</c> -> <c>System.Text.Json.JsonElement?</c> (opaque bag)</item>
    /// <item><c>record&lt;T&gt;</c> -> <c>RecordId&lt;T&gt;</c></item>
    /// <item><c>array&lt;X&gt;</c> -> <c>IReadOnlyList&lt;X&gt;</c> (recursive)</item>
    /// <item><c>option&lt;X&gt;</c> -> nullable wrapper on the inner mapping</item>
    /// </list>
    /// Unknown tokens throw <see cref="NotSupportedException"/> with the
    /// offending token in the message; callers should treat the exception as
    /// a hard build error.
    /// </remarks>
    public static class CSharpTypeMapper
    {
        /// <summary>
        /// Map a Mermaid type token to a C# type reference.
        /// </summary>
        /// <param name="mermaidType">The raw Mermaid token (e.g. <c>option&lt;string&gt;</c>).</param>
        /// <returns>A <see cref="CSharpTypeRef"/> capturing the C# type string and nullability.</returns>
        /// <exception cref="NotSupportedException">when the token is not in the table.</exception>
        public static CSharpTypeRef Map(string mermaidType)
        {
            if (mermaidType == null) throw new ArgumentNullException(nameof(mermaidType));
            var token = mermaidType.Trim();
            if (token.Length == 0)
            {
                throw new NotSupportedException(
                    "Empty Mermaid type token. Provide a SurrealDB type (string, int, decimal, datetime, duration, bool, object, record<T>, array<X>, option<X>).");
            }
            return MapInner(token, isOption: false);
        }

        private static CSharpTypeRef MapInner(string token, bool isOption)
        {
            // option<X>
            if (StartsWithOpenParen(token, "option"))
            {
                var inner = ExtractGenericInner(token, "option");
                var innerRef = MapInner(inner, isOption: true);
                // Mark as nullable.
                return innerRef.WithNullable(true);
            }

            // array<X>
            if (StartsWithOpenParen(token, "array"))
            {
                var inner = ExtractGenericInner(token, "array");
                var innerRef = MapInner(inner, isOption: false);
                var typeStr = "global::System.Collections.Generic.IReadOnlyList<" + innerRef.TypeName + ">";
                return new CSharpTypeRef(typeStr, isNullable: isOption);
            }

            // record<T>
            if (StartsWithOpenParen(token, "record"))
            {
                var inner = ExtractGenericInner(token, "record");
                // record<T> -- T is a SurrealDB table name; we map it to the
                // PascalCase generated class name in the same namespace. The
                // generator emits classes whose SchemaName matches the table
                // name, so the C# `RecordId<T>` constraint binds via the
                // marker interface and runtime SchemaName check.
                var pascal = ToPascalCase(inner);
                var typeStr = "global::Oasis.SurrealDb.Client.RecordId<" + pascal + ">";
                return new CSharpTypeRef(typeStr, isNullable: isOption);
            }

            // Scalar atoms.
            string mapped;
            switch (token)
            {
                case "string":   mapped = "string"; break;
                case "int":      mapped = "long";   break;
                case "decimal":  mapped = "decimal"; break;
                case "datetime": mapped = "global::System.DateTimeOffset"; break;
                case "duration": mapped = "global::System.TimeSpan"; break;
                case "bool":     mapped = "bool"; break;
                case "object":   mapped = "global::System.Text.Json.JsonElement"; break;
                default:
                    throw new NotSupportedException(
                        "Unknown SurrealDB type '" + token + "'. " +
                        "Recognized atomic types: string, int, decimal, datetime, duration, bool, object. " +
                        "Generic forms: record<T>, array<X>, option<X>. " +
                        "Extend CSharpTypeMapper.MapInner to add support, or use @surreal.csharp.skip to omit this field.");
            }
            return new CSharpTypeRef(mapped, isNullable: isOption);
        }

        private static bool StartsWithOpenParen(string token, string wrapper)
        {
            return token.Length > wrapper.Length + 2
                && token.StartsWith(wrapper, StringComparison.Ordinal)
                && token[wrapper.Length] == '<'
                && token[token.Length - 1] == '>';
        }

        private static string ExtractGenericInner(string token, string wrapper)
        {
            // From `wrapper<inner>` -- return `inner`. Inner may itself
            // contain angle brackets (option<array<string>>); count depth.
            int start = wrapper.Length + 1; // past '<'
            int end = token.Length - 1;     // at '>'
            return token.Substring(start, end - start);
        }

        /// <summary>
        /// Translate a snake_case identifier to PascalCase: <c>avatar_id</c> -> <c>AvatarId</c>.
        /// Multiple underscores collapse: <c>foo__bar</c> -> <c>FooBar</c>.
        /// </summary>
        public static string ToPascalCase(string snake)
        {
            if (snake == null) throw new ArgumentNullException(nameof(snake));
            var sb = new StringBuilder(snake.Length);
            bool nextUpper = true;
            for (int i = 0; i < snake.Length; i++)
            {
                char c = snake[i];
                if (c == '_')
                {
                    nextUpper = true;
                    continue;
                }
                sb.Append(nextUpper ? char.ToUpperInvariant(c) : c);
                nextUpper = false;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// C# type reference produced by <see cref="CSharpTypeMapper"/>. Captures
    /// the type string (e.g. <c>global::System.Collections.Generic.IReadOnlyList&lt;string&gt;</c>)
    /// plus the nullability bit that drives the <c>?</c> suffix.
    /// </summary>
    public readonly struct CSharpTypeRef
    {
        /// <summary>The fully-qualified type string (without trailing <c>?</c>).</summary>
        public string TypeName { get; }

        /// <summary>True when the source field was wrapped in <c>option&lt;...&gt;</c>.</summary>
        public bool IsNullable { get; }

        public CSharpTypeRef(string typeName, bool isNullable)
        {
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
            IsNullable = isNullable;
        }

        /// <summary>
        /// Return the property-type spelling: <c>TypeName</c> for non-nullable,
        /// <c>TypeName?</c> for nullable.
        /// </summary>
        public string ToPropertyType()
            => IsNullable ? TypeName + "?" : TypeName;

        /// <summary>Return a copy with the supplied nullability override.</summary>
        public CSharpTypeRef WithNullable(bool isNullable)
            => new CSharpTypeRef(TypeName, isNullable);
    }
}
