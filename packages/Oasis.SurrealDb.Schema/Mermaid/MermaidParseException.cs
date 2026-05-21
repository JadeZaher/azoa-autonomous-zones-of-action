// SPDX-License-Identifier: UNLICENSED
// Oasis.SurrealDb.Schema -- Parse-time exception.
//
// All Mermaid + annotation-DSL errors flow through this exception. The
// message format is intentionally <c>file:line:col: message</c> so
// downstream CLI tooling can surface clickable errors in IDEs without
// extra formatting.

using System;

namespace Oasis.SurrealDb.Schema.Mermaid
{
    /// <summary>
    /// Thrown when a Mermaid source file cannot be parsed. Carries enough
    /// position metadata to produce a clickable <c>file:line:col</c> error.
    /// </summary>
    public sealed class MermaidParseException : Exception
    {
        /// <summary>Source file path (best-effort; may be empty for in-memory parses).</summary>
        public string File { get; }

        /// <summary>1-based line number where the error was detected.</summary>
        public int Line { get; }

        /// <summary>1-based column number.</summary>
        public int Column { get; }

        /// <summary>Human-readable diagnostic (without the file:line:col prefix).</summary>
        public string Diagnostic { get; }

        public MermaidParseException(string file, int line, int column, string message)
            : base(FormatMessage(file, line, column, message))
        {
            File = file ?? string.Empty;
            Line = line;
            Column = column;
            Diagnostic = message ?? string.Empty;
        }

        private static string FormatMessage(string file, int line, int column, string message)
        {
            var loc = string.IsNullOrEmpty(file) ? $"<input>:{line}:{column}" : $"{file}:{line}:{column}";
            return $"{loc}: {message}";
        }
    }
}
