using System.Runtime.CompilerServices;
using System.Text;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Trusted SurrealQL templates: code literals plus validated identifier tokens; see AGENTS.md §identifier-template.</summary>
public static class SurrealIdentifierTemplate
{
    /// <summary>Builds one statement from template literals, <see cref="SurrealTemplateToken"/> and <see cref="SurrealTemplateFragment"/> holes.</summary>
    public static SurrealQuery Of(SurrealIdentifierTemplateHandler template)
    {
        var text = template.Builder;
        // raw: literals are code; every hole is an allowlisted token or a fragment built the same way. See AGENTS.md §identifier-template.
#pragma warning disable SRDB0001
        return SurrealQuery.Of(text.ToString());
#pragma warning restore SRDB0001
    }

    /// <summary>Builds a reusable SurrealQL fragment from the same trusted parts as <see cref="Of"/>.</summary>
    public static SurrealTemplateFragment Fragment(SurrealIdentifierTemplateHandler template) => new(template.Builder.ToString());

    /// <summary>Joins fragments with <c> AND </c>; an empty sequence yields an empty fragment.</summary>
    public static SurrealTemplateFragment And(IEnumerable<SurrealTemplateFragment> parts) =>
        new(string.Join(" AND ", parts.Select(part => part.Sql)));

    /// <summary>A <c>THROW 'MARKER'</c> fragment; markers are uppercase letters and underscores only.</summary>
    public static SurrealTemplateFragment Throw(string marker)
    {
        if (string.IsNullOrEmpty(marker) || marker.Any(character => character is not (>= 'A' and <= 'Z') and not '_'))
            throw new ArgumentException("An uppercase failure marker is required.", nameof(marker));
        return new("THROW '" + marker + "'");
    }

    /// <summary>Validates one identifier token (<c>^[a-z0-9_]+$</c>).</summary>
    public static SurrealTemplateToken Token(string value) => new(value);
}

/// <summary>A SurrealQL identifier/variable/table token restricted to <c>^[a-z0-9_]+$</c>.</summary>
public readonly struct SurrealTemplateToken
{
    internal SurrealTemplateToken(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
            throw new ArgumentException("SurrealQL template tokens must match ^[a-z0-9_]+$.", nameof(value));
        Value = value;
    }

    /// <summary>The validated token text.</summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>SurrealQL text composed only of template literals and validated tokens.</summary>
public readonly struct SurrealTemplateFragment
{
    private readonly string? _sql;

    internal SurrealTemplateFragment(string sql) => _sql = sql;

    /// <summary>The empty fragment.</summary>
    public static SurrealTemplateFragment Empty => new(string.Empty);

    internal string Sql => _sql ?? string.Empty;

    /// <summary>Whether the fragment renders no text.</summary>
    public bool IsEmpty => Sql.Length == 0;
}

/// <summary>Interpolated-string handler accepting only token and fragment holes.</summary>
[InterpolatedStringHandler]
public ref struct SurrealIdentifierTemplateHandler
{
    /// <summary>Creates the handler (compiler-invoked).</summary>
    public SurrealIdentifierTemplateHandler(int literalLength, int formattedCount) =>
        Builder = new StringBuilder(literalLength + formattedCount * 24);

    internal StringBuilder Builder { get; }

    /// <summary>Appends a code literal segment (compiler-invoked).</summary>
    public readonly void AppendLiteral(string value) => Builder.Append(value);

    /// <summary>Appends a validated token.</summary>
    public readonly void AppendFormatted(SurrealTemplateToken token) => Builder.Append(token.Value);

    /// <summary>Appends a trusted fragment.</summary>
    public readonly void AppendFormatted(SurrealTemplateFragment fragment) => Builder.Append(fragment.Sql);
}
