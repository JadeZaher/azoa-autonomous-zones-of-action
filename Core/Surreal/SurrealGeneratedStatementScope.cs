using System.Text;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Scopes bound parameters in one trusted typed-generated statement; see AGENTS.md.</summary>
public static class SurrealGeneratedStatementScope
{
    /// <summary>Captures one scoped typed-generated result for a surrounding atomic transaction.</summary>
    public static SurrealQuery Assign(SurrealQuery statement, string scope, string variable)
    {
        ValidateVariable(variable);
        var scoped = Apply(statement, scope);
        // raw: binds a typed mutation result used by later statements in the same atomic transaction.
#pragma warning disable SRDB0001
        var assigned = SurrealQuery.Of($"LET ${variable} = ({scoped.Sql})");
#pragma warning restore SRDB0001
        foreach (var parameter in scoped.Params) assigned = assigned.WithParam(parameter.Key, parameter.Value);
        return assigned;
    }

    /// <summary>Captures one scoped single-record read (e.g. <c>SurrealQuery&lt;T&gt;.Key(id)</c>) as the record, or NONE when absent.</summary>
    public static SurrealQuery AssignRecord(SurrealQuery statement, string scope, string variable)
    {
        ValidateVariable(variable);
        var scoped = Apply(statement, scope);
        if (!scoped.Sql.StartsWith("SELECT ", StringComparison.Ordinal))
            throw new ArgumentException("AssignRecord captures single-record SELECTs; use Assign for ONLY mutations.", nameof(statement));
        // raw: a SELECT yields an array even for one record id; [0] is the record or NONE. See AGENTS.md §generated-statement-scope.
#pragma warning disable SRDB0001
        var assigned = SurrealQuery.Of($"LET ${variable} = ({scoped.Sql})[0]");
#pragma warning restore SRDB0001
        foreach (var parameter in scoped.Params) assigned = assigned.WithParam(parameter.Key, parameter.Value);
        return assigned;
    }

    /// <summary>Aborts the surrounding transaction when a captured conditional mutation matched no row.</summary>
    public static SurrealQuery RequireResult(string variable, string failureMarker)
    {
        ValidateVariable(variable);
        if (string.IsNullOrEmpty(failureMarker) || failureMarker.Any(character => character is not (>= 'A' and <= 'Z') and not '_'))
            throw new ArgumentException("An uppercase failure marker is required.", nameof(failureMarker));
        // raw: validated variable and marker tokens only; no value is interpolated.
#pragma warning disable SRDB0001
        return SurrealQuery.Of($"IF ${variable} = NONE OR ${variable} = [] {{ THROW '{failureMarker}' }}");
#pragma warning restore SRDB0001
    }

    /// <summary>Renames parameter tokens outside quoted content; accepts no batches, comments, or unbound variables.</summary>
    public static SurrealQuery Apply(SurrealQuery statement, string scope)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (statement.IsMultiStatement || string.IsNullOrEmpty(scope)
            || scope.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
            throw new ArgumentException("A single generated statement and lowercase scope are required.");
        var sql = statement.Sql;
        var output = new StringBuilder(sql.Length);
        char quote = '\0';
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quote != '\0')
            {
                output.Append(character);
                if (character == '\\' && index + 1 < sql.Length) output.Append(sql[++index]);
                else if (character == quote)
                {
                    if (index + 1 < sql.Length && sql[index + 1] == quote) output.Append(sql[++index]);
                    else quote = '\0';
                }
                continue;
            }
            if (character is '\'' or '"' or '`' or '⟨')
            {
                quote = character == '⟨' ? '⟩' : character;
                output.Append(character);
                continue;
            }
            if (character == '#' || (index + 1 < sql.Length &&
                (sql.AsSpan(index, 2).SequenceEqual("--") || sql.AsSpan(index, 2).SequenceEqual("/*"))))
                throw new ArgumentException("Generated statements must not contain comments.", nameof(statement));
            if (character != '$') { output.Append(character); continue; }
            var start = ++index;
            while (index < sql.Length && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] == '_')) index++;
            var name = sql[start..index];
            index--;
            if (!statement.Params.ContainsKey(name))
                throw new ArgumentException("Generated statements cannot contain unbound variables.", nameof(statement));
            output.Append('$').Append(scope).Append('_').Append(name);
        }
        if (quote != '\0') throw new ArgumentException("Unterminated quoted content.", nameof(statement));
        // raw: only token-renaming of trusted generated SQL; see AGENTS.md §generated-statement-scope.
#pragma warning disable SRDB0001
        var scoped = SurrealQuery.Of(output.ToString());
#pragma warning restore SRDB0001
        foreach (var parameter in statement.Params)
            scoped = scoped.WithParam(scope + "_" + parameter.Key, parameter.Value);
        return scoped;
    }

    private static void ValidateVariable(string variable)
    {
        if (string.IsNullOrEmpty(variable) || variable.Any(character => character is not (>= 'a' and <= 'z') and not '_'))
            throw new ArgumentException("A lowercase transaction variable is required.", nameof(variable));
    }
}
