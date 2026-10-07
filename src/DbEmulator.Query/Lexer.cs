using System.Globalization;
using System.Text;

namespace DbEmulator.Query;

internal enum TokenKind
{
    End,
    Identifier,
    Number,
    String,
    Parameter,
    Symbol,
}

internal enum Keyword
{
    None,
    Select,
    From,
    Where,
    And,
    Or,
    Not,
    In,
    Between,
    Like,
    Escape,
    As,
    Join,
    Order,
    By,
    Group,
    Asc,
    Desc,
    Distinct,
    Top,
    Value,
    Offset,
    Limit,
    Exists,
    Array,
    True,
    False,
    Null,
    Undefined,
}

/// <summary>
/// A lexical token. Keywords are Identifier tokens with <see cref="Keyword"/> set, so that they can still be used
/// as property names after '.' (e.g. <c>c.value</c>).
/// </summary>
internal readonly record struct Token(
    TokenKind Kind,
    string Text,
    int Position,
    Keyword Keyword = Keyword.None,
    double NumberValue = 0,
    string? StringValue = null)
{
    public bool IsWord => Kind == TokenKind.Identifier;

    public bool IsPlainIdentifier => Kind == TokenKind.Identifier && Keyword == Keyword.None;
}

internal static class Lexer
{
    private static readonly Dictionary<string, Keyword> s_keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = Keyword.Select,
        ["FROM"] = Keyword.From,
        ["WHERE"] = Keyword.Where,
        ["AND"] = Keyword.And,
        ["OR"] = Keyword.Or,
        ["NOT"] = Keyword.Not,
        ["IN"] = Keyword.In,
        ["BETWEEN"] = Keyword.Between,
        ["LIKE"] = Keyword.Like,
        ["ESCAPE"] = Keyword.Escape,
        ["AS"] = Keyword.As,
        ["JOIN"] = Keyword.Join,
        ["ORDER"] = Keyword.Order,
        ["BY"] = Keyword.By,
        ["GROUP"] = Keyword.Group,
        ["ASC"] = Keyword.Asc,
        ["DESC"] = Keyword.Desc,
        ["DISTINCT"] = Keyword.Distinct,
        ["TOP"] = Keyword.Top,
        ["VALUE"] = Keyword.Value,
        ["OFFSET"] = Keyword.Offset,
        ["LIMIT"] = Keyword.Limit,
        ["EXISTS"] = Keyword.Exists,
        ["ARRAY"] = Keyword.Array,
        ["TRUE"] = Keyword.True,
        ["FALSE"] = Keyword.False,
        ["NULL"] = Keyword.Null,
        ["UNDEFINED"] = Keyword.Undefined,
    };

    // Longest symbols first.
    private static readonly string[] s_symbols =
    [
        ">>>", "<<", ">>", "<=", ">=", "<>", "!=", "??", "||",
        "(", ")", "[", "]", "{", "}", ",", ".", ":", "?", "*", "/", "%", "+", "-", "|", "^", "&", "~", "=", "<", ">",
    ];

    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        int n = text.Length;
        while (true)
        {
            SkipTrivia(text, ref i);
            if (i >= n)
            {
                tokens.Add(new Token(TokenKind.End, string.Empty, n));
                return tokens;
            }

            int start = i;
            char ch = text[i];
            if (IsIdentifierStart(ch))
            {
                while (i < n && IsIdentifierPart(text[i]))
                {
                    i++;
                }

                string word = text[start..i];
                s_keywords.TryGetValue(word, out var keyword);
                tokens.Add(new Token(TokenKind.Identifier, word, start, keyword));
                continue;
            }

            if (char.IsAsciiDigit(ch) || (ch == '.' && i + 1 < n && char.IsAsciiDigit(text[i + 1]) && !PreviousEndsValue(tokens)))
            {
                tokens.Add(LexNumber(text, ref i));
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                tokens.Add(LexString(text, ref i));
                continue;
            }

            if (ch == '@')
            {
                i++;
                while (i < n && IsIdentifierPart(text[i]))
                {
                    i++;
                }

                if (i == start + 1)
                {
                    throw SyntaxError("@", start);
                }

                tokens.Add(new Token(TokenKind.Parameter, text[start..i], start));
                continue;
            }

            string? symbol = null;
            foreach (var s in s_symbols)
            {
                if (string.CompareOrdinal(text, i, s, 0, s.Length) == 0)
                {
                    symbol = s;
                    break;
                }
            }

            if (symbol is null)
            {
                throw SyntaxError(ch.ToString(), start);
            }

            i += symbol.Length;
            tokens.Add(new Token(TokenKind.Symbol, symbol, start));
        }
    }

    public static SqlQueryException SyntaxError(string near, int position) =>
        new($"Syntax error near '{near}' at position {position}.");

    private static void SkipTrivia(string text, ref int i)
    {
        int n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < n && text[i + 1] == '-')
            {
                i += 2;
                while (i < n && text[i] != '\n' && text[i] != '\r')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new SqlQueryException($"Syntax error: unterminated comment starting at position {i}.");
                }

                i = end + 2;
            }
            else
            {
                return;
            }
        }
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c == '$';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static bool PreviousEndsValue(List<Token> tokens)
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        var t = tokens[^1];
        if (t.Kind == TokenKind.Identifier)
        {
            // A keyword such as VALUE or SELECT does not end a value ("SELECT VALUE .5"), unless it is a literal keyword
            // or a property name following '.'.
            bool afterDot = tokens.Count > 1 && tokens[^2].Kind == TokenKind.Symbol && tokens[^2].Text == ".";
            return afterDot || t.Keyword is Keyword.None or Keyword.True or Keyword.False or Keyword.Null or Keyword.Undefined;
        }

        return t.Kind is TokenKind.Number or TokenKind.String or TokenKind.Parameter
            || (t.Kind == TokenKind.Symbol && (t.Text == ")" || t.Text == "]" || t.Text == "}"));
    }

    private static Token LexNumber(string text, ref int i)
    {
        int start = i;
        int n = text.Length;
        if (text[i] == '0' && i + 1 < n && (text[i + 1] == 'x' || text[i + 1] == 'X'))
        {
            i += 2;
            int digitsStart = i;
            while (i < n && char.IsAsciiHexDigit(text[i]))
            {
                i++;
            }

            if (i == digitsStart || (i < n && IsIdentifierPart(text[i])))
            {
                throw SyntaxError(text[start..Math.Min(n, i + 1)], start);
            }

            if (!ulong.TryParse(text.AsSpan(digitsStart, i - digitsStart), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex))
            {
                throw new SqlQueryException($"Syntax error: number literal '{text[start..i]}' at position {start} is out of range.");
            }

            return new Token(TokenKind.Number, text[start..i], start, NumberValue: hex);
        }

        while (i < n && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i + 1 < n && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
        {
            i++;
            while (i < n && char.IsAsciiDigit(text[i]))
            {
                i++;
            }
        }

        if (i < n && (text[i] == 'e' || text[i] == 'E'))
        {
            int save = i;
            i++;
            if (i < n && (text[i] == '+' || text[i] == '-'))
            {
                i++;
            }

            if (i < n && char.IsAsciiDigit(text[i]))
            {
                while (i < n && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
            }
            else
            {
                i = save;
            }
        }

        string literal = text[start..i];
        double value = double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value))
        {
            throw new SqlQueryException($"Syntax error: number literal '{literal}' at position {start} is out of range.");
        }

        return new Token(TokenKind.Number, literal, start, NumberValue: value);
    }

    private static Token LexString(string text, ref int i)
    {
        int start = i;
        char quote = text[i++];
        int n = text.Length;
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= n)
            {
                throw new SqlQueryException($"Syntax error: unterminated string literal starting at position {start}.");
            }

            char c = text[i++];
            if (c == quote)
            {
                break;
            }

            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            if (i >= n)
            {
                throw new SqlQueryException($"Syntax error: unterminated string literal starting at position {start}.");
            }

            char e = text[i++];
            switch (e)
            {
                case '\\':
                case '\'':
                case '"':
                case '/':
                    sb.Append(e);
                    break;
                case 'b':
                    sb.Append('\b');
                    break;
                case 'f':
                    sb.Append('\f');
                    break;
                case 'n':
                    sb.Append('\n');
                    break;
                case 'r':
                    sb.Append('\r');
                    break;
                case 't':
                    sb.Append('\t');
                    break;
                case 'u':
                    if (i + 4 > n || !ushort.TryParse(text.AsSpan(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code))
                    {
                        throw new SqlQueryException($"Syntax error: invalid unicode escape sequence at position {i - 2}.");
                    }

                    sb.Append((char)code);
                    i += 4;
                    break;
                default:
                    throw new SqlQueryException($"Syntax error: invalid escape sequence '\\{e}' at position {i - 2}.");
            }
        }

        return new Token(TokenKind.String, text[start..i], start, StringValue: sb.ToString());
    }
}
