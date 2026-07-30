using System.Globalization;
using System.Text;

namespace Authagonal.Server.Services.Scim;

/// <summary>
/// A parsed SCIM filter (RFC 7644 §3.4.2.2). Evaluate one with <see cref="ScimFilterEvaluator"/>.
/// </summary>
/// <remarks>
/// The grammar is implemented in full — all ten comparison operators, <c>and</c>/<c>or</c>/<c>not</c>,
/// parenthesised grouping, value paths (<c>emails[type eq "work"]</c>), sub-attributes
/// (<c>name.givenName</c>) and URN-prefixed attribute paths. The previous implementation understood a
/// single <c>attr eq|co "value"</c> term, which is a problem beyond missing features: SCIM's
/// ServiceProviderConfig has no way to advertise a partial filter capability, so a provider claiming
/// <c>filter.supported = true</c> is claiming this grammar.
/// </remarks>
public abstract record ScimFilterExpression
{
    /// <summary>An <c>and</c>/<c>or</c> of two sub-expressions.</summary>
    public sealed record Logical(ScimFilterExpression Left, LogicalOperator Operator, ScimFilterExpression Right)
        : ScimFilterExpression;

    /// <summary><c>not (...)</c>.</summary>
    public sealed record Not(ScimFilterExpression Inner) : ScimFilterExpression;

    /// <summary><c>attrPath pr</c> — attribute present and non-empty.</summary>
    public sealed record Present(ScimAttributePath Path) : ScimFilterExpression;

    /// <summary><c>attrPath op value</c>.</summary>
    public sealed record Comparison(ScimAttributePath Path, ComparisonOperator Operator, ScimComparisonValue Value)
        : ScimFilterExpression;

    /// <summary>
    /// A bare value path used as a filter — <c>emails[type eq "work"]</c> — true when at least one
    /// element of the multi-valued attribute satisfies the inner filter.
    /// </summary>
    public sealed record ValuePathExists(ScimAttributePath Path) : ScimFilterExpression;
}

public enum LogicalOperator { And, Or }

public enum ComparisonOperator { Eq, Ne, Co, Sw, Ew, Gt, Ge, Lt, Le }

/// <summary>A comparison right-hand side: string, number, boolean or null.</summary>
public readonly record struct ScimComparisonValue(string? String, double? Number, bool? Boolean, bool IsNull)
{
    public static ScimComparisonValue FromString(string s) => new(s, null, null, false);
    public static ScimComparisonValue FromNumber(double d) => new(null, d, null, false);
    public static ScimComparisonValue FromBoolean(bool b) => new(null, null, b, false);
    public static readonly ScimComparisonValue Null = new(null, null, null, true);
}

/// <summary>
/// An attribute path: an optional schema URN, one or more dot-separated segments, and optionally a
/// value filter applied part-way along (<c>emails[type eq "work"].value</c> is segments
/// <c>emails</c> + <c>value</c> with a filter attached to <c>emails</c>).
/// </summary>
public sealed record ScimAttributePath(
    string? SchemaUrn,
    IReadOnlyList<string> Segments,
    ScimFilterExpression? ValueFilter,
    int ValueFilterSegmentIndex)
{
    public override string ToString() =>
        (SchemaUrn is null ? "" : SchemaUrn + ":") + string.Join('.', Segments);
}

