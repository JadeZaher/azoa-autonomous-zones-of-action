namespace AZOA.WebAPI.Helpers.Deployments;

/// <summary>Rewrites predicate path identifiers while preserving quoted literal bytes.</summary>
public static class PredicateBindingRewriter
{
    public static string Rewrite(string input, IReadOnlyDictionary<string, string> names)
    {
        var output = new System.Text.StringBuilder(input.Length);
        for (var index = 0; index < input.Length;)
        {
            if (input[index] is '\'' or '"')
            {
                var quote = input[index];
                var start = index++;
                var closed = false;
                while (index < input.Length)
                {
                    if (input[index] == '\\') { index += 2; continue; }
                    if (input[index++] == quote) { closed = true; break; }
                }
                if (!closed) throw new ArgumentException("Unterminated predicate literal.");
                output.Append(input.AsSpan(start, index - start));
                continue;
            }
            if (char.IsLetter(input[index]) || input[index] == '_')
            {
                var start = index++;
                while (index < input.Length && (char.IsLetterOrDigit(input[index]) || input[index] == '_')) index++;
                var identifier = input[start..index];
                output.Append(identifier);
                var segment = 0;
                while (index < input.Length)
                {
                    var separator = index;
                    var dot = index;
                    while (dot < input.Length && char.IsWhiteSpace(input[dot])) dot++;
                    if (dot >= input.Length || input[dot] != '.') break;
                    var next = dot + 1;
                    while (next < input.Length && char.IsWhiteSpace(input[next])) next++;
                    var end = next;
                    while (end < input.Length && (char.IsLetterOrDigit(input[end]) || input[end] is '_' or '-')) end++;
                    if (next == end) throw new ArgumentException("Predicate path has an empty segment.");
                    output.Append(input.AsSpan(separator, next - separator));
                    var original = input[next..end];
                    if (segment == 0 && identifier is "run" or "upstream")
                    {
                        if (!names.TryGetValue(original, out var replacement)) throw new ArgumentException("Conditional binding references an unknown node.");
                        output.Append(replacement);
                    }
                    else output.Append(original);
                    segment++;
                    index = end;
                }
                continue;
            }
            output.Append(input[index++]);
        }
        return output.ToString();
    }
}
