using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SapB1.Mock.OData;

/// <summary>
/// Intérprete del subconjunto de <c>$filter</c> de OData v3 que usa el hub:
/// comparaciones (<c>eq ne gt ge lt le</c>), <c>and</c>, <c>or</c>, <c>not</c>, paréntesis y las
/// funciones <c>startswith</c>, <c>endswith</c>, <c>contains</c> y <c>substringof</c>.
/// Las comparaciones de texto distinguen mayúsculas y minúsculas.
/// </summary>
internal static class ODataFilterParser
{
    private static readonly string[] ComparisonOperators = ["eq", "ne", "gt", "ge", "lt", "le"];
    private static readonly string[] Functions = ["startswith", "endswith", "contains", "substringof"];

    /// <param name="filter">Valor de <c>$filter</c>.</param>
    /// <param name="knownProperties">
    /// Propiedades válidas de la entidad; cualquier otra es un error. Si es <see langword="null"/> (colección vacía,
    /// de la que no se pueden inferir) solo se valida la sintaxis.
    /// </param>
    public static Func<JsonObject, bool> Parse(string filter, IReadOnlySet<string>? knownProperties)
    {
        var parser = new Parser(Tokenize(filter), knownProperties);
        var predicate = parser.ParseOr();
        parser.ExpectEnd();
        return predicate;
    }

    private enum TokenKind
    {
        Identifier,
        String,
        Number,
        OpenParen,
        CloseParen,
        Comma,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '(')
            {
                tokens.Add(new Token(TokenKind.OpenParen, "("));
                i++;
            }
            else if (c == ')')
            {
                tokens.Add(new Token(TokenKind.CloseParen, ")"));
                i++;
            }
            else if (c == ',')
            {
                tokens.Add(new Token(TokenKind.Comma, ","));
                i++;
            }
            else if (c == '\'')
            {
                tokens.Add(ReadString(text, ref i));
            }
            else if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                var start = i;
                i++;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, text[start..i]));
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start..i]));
            }
            else
            {
                throw new ODataQueryException($"Unexpected character '{c}' in $filter.");
            }
        }

        return tokens;
    }

    private static Token ReadString(string text, ref int i)
    {
        var builder = new StringBuilder();
        i++; // comilla de apertura

        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                // Dos comillas seguidas representan una comilla literal.
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    builder.Append('\'');
                    i += 2;
                    continue;
                }

                i++;
                return new Token(TokenKind.String, builder.ToString());
            }

            builder.Append(text[i]);
            i++;
        }

        throw new ODataQueryException("Unterminated string literal in $filter.");
    }

    private sealed class Parser(List<Token> tokens, IReadOnlySet<string>? knownProperties)
    {
        private int _position;

        public void ExpectEnd()
        {
            if (_position < tokens.Count)
            {
                throw new ODataQueryException($"Unexpected '{tokens[_position].Text}' in $filter.");
            }
        }

        public Func<JsonObject, bool> ParseOr()
        {
            var left = ParseAnd();
            while (PeekKeyword("or"))
            {
                _position++;
                var (l, r) = (left, ParseAnd());
                left = row => l(row) || r(row);
            }

            return left;
        }

        private Func<JsonObject, bool> ParseAnd()
        {
            var left = ParseNot();
            while (PeekKeyword("and"))
            {
                _position++;
                var (l, r) = (left, ParseNot());
                left = row => l(row) && r(row);
            }

            return left;
        }

        private Func<JsonObject, bool> ParseNot()
        {
            if (PeekKeyword("not"))
            {
                _position++;
                var inner = ParseNot();
                return row => !inner(row);
            }

            return ParsePrimary();
        }

        private Func<JsonObject, bool> ParsePrimary()
        {
            if (Peek() is { Kind: TokenKind.OpenParen })
            {
                _position++;
                var inner = ParseOr();
                Expect(TokenKind.CloseParen);
                return inner;
            }

            if (Peek() is { Kind: TokenKind.Identifier } name
                && Functions.Contains(name.Text, StringComparer.Ordinal)
                && PeekAt(1) is { Kind: TokenKind.OpenParen })
            {
                return ParseFunction();
            }

            return ParseComparison();
        }

        private Func<JsonObject, bool> ParseFunction()
        {
            var name = Next().Text;
            Expect(TokenKind.OpenParen);
            var first = ParseOperand();
            Expect(TokenKind.Comma);
            var second = ParseOperand();
            Expect(TokenKind.CloseParen);

            // substringof('texto', Propiedad) invierte el orden de los argumentos respecto de contains.
            var (haystack, needle) = name == "substringof" ? (second, first) : (first, second);

            Func<string, string, bool> test = name switch
            {
                "startswith" => (h, n) => h.StartsWith(n, StringComparison.Ordinal),
                "endswith" => (h, n) => h.EndsWith(n, StringComparison.Ordinal),
                _ => (h, n) => h.Contains(n, StringComparison.Ordinal),
            };

            return row => ODataValues.TryGetString(haystack(row), out var h)
                && ODataValues.TryGetString(needle(row), out var n)
                && test(h, n);
        }

        private Func<JsonObject, bool> ParseComparison()
        {
            var left = ParseOperand();
            var op = Next();

            if (op.Kind != TokenKind.Identifier || !ComparisonOperators.Contains(op.Text, StringComparer.Ordinal))
            {
                throw new ODataQueryException($"Expected a comparison operator in $filter but found '{op.Text}'.");
            }

            var right = ParseOperand();

            return row =>
            {
                var comparison = ODataValues.Compare(left(row), right(row));
                return op.Text switch
                {
                    "eq" => comparison == 0,
                    "ne" => comparison != 0,
                    "gt" => comparison > 0,
                    "ge" => comparison >= 0,
                    "lt" => comparison < 0,
                    _ => comparison <= 0,
                };
            };
        }

        private Func<JsonObject, JsonNode?> ParseOperand()
        {
            var token = Next();

            switch (token.Kind)
            {
                case TokenKind.String:
                    return _ => JsonValue.Create(token.Text);

                case TokenKind.Number:
                    if (!decimal.TryParse(token.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                    {
                        throw new ODataQueryException($"Invalid number '{token.Text}' in $filter.");
                    }

                    return _ => JsonValue.Create(number);

                case TokenKind.Identifier when token.Text is "true" or "false":
                    var flag = token.Text == "true";
                    return _ => JsonValue.Create(flag);

                case TokenKind.Identifier when token.Text == "null":
                    return _ => null;

                case TokenKind.Identifier:
                    if (knownProperties is not null && !knownProperties.Contains(token.Text))
                    {
                        throw new ODataQueryException($"Property '{token.Text}' is invalid.");
                    }

                    return row => row[token.Text];

                default:
                    throw new ODataQueryException($"Unexpected '{token.Text}' in $filter.");
            }
        }

        private Token? Peek() => PeekAt(0);

        private Token? PeekAt(int offset) =>
            _position + offset < tokens.Count ? tokens[_position + offset] : null;

        private bool PeekKeyword(string keyword) =>
            Peek() is { Kind: TokenKind.Identifier } token && token.Text == keyword;

        private Token Next()
        {
            if (_position >= tokens.Count)
            {
                throw new ODataQueryException("Unexpected end of $filter.");
            }

            return tokens[_position++];
        }

        private void Expect(TokenKind kind)
        {
            var token = Next();
            if (token.Kind != kind)
            {
                throw new ODataQueryException($"Unexpected '{token.Text}' in $filter.");
            }
        }
    }
}
