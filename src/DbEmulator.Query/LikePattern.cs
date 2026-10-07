namespace DbEmulator.Query;

/// <summary>
/// A compiled LIKE pattern: <c>%</c> (any sequence), <c>_</c> (any single character), <c>[abc]</c>, <c>[a-c]</c>,
/// <c>[^a-c]</c> character classes, and an optional escape character. Case-sensitive; matches the whole string.
/// </summary>
internal sealed class LikePattern
{
    private readonly Element[] _elements;

    private LikePattern(Element[] elements)
    {
        _elements = elements;
    }

    private enum ElementKind : byte
    {
        Literal,
        AnyChar,
        AnySequence,
        Class,
    }

    public static LikePattern Compile(string pattern, char? escape)
    {
        var elements = new List<Element>();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (escape.HasValue && c == escape.Value && i + 1 < pattern.Length)
            {
                elements.Add(Element.Literal(pattern[++i]));
                continue;
            }

            switch (c)
            {
                case '%':
                    if (elements.Count == 0 || elements[^1].Kind != ElementKind.AnySequence)
                    {
                        elements.Add(new Element(ElementKind.AnySequence, default, null, false));
                    }

                    break;
                case '_':
                    elements.Add(new Element(ElementKind.AnyChar, default, null, false));
                    break;
                case '[':
                    if (TryParseClass(pattern, ref i, out var cls))
                    {
                        elements.Add(cls);
                    }
                    else
                    {
                        elements.Add(Element.Literal(c));
                    }

                    break;
                default:
                    elements.Add(Element.Literal(c));
                    break;
            }
        }

        return new LikePattern(elements.ToArray());
    }

    private static bool TryParseClass(string pattern, ref int i, out Element element)
    {
        element = default;
        int start = i + 1;
        bool negated = start < pattern.Length && pattern[start] == '^';
        int contentStart = negated ? start + 1 : start;
        int close = pattern.IndexOf(']', contentStart);
        if (close < 0 || close == contentStart)
        {
            return false;
        }

        var ranges = new List<(char Low, char High)>();
        for (int j = contentStart; j < close; j++)
        {
            char low = pattern[j];
            if (j + 2 < close && pattern[j + 1] == '-')
            {
                char high = pattern[j + 2];
                ranges.Add(low <= high ? (low, high) : (high, low));
                j += 2;
            }
            else
            {
                ranges.Add((low, low));
            }
        }

        element = new Element(ElementKind.Class, default, ranges.ToArray(), negated);
        i = close;
        return true;
    }

    public bool IsMatch(string text)
    {
        var elements = _elements;
        int n = elements.Length;
        int ti = 0;
        int pi = 0;
        int starPi = -1;
        int starTi = 0;
        while (ti < text.Length)
        {
            if (pi < n && elements[pi].Kind == ElementKind.AnySequence)
            {
                starPi = pi++;
                starTi = ti;
            }
            else if (pi < n && elements[pi].Matches(text[ti]))
            {
                pi++;
                ti++;
            }
            else if (starPi >= 0)
            {
                pi = starPi + 1;
                ti = ++starTi;
            }
            else
            {
                return false;
            }
        }

        while (pi < n && elements[pi].Kind == ElementKind.AnySequence)
        {
            pi++;
        }

        return pi == n;
    }

    private readonly struct Element(ElementKind kind, char ch, (char Low, char High)[]? ranges, bool negated)
    {
        public ElementKind Kind { get; } = kind;

        public static Element Literal(char c) => new(ElementKind.Literal, c, null, false);

        public bool Matches(char c)
        {
            switch (Kind)
            {
                case ElementKind.Literal:
                    return c == ch;
                case ElementKind.AnyChar:
                    return true;
                case ElementKind.Class:
                    bool inClass = false;
                    foreach (var (low, high) in ranges!)
                    {
                        if (c >= low && c <= high)
                        {
                            inClass = true;
                            break;
                        }
                    }

                    return inClass != negated;
                default:
                    return false;
            }
        }
    }
}
