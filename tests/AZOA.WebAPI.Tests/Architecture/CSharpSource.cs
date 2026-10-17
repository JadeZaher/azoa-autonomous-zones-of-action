using System.Text;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Lightweight C# source model for architecture rules: string literals, comment-free code and line lookup; see Architecture/AGENTS.md §surrealql-source-rules.</summary>
internal sealed class CSharpSource
{
    private static readonly string[] ExcludedDirectories = ["tests", "bin", "obj", "node_modules", "frontend", "sdk", "contracts", ".git", ".omc", "conductor", "docs"];
    private readonly int[] _lineStarts;

    private CSharpSource(string relativePath, string text)
    {
        RelativePath = relativePath.Replace('\\', '/');
        Text = text;
        _lineStarts = [0, .. text.Select((character, index) => (character, index)).Where(pair => pair.character == '\n').Select(pair => pair.index + 1)];
        (Code, Literals) = Lex(text);
    }

    public string RelativePath { get; }

    public string Text { get; }

    /// <summary>Text with comments blanked and every literal body replaced by spaces (offsets preserved).</summary>
    public string Code { get; }

    public IReadOnlyList<StringLiteral> Literals { get; }

    public static CSharpSource Parse(string relativePath, string text) => new(relativePath, text);

    public static IReadOnlyList<CSharpSource> Production()
    {
        var root = RepositoryRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Any(part => ExcludedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .Select(path => Parse(Path.GetRelativePath(root, path), File.ReadAllText(path)))
            .ToList();
    }

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AZOA.WebAPI.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }

    public int LineOf(int offset)
    {
        var index = Array.BinarySearch(_lineStarts, offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }

    public string Line(int line) => Text[_lineStarts[line - 1]..(line < _lineStarts.Length ? _lineStarts[line] : Text.Length)].TrimEnd('\r', '\n');

    public string Location(int offset) => $"{RelativePath}:{LineOf(offset)}";

    public StringLiteral? LiteralAt(int offset) => Literals.FirstOrDefault(literal => literal.Start == offset);

    /// <summary>Index of the bracket closing the one at <paramref name="open"/> in <see cref="Code"/>, or -1.</summary>
    public int Close(int open)
    {
        var (opening, closing) = Code[open] switch { '(' => ('(', ')'), '{' => ('{', '}'), '[' => ('[', ']'), '<' => ('<', '>'), _ => ('\0', '\0') };
        var depth = 0;
        for (var index = open; index < Code.Length; index++)
        {
            if (Code[index] == opening) depth++;
            else if (Code[index] == closing && --depth == 0) return index;
        }

        return -1;
    }

    /// <summary>Splits <paramref name="start"/>..<paramref name="end"/> of <see cref="Code"/> at top-level commas.</summary>
    public IEnumerable<(int Start, int End)> Arguments(int start, int end)
    {
        var depth = 0;
        var segment = start;
        for (var index = start; index < end; index++)
        {
            switch (Code[index])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0:
                    yield return (segment, index);
                    segment = index + 1;
                    break;
            }
        }

        yield return (segment, end);
    }

    private static (string Code, List<StringLiteral> Literals) Lex(string text)
    {
        var code = new StringBuilder(text);
        var literals = new List<StringLiteral>();
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                var end = text.IndexOf('\n', index);
                end = end < 0 ? text.Length : end;
                Blank(code, index, end);
                index = end;
                continue;
            }

            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                Blank(code, index, end);
                index = end;
                continue;
            }

            if (text[index] == '\'')
            {
                var end = index + 1;
                while (end < text.Length && text[end] != '\'') end += text[end] == '\\' ? 2 : 1;
                index = Math.Min(end + 1, text.Length);
                continue;
            }

            var prefixStart = index;
            var interpolated = false;
            var verbatim = false;
            while (index < text.Length && (text[index] == '$' || text[index] == '@'))
            {
                interpolated |= text[index] == '$';
                verbatim |= text[index] == '@';
                index++;
            }

            if (index >= text.Length || text[index] != '"')
            {
                index = prefixStart + 1;
                continue;
            }

            var quotes = 0;
            while (index + quotes < text.Length && text[index + quotes] == '"') quotes++;
            int bodyStart, bodyEnd, literalEnd;
            if (quotes >= 3)
            {
                var fence = new string('"', quotes);
                bodyStart = index + quotes;
                bodyEnd = text.IndexOf(fence, bodyStart, StringComparison.Ordinal);
                bodyEnd = bodyEnd < 0 ? text.Length : bodyEnd;
                literalEnd = Math.Min(bodyEnd + quotes, text.Length);
            }
            else if (quotes == 2 && !verbatim)
            {
                bodyStart = bodyEnd = index + 1;
                literalEnd = index + 2;
            }
            else
            {
                bodyStart = index + 1;
                var cursor = bodyStart;
                var holes = 0;
                while (cursor < text.Length)
                {
                    if (verbatim && text[cursor] == '"' && cursor + 1 < text.Length && text[cursor + 1] == '"') { cursor += 2; continue; }
                    if (!verbatim && text[cursor] == '\\') { cursor += 2; continue; }
                    if (interpolated && (text[cursor] == '{' || text[cursor] == '}') && cursor + 1 < text.Length && text[cursor + 1] == text[cursor] && holes == 0) { cursor += 2; continue; }
                    if (interpolated && text[cursor] == '{') { holes++; cursor++; continue; }
                    if (interpolated && text[cursor] == '}' && holes > 0) { holes--; cursor++; continue; }
                    if (text[cursor] == '"' && holes > 0)
                    {
                        cursor++;
                        while (cursor < text.Length && text[cursor] != '"') cursor += text[cursor] == '\\' ? 2 : 1;
                        cursor++;
                        continue;
                    }

                    if (text[cursor] == '"') break;
                    cursor++;
                }

                bodyEnd = Math.Min(cursor, text.Length);
                literalEnd = Math.Min(cursor + 1, text.Length);
            }

            literals.Add(new StringLiteral(prefixStart, literalEnd, text[bodyStart..bodyEnd], interpolated));
            Blank(code, bodyStart, bodyEnd);
            index = literalEnd;
        }

        return (code.ToString(), literals);
    }

    private static void Blank(StringBuilder code, int start, int end)
    {
        for (var index = start; index < end; index++)
            if (code[index] != '\n' && code[index] != '\r') code[index] = ' ';
    }
}

/// <summary>A string literal: <see cref="Start"/> is the first prefix/quote character, <see cref="End"/> one past the closing quote.</summary>
internal sealed record StringLiteral(int Start, int End, string Body, bool Interpolated);