/// <summary>Recursive-descent parser for the RFC 7644 §3.4.2.2 filter grammar.</summary>
public static class ScimFilterParser
{
    /// <summary>
    /// Parses a filter. Returns false with a caller-safe <paramref name="error"/> when the input is not
    /// a valid filter; that error is the <c>detail</c> of a 400 <c>invalidFilter</c>.
    /// </summary>
    public static bool TryParse(string? filter, out ScimFilterExpression? expression, out string? error)
    {
        expression = null;
        error = null;
        if (string.IsNullOrWhiteSpace(filter))
            return true; // absent: not an error, just no filter

        // Length cap before tokenizing. Any real SCIM filter is far shorter than this; the bound exists so
        // an attacker cannot pay one request to make the parser do unbounded work.
        if (filter.Length > MaxFilterLength)
        {
            error = $"Filter exceeds the maximum supported length of {MaxFilterLength} characters.";
            return false;
        }

        try
        {
            var tokens = Tokenizer.Tokenize(filter);
            var parser = new Parser(tokens);
            expression = parser.ParseExpression();
            parser.ExpectEnd();
            return true;
        }
        catch (ScimFilterFormatException ex)
        {
            expression = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Maximum accepted filter length. RFC 7644 sets no bound, so this is ours.
    /// </summary>
    private const int MaxFilterLength = 1024;

    /// <summary>
    /// Maximum grouping depth. <c>ParseExpression → ParseAnd → ParseNot → ParsePrimary</c> is mutually
    /// recursive, descending on every <c>(</c> and on every value-path <c>[</c>, with no bound — so a filter
    /// of nested parentheses overflowed the stack. A <see cref="StackOverflowException"/> cannot be caught
    /// in .NET: it terminates the PROCESS, taking down every tenant served by that worker, from a single
    /// unauthenticated-in-effect request against a SCIM endpoint. Real filters nest two or three deep.
    /// </summary>
    private const int MaxFilterDepth = 20;

    private sealed class ScimFilterFormatException(string message) : Exception(message);

    // ── Tokenizer ────────────────────────────────────────────────────────────────────────────────
    private enum TokenKind { Identifier, String, Number, True, False, Null, LParen, RParen, LBracket, RBracket, End }

    private readonly record struct Token(TokenKind Kind, string Text, double Number = 0);

    private static class Tokenizer
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
                    case '(': tokens.Add(new Token(TokenKind.LParen, "(")); i++; continue;
                    case ')': tokens.Add(new Token(TokenKind.RParen, ")")); i++; continue;
                    case '[': tokens.Add(new Token(TokenKind.LBracket, "[")); i++; continue;
                    case ']': tokens.Add(new Token(TokenKind.RBracket, "]")); i++; continue;
                    case '"': tokens.Add(ReadString(input, ref i)); continue;
                }

                if (c == '-' || char.IsDigit(c)) { tokens.Add(ReadNumber(input, ref i)); continue; }

                if (IsIdentifierStart(c))
                {
                    var start = i;
                    // Attribute paths carry dots, colons (URN prefixes), dashes, underscores and $ref.
                    while (i < input.Length && IsIdentifierPart(input[i])) i++;
                    var text = input[start..i];
                    tokens.Add(text.ToLowerInvariant() switch
                    {
                        "true" => new Token(TokenKind.True, text),
                        "false" => new Token(TokenKind.False, text),
                        "null" => new Token(TokenKind.Null, text),
                        _ => new Token(TokenKind.Identifier, text),
                    });
                    continue;
                }

                throw new ScimFilterFormatException(
                    $"Unexpected character '{c}' at position {i}.");
            }

