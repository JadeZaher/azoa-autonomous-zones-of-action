using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AZOA.WebAPI.Services.Quest.Predicates;

/// <summary>
/// Thrown for any predicate that cannot be parsed, references an unknown path,
/// or compares incompatible types. The handler catches this and returns a
/// <c>Fail</c> result — an exception MUST NOT escape into the engine, and the
/// gate fails closed.
/// </summary>
public sealed class GatePredicateException : Exception
{
    public GatePredicateException(string message) : base(message) { }
}

/// <summary>
/// A tiny, whitelisted, hand-rolled boolean-expression evaluator over a flat
/// scope of <see cref="JsonElement"/> values. Zero external dependencies — no
/// <c>DataTable.Compute</c>, no Roslyn, no reflection, no <c>eval</c> of any
/// kind. The grammar is <b>closed</b>: there are no function calls, no method
/// invocation, no indexers, no I/O — only literals, dotted field paths,
/// comparison, boolean operators and parentheses. This makes the
/// no-arbitrary-code guarantee provable: any token the lexer/parser does not
/// recognise is a hard parse error.
/// </summary>
/// <remarks>
/// <para><b>Grammar (closed):</b></para>
/// <code>
/// expr      := orExpr
/// orExpr    := andExpr ( "||" andExpr )*
/// andExpr   := notExpr ( "&amp;&amp;" notExpr )*
/// notExpr   := "!" notExpr | comparison
/// comparison:= primary ( ("==" | "!=" | "&lt;" | "&lt;=" | "&gt;" | "&gt;=") primary )?
/// primary   := number | string | "true" | "false" | "null"
///            | path | "(" expr ")"
/// path      := identifier ( "." identifier )*    // e.g. upstream.bal.amount, reads.kyc
/// </code>
/// <para><b>Literals:</b> numbers (double), strings (single- OR double-quoted),
/// <c>true</c>/<c>false</c>, <c>null</c>. Single-quoted strings are recommended
/// so JSON double-quotes do not clash, but both quote styles are accepted.</para>
/// <para><b>Field paths</b> resolve <c>upstream.&lt;node&gt;.&lt;json.path&gt;</c>
/// and <c>reads.&lt;name&gt;[.&lt;json.path&gt;]</c>. The first one or two
/// segments select a value from <paramref name="scope"/> (the handler keys it
/// as <c>upstream.&lt;node&gt;</c> and <c>reads.&lt;name&gt;</c>); any remaining
/// segments navigate into the JSON object. A missing scope key or missing JSON
/// member is a <see cref="GatePredicateException"/> — the gate <b>fails
/// closed</b>, never silently <c>false</c>.</para>
/// <para><b>Comparison semantics:</b> numbers compare numerically for all six
/// operators. Strings compare with <c>==</c>/<c>!=</c> only (lexical/ordinal
/// equality); ordering operators (<c>&lt; &lt;= &gt; &gt;=</c>) on strings are
/// <b>rejected</b> with a <see cref="GatePredicateException"/>. Booleans compare
/// with <c>==</c>/<c>!=</c> only and act as the operands of <c>! &amp;&amp;
/// ||</c>. Comparing values of different types with <c>==</c> yields
/// <c>false</c> (and <c>!=</c> yields <c>true</c>); ordering across types is a
/// <see cref="GatePredicateException"/>.</para>
/// <para><b>Boolean operators</b> <c>&amp;&amp;</c> and <c>||</c> short-circuit
/// and require boolean operands; <c>!</c> requires a boolean operand.</para>
/// </remarks>
public static class GatePredicateEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="predicate"/> against <paramref name="scope"/>
    /// and returns the boolean result.
    /// </summary>
    /// <param name="predicate">The whitelisted boolean expression.</param>
    /// <param name="scope">
    /// Top-level values keyed by their first path segment(s): the handler keys
    /// upstream node outputs as <c>upstream.&lt;node&gt;</c> and injected reads
    /// as <c>reads.&lt;name&gt;</c>.
    /// </param>
    /// <exception cref="GatePredicateException">
    /// On parse error, unknown/missing path, or type mismatch (fails closed).
    /// </exception>
    public static bool Evaluate(string predicate, IReadOnlyDictionary<string, JsonElement> scope)
    {
        if (predicate is null) throw new GatePredicateException("predicate is null");
        if (scope is null) throw new GatePredicateException("scope is null");

        var tokens = Lexer.Tokenize(predicate);
        var parser = new Parser(tokens);
        var ast = parser.ParseExpression();
        parser.ExpectEnd();

        var value = ast.Evaluate(scope);
        if (value is not bool b)
            throw new GatePredicateException("predicate did not evaluate to a boolean");
        return b;
    }

    // ─────────────────────────────── Lexer ───────────────────────────────

    private enum TokenKind
    {
        Number, String, True, False, Null, Identifier,
        Dot, EqEq, NotEq, Lt, LtEq, Gt, GtEq, And, Or, Not, LParen, RParen, End
    }

    private readonly struct Token
    {
        public Token(TokenKind kind, string text, int position)
        {
            Kind = kind;
            Text = text;
            Position = position;
        }

        public TokenKind Kind { get; }
        public string Text { get; }
        public int Position { get; }
    }

    private static class Lexer
    {
        public static List<Token> Tokenize(string input)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < input.Length)
            {
                var c = input[i];

                if (char.IsWhiteSpace(c)) { i++; continue; }

                switch (c)
                {
                    case '(': tokens.Add(new Token(TokenKind.LParen, "(", i)); i++; continue;
                    case ')': tokens.Add(new Token(TokenKind.RParen, ")", i)); i++; continue;
                    case '.': tokens.Add(new Token(TokenKind.Dot, ".", i)); i++; continue;
                }

                if (c == '&')
                {
                    if (i + 1 < input.Length && input[i + 1] == '&')
                    {
                        tokens.Add(new Token(TokenKind.And, "&&", i)); i += 2; continue;
                    }
                    throw new GatePredicateException($"unexpected '&' at {i} (use '&&')");
                }

                if (c == '|')
                {
                    if (i + 1 < input.Length && input[i + 1] == '|')
                    {
                        tokens.Add(new Token(TokenKind.Or, "||", i)); i += 2; continue;
                    }
                    throw new GatePredicateException($"unexpected '|' at {i} (use '||')");
                }

                if (c == '=')
                {
                    if (i + 1 < input.Length && input[i + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.EqEq, "==", i)); i += 2; continue;
                    }
                    throw new GatePredicateException($"unexpected '=' at {i} (use '==')");
                }

                if (c == '!')
                {
                    if (i + 1 < input.Length && input[i + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.NotEq, "!=", i)); i += 2; continue;
                    }
                    tokens.Add(new Token(TokenKind.Not, "!", i)); i++; continue;
                }

                if (c == '<')
                {
                    if (i + 1 < input.Length && input[i + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.LtEq, "<=", i)); i += 2; continue;
                    }
                    tokens.Add(new Token(TokenKind.Lt, "<", i)); i++; continue;
                }

                if (c == '>')
                {
                    if (i + 1 < input.Length && input[i + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.GtEq, ">=", i)); i += 2; continue;
                    }
                    tokens.Add(new Token(TokenKind.Gt, ">", i)); i++; continue;
                }

                if (c == '\'' || c == '"')
                {
                    var (text, next) = ReadString(input, i, c);
                    tokens.Add(new Token(TokenKind.String, text, i));
                    i = next;
                    continue;
                }

                if (c == '-' || char.IsDigit(c))
                {
                    var (text, next) = ReadNumber(input, i);
                    tokens.Add(new Token(TokenKind.Number, text, i));
                    i = next;
                    continue;
                }

                if (IsIdentifierStart(c))
                {
                    var (text, next) = ReadIdentifier(input, i);
                    var kind = text switch
                    {
                        "true" => TokenKind.True,
                        "false" => TokenKind.False,
                        "null" => TokenKind.Null,
                        _ => TokenKind.Identifier
                    };
                    tokens.Add(new Token(kind, text, i));
                    i = next;
                    continue;
                }

                throw new GatePredicateException($"unexpected character '{c}' at {i}");
            }

            tokens.Add(new Token(TokenKind.End, string.Empty, input.Length));
            return tokens;
        }

        private static (string text, int next) ReadString(string input, int start, char quote)
        {
            var sb = new StringBuilder();
            var i = start + 1;
            while (i < input.Length)
            {
                var c = input[i];
                if (c == '\\')
                {
                    if (i + 1 >= input.Length)
                        throw new GatePredicateException($"unterminated escape in string at {start}");
                    var esc = input[i + 1];
                    sb.Append(esc switch
                    {
                        '\\' => '\\',
                        '\'' => '\'',
                        '"' => '"',
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        _ => throw new GatePredicateException($"invalid escape '\\{esc}' at {i}")
                    });
                    i += 2;
                    continue;
                }
                if (c == quote)
                    return (sb.ToString(), i + 1);
                sb.Append(c);
                i++;
            }
            throw new GatePredicateException($"unterminated string starting at {start}");
        }

        private static (string text, int next) ReadNumber(string input, int start)
        {
            var i = start;
            if (input[i] == '-') i++;
            var sawDigit = false;
            while (i < input.Length && char.IsDigit(input[i])) { i++; sawDigit = true; }
            if (i < input.Length && input[i] == '.')
            {
                i++;
                while (i < input.Length && char.IsDigit(input[i])) { i++; sawDigit = true; }
            }
            if (i < input.Length && (input[i] == 'e' || input[i] == 'E'))
            {
                i++;
                if (i < input.Length && (input[i] == '+' || input[i] == '-')) i++;
                var sawExp = false;
                while (i < input.Length && char.IsDigit(input[i])) { i++; sawExp = true; }
                if (!sawExp)
                    throw new GatePredicateException($"malformed number exponent at {start}");
            }
            if (!sawDigit)
                throw new GatePredicateException($"malformed number at {start}");
            return (input.Substring(start, i - start), i);
        }

        private static (string text, int next) ReadIdentifier(string input, int start)
        {
            var i = start;
            while (i < input.Length && IsIdentifierPart(input[i])) i++;
            return (input.Substring(start, i - start), i);
        }

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    // ─────────────────────────────── AST ───────────────────────────────

    private abstract class Node
    {
        public abstract object? Evaluate(IReadOnlyDictionary<string, JsonElement> scope);
    }

    private sealed class LiteralNode : Node
    {
        private readonly object? _value;
        public LiteralNode(object? value) => _value = value;
        public override object? Evaluate(IReadOnlyDictionary<string, JsonElement> scope) => _value;
    }

    private sealed class PathNode : Node
    {
        private readonly IReadOnlyList<string> _segments;
        public PathNode(IReadOnlyList<string> segments) => _segments = segments;

        public override object? Evaluate(IReadOnlyDictionary<string, JsonElement> scope)
        {
            // Try the two-segment key first (upstream.<node>, reads.<name>),
            // then the one-segment key. Remaining segments navigate into JSON.
            JsonElement element;
            int consumed;
            if (_segments.Count >= 2 && scope.TryGetValue($"{_segments[0]}.{_segments[1]}", out element))
            {
                consumed = 2;
            }
            else if (scope.TryGetValue(_segments[0], out element))
            {
                consumed = 1;
            }
            else
            {
                throw new GatePredicateException($"unknown path '{Path()}': no scope value bound");
            }

            for (var i = consumed; i < _segments.Count; i++)
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new GatePredicateException(
                        $"path '{Path()}': cannot read member '{_segments[i]}' from a non-object");
                if (!element.TryGetProperty(_segments[i], out var child))
                    throw new GatePredicateException(
                        $"path '{Path()}': member '{_segments[i]}' not found");
                element = child;
            }

            return JsonToValue(element, Path());
        }

        private string Path() => string.Join(".", _segments);
    }

    private sealed class NotNode : Node
    {
        private readonly Node _operand;
        public NotNode(Node operand) => _operand = operand;

        public override object? Evaluate(IReadOnlyDictionary<string, JsonElement> scope)
        {
            var v = _operand.Evaluate(scope);
            if (v is not bool b)
                throw new GatePredicateException("'!' requires a boolean operand");
            return !b;
        }
    }

    private sealed class AndNode : Node
    {
        private readonly Node _left;
        private readonly Node _right;
        public AndNode(Node left, Node right) { _left = left; _right = right; }

        public override object? Evaluate(IReadOnlyDictionary<string, JsonElement> scope)
        {
            if (_left.Evaluate(scope) is not bool l)
                throw new GatePredicateException("'&&' requires boolean operands");
            if (!l) return false; // short-circuit
            if (_right.Evaluate(scope) is not bool r)
                throw new GatePredicateException("'&&' requires boolean operands");
            return r;
        }
    }

    private sealed class OrNode : Node
    {
        private readonly Node _left;
        private readonly Node _right;
        public OrNode(Node left, Node right) { _left = left; _right = right; }

        public override object? Evaluate(IReadOnlyDictionary<string, JsonElement> scope)
        {
            if (_left.Evaluate(scope) is not bool l)
                throw new GatePredicateException("'||' requires boolean operands");
            if (l) return true; // short-circuit
            if (_right.Evaluate(scope) is not bool r)
                throw new GatePredicateException("'||' requires boolean operands");
            return r;
        }
    }

    private sealed class ComparisonNode : Node
    {
        private readonly Node _left;
        private readonly Node _right;
        private readonly TokenKind _op;
        public ComparisonNode(Node left, TokenKind op, Node right) { _left = left; _op = op; _right = right; }

        public override object? Evaluate(IReadOnlyDictionary<string, JsonElement> scope)
        {
            var l = _left.Evaluate(scope);
            var r = _right.Evaluate(scope);

            if (_op is TokenKind.EqEq or TokenKind.NotEq)
            {
                var equal = ValuesEqual(l, r);
                return _op == TokenKind.EqEq ? equal : !equal;
            }

            // Ordering operators: numbers only.
            if (l is double ld && r is double rd)
            {
                var cmp = ld.CompareTo(rd);
                return _op switch
                {
                    TokenKind.Lt => cmp < 0,
                    TokenKind.LtEq => cmp <= 0,
                    TokenKind.Gt => cmp > 0,
                    TokenKind.GtEq => cmp >= 0,
                    _ => throw new GatePredicateException("unreachable comparison op")
                };
            }

            throw new GatePredicateException(
                "ordering comparison (< <= > >=) is only valid between two numbers");
        }

        private static bool ValuesEqual(object? l, object? r)
        {
            if (l is null && r is null) return true;
            if (l is null || r is null) return false;
            if (l is double ld && r is double rd) return ld.Equals(rd);
            if (l is string ls && r is string rs) return string.Equals(ls, rs, StringComparison.Ordinal);
            if (l is bool lb && r is bool rb) return lb == rb;
            // Different types compare unequal (no exception for == / !=).
            return false;
        }
    }

    // ─────────────────────────────── Parser ───────────────────────────────

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _pos;

        public Parser(List<Token> tokens) => _tokens = tokens;

        private Token Current => _tokens[_pos];

        public void ExpectEnd()
        {
            if (Current.Kind != TokenKind.End)
                throw new GatePredicateException(
                    $"unexpected trailing token '{Current.Text}' at {Current.Position}");
        }

        public Node ParseExpression() => ParseOr();

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Current.Kind == TokenKind.Or)
            {
                _pos++;
                var right = ParseAnd();
                left = new OrNode(left, right);
            }
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (Current.Kind == TokenKind.And)
            {
                _pos++;
                var right = ParseNot();
                left = new AndNode(left, right);
            }
            return left;
        }

        private Node ParseNot()
        {
            if (Current.Kind == TokenKind.Not)
            {
                _pos++;
                return new NotNode(ParseNot());
            }
            return ParseComparison();
        }

        private Node ParseComparison()
        {
            var left = ParsePrimary();
            if (Current.Kind is TokenKind.EqEq or TokenKind.NotEq
                or TokenKind.Lt or TokenKind.LtEq or TokenKind.Gt or TokenKind.GtEq)
            {
                var op = Current.Kind;
                _pos++;
                var right = ParsePrimary();
                return new ComparisonNode(left, op, right);
            }
            return left;
        }

        private Node ParsePrimary()
        {
            var tok = Current;
            switch (tok.Kind)
            {
                case TokenKind.Number:
                    _pos++;
                    return new LiteralNode(ParseNumberLiteral(tok));
                case TokenKind.String:
                    _pos++;
                    return new LiteralNode(tok.Text);
                case TokenKind.True:
                    _pos++;
                    return new LiteralNode(true);
                case TokenKind.False:
                    _pos++;
                    return new LiteralNode(false);
                case TokenKind.Null:
                    _pos++;
                    return new LiteralNode(null);
                case TokenKind.Identifier:
                    return ParsePath();
                case TokenKind.LParen:
                    _pos++;
                    var inner = ParseExpression();
                    if (Current.Kind != TokenKind.RParen)
                        throw new GatePredicateException(
                            $"expected ')' at {Current.Position}");
                    _pos++;
                    return inner;
                default:
                    throw new GatePredicateException(
                        $"unexpected token '{tok.Text}' at {tok.Position}");
            }
        }

        private Node ParsePath()
        {
            var segments = new List<string> { Current.Text };
            _pos++;
            while (Current.Kind == TokenKind.Dot)
            {
                _pos++;
                if (Current.Kind != TokenKind.Identifier)
                    throw new GatePredicateException(
                        $"expected identifier after '.' at {Current.Position}");
                segments.Add(Current.Text);
                _pos++;
            }
            return new PathNode(segments);
        }

        private static double ParseNumberLiteral(Token tok)
        {
            if (!double.TryParse(tok.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw new GatePredicateException($"malformed number '{tok.Text}' at {tok.Position}");
            return d;
        }
    }

    // ─────────────────────────── JSON value mapping ───────────────────────────

    private static object? JsonToValue(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetDouble(out var d)) return d;
                throw new GatePredicateException($"path '{path}': number out of range");
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            default:
                throw new GatePredicateException(
                    $"path '{path}': value of kind {element.ValueKind} is not comparable " +
                    "(only number/string/bool/null are supported)");
        }
    }
}