            tokens.Add(new Token(TokenKind.End, ""));
            return tokens;
        }

        // '.' can START a token so the sub-attribute trailing a value path (emails[type eq "work"].value)
        // lexes as one identifier; the parser only accepts it in that position.
        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c is '_' or '$' or '.';

        private static bool IsIdentifierPart(char c) =>
            char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '$';

        private static Token ReadString(string input, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (true)
            {
                if (i >= input.Length)
                    throw new ScimFilterFormatException("Unterminated string literal in filter.");

                var c = input[i];
                if (c == '\\')
                {
                    if (i + 1 >= input.Length)
                        throw new ScimFilterFormatException("Unterminated escape sequence in filter.");
                    var esc = input[i + 1];
                    sb.Append(esc switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => throw new ScimFilterFormatException($"Unsupported escape '\\{esc}' in filter."),
                    });
                    i += 2;
                    continue;
                }
                if (c == '"') { i++; break; }
                sb.Append(c);
                i++;
            }
            return new Token(TokenKind.String, sb.ToString());
        }

        private static Token ReadNumber(string input, ref int i)
        {
            var start = i;
            if (input[i] == '-') i++;
            while (i < input.Length && char.IsDigit(input[i])) i++;
            if (i < input.Length && input[i] == '.')
            {
                i++;
                while (i < input.Length && char.IsDigit(input[i])) i++;
            }
            // Exponent, and only here may a sign follow — anywhere else a '-' begins the next token.
            if (i < input.Length && input[i] is 'e' or 'E')
            {
                i++;
                if (i < input.Length && input[i] is '+' or '-') i++;
                while (i < input.Length && char.IsDigit(input[i])) i++;
            }
            var text = input[start..i];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new ScimFilterFormatException($"'{text}' is not a valid number.");
            return new Token(TokenKind.Number, text, value);
        }
    }

    // ── Parser ───────────────────────────────────────────────────────────────────────────────────
    private sealed class Parser(List<Token> tokens)
    {
        private int _depth;

        /// <summary>
        /// Enters a nesting level, throwing past <see cref="MaxFilterDepth"/>. Every recursive descent
        /// (a parenthesised group, a NOT group, a value-path bracket) must be wrapped in this so the
        /// recursion is bounded rather than bounded by the stack.
        /// </summary>
        private void EnterDepth()
        {
            if (++_depth > MaxFilterDepth)
                throw new ScimFilterFormatException(
                    $"Filter nesting exceeds the maximum supported depth of {MaxFilterDepth}.");
        }

        private void ExitDepth() => _depth--;

        private int _pos;

        private Token Current => tokens[_pos];

        private bool IsKeyword(string keyword) =>
            Current.Kind == TokenKind.Identifier
            && string.Equals(Current.Text, keyword, StringComparison.OrdinalIgnoreCase);

        public void ExpectEnd()
        {
            if (Current.Kind != TokenKind.End)
                throw new ScimFilterFormatException(
                    $"Unexpected '{Current.Text}' after the end of the filter expression.");
        }

        /// <summary>or has the lowest precedence, then and, then not.</summary>
        public ScimFilterExpression ParseExpression()
        {
            var left = ParseAnd();
            while (IsKeyword("or"))
            {
                _pos++;
                var right = ParseAnd();
                left = new ScimFilterExpression.Logical(left, LogicalOperator.Or, right);
            }
            return left;
        }

        private ScimFilterExpression ParseAnd()
        {
            var left = ParseNot();
            while (IsKeyword("and"))
            {
                _pos++;
                var right = ParseNot();
                left = new ScimFilterExpression.Logical(left, LogicalOperator.And, right);
            }
            return left;
        }

        private ScimFilterExpression ParseNot()
        {
            if (IsKeyword("not"))
            {
                _pos++;
                if (Current.Kind != TokenKind.LParen)
                    throw new ScimFilterFormatException("'not' must be followed by a parenthesised filter.");
                _pos++;
                EnterDepth();
                var inner = ParseExpression();
                ExitDepth();
                Expect(TokenKind.RParen, ")");
                return new ScimFilterExpression.Not(inner);
            }
            return ParsePrimary();
        }

        private ScimFilterExpression ParsePrimary()
        {
            if (Current.Kind == TokenKind.LParen)
            {
                _pos++;
                EnterDepth();
                var inner = ParseExpression();
                ExitDepth();
                Expect(TokenKind.RParen, ")");
                return inner;
            }

            if (Current.Kind != TokenKind.Identifier)
                throw new ScimFilterFormatException(
                    Current.Kind == TokenKind.End
                        ? "Filter ended unexpectedly; expected an attribute name."
                        : $"Expected an attribute name but found '{Current.Text}'.");

            var path = ParseAttributePath();

            // A bare value path (emails[type eq "work"]) is itself a filter.
            if (Current.Kind is TokenKind.End or TokenKind.RParen || IsKeyword("and") || IsKeyword("or"))
            {
                if (path.ValueFilter is not null)
                    return new ScimFilterExpression.ValuePathExists(path);
                throw new ScimFilterFormatException(
                    $"Attribute '{path}' is missing an operator (expected pr, eq, ne, co, sw, ew, gt, ge, lt or le).");
            }

            if (IsKeyword("pr"))
            {
                _pos++;
                return new ScimFilterExpression.Present(path);
            }

            var op = ParseComparisonOperator();
            var value = ParseComparisonValue();
            return new ScimFilterExpression.Comparison(path, op, value);
        }

        /// <summary>attrPath, with an optional value filter and trailing sub-attributes.</summary>
        private ScimAttributePath ParseAttributePath()
        {
            var raw = Current.Text;
            _pos++;

            string? urn = null;
            // A URN-prefixed path is urn:...:Attribute — the attribute is everything after the last colon.
            var lastColon = raw.LastIndexOf(':');
            if (lastColon >= 0)
            {
                urn = raw[..lastColon];
                raw = raw[(lastColon + 1)..];
                if (raw.Length == 0)
                    throw new ScimFilterFormatException($"Attribute path '{urn}:' names no attribute.");
            }

            var segments = new List<string>(raw.Split('.', StringSplitOptions.RemoveEmptyEntries));
            if (segments.Count == 0)
                throw new ScimFilterFormatException("Empty attribute path in filter.");

            ScimFilterExpression? valueFilter = null;
            var valueFilterIndex = -1;
            if (Current.Kind == TokenKind.LBracket)
            {
                _pos++;
                // The value-path bracket is the second recursive descent, and nests just as deeply.
                EnterDepth();
                valueFilter = ParseExpression();
                ExitDepth();
                Expect(TokenKind.RBracket, "]");
                valueFilterIndex = segments.Count - 1;

                // Sub-attributes after the bracket: emails[type eq "work"].value
                if (Current.Kind == TokenKind.Identifier && Current.Text.StartsWith('.'))
                {
                    segments.AddRange(Current.Text.Split('.', StringSplitOptions.RemoveEmptyEntries));
                    _pos++;
                }
            }

            return new ScimAttributePath(urn, segments, valueFilter, valueFilterIndex);
        }

        private ComparisonOperator ParseComparisonOperator()
        {
            if (Current.Kind != TokenKind.Identifier)
                throw new ScimFilterFormatException($"Expected a comparison operator but found '{Current.Text}'.");

            var text = Current.Text.ToLowerInvariant();
            _pos++;
            return text switch
            {
                "eq" => ComparisonOperator.Eq,
                "ne" => ComparisonOperator.Ne,
                "co" => ComparisonOperator.Co,
                "sw" => ComparisonOperator.Sw,
                "ew" => ComparisonOperator.Ew,
                "gt" => ComparisonOperator.Gt,
                "ge" => ComparisonOperator.Ge,
                "lt" => ComparisonOperator.Lt,
                "le" => ComparisonOperator.Le,
                _ => throw new ScimFilterFormatException(
                    $"'{text}' is not a SCIM comparison operator (expected eq, ne, co, sw, ew, gt, ge, lt, le or pr)."),
            };
        }

        private ScimComparisonValue ParseComparisonValue()
        {
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.String: _pos++; return ScimComparisonValue.FromString(token.Text);
                case TokenKind.Number: _pos++; return ScimComparisonValue.FromNumber(token.Number);
                case TokenKind.True: _pos++; return ScimComparisonValue.FromBoolean(true);
                case TokenKind.False: _pos++; return ScimComparisonValue.FromBoolean(false);
                case TokenKind.Null: _pos++; return ScimComparisonValue.Null;
                default:
                    throw new ScimFilterFormatException(
                        token.Kind == TokenKind.End
                            ? "Filter ended unexpectedly; expected a comparison value."
                            : $"Expected a comparison value but found '{token.Text}'.");
            }
        }

        private void Expect(TokenKind kind, string display)
        {
            if (Current.Kind != kind)
                throw new ScimFilterFormatException($"Expected '{display}' in filter.");
            _pos++;
        }
    }
}
