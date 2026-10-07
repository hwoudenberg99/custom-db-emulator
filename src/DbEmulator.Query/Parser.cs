namespace DbEmulator.Query;

/// <summary>Recursive-descent parser for the Cosmos DB NoSQL query language.</summary>
internal sealed class Parser
{
    /// <summary>Maximum nesting of parenthesized expressions / subqueries (bounds parser recursion).</summary>
    private const int MaxRecursion = 128;

    /// <summary>Maximum AST depth (bounds compiler and evaluator recursion, e.g. for very long operator chains).</summary>
    private const int MaxExpressionDepth = 1000;

    private static readonly Dictionary<string, BinaryOp>[] s_binaryLevels =
    [
        new() { ["|"] = BinaryOp.BitOr },
        new() { ["^"] = BinaryOp.BitXor },
        new() { ["&"] = BinaryOp.BitAnd },
        new() { ["<<"] = BinaryOp.ShiftLeft, [">>"] = BinaryOp.ShiftRight, [">>>"] = BinaryOp.ShiftRightZeroFill },
        new() { ["+"] = BinaryOp.Add, ["-"] = BinaryOp.Subtract, ["||"] = BinaryOp.Concat },
        new() { ["*"] = BinaryOp.Multiply, ["/"] = BinaryOp.Divide, ["%"] = BinaryOp.Modulo },
    ];

    private readonly List<Token> _tokens;
    private int _pos;
    private int _recursion;

    private Parser(List<Token> tokens)
    {
        _tokens = tokens;
    }

    private Token Current => _tokens[_pos];

    public static QueryAst Parse(string text)
    {
        var parser = new Parser(Lexer.Tokenize(text));
        var query = parser.ParseQuery();
        if (parser.Current.Kind != TokenKind.End)
        {
            throw Error(parser.Current);
        }

        return query;
    }

    // ------------------------------------------------------------------ token helpers

    private Token Peek(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];

    private Token Advance()
    {
        var t = _tokens[_pos];
        if (_pos < _tokens.Count - 1)
        {
            _pos++;
        }

        return t;
    }

    private bool IsKeyword(Keyword keyword, int offset = 0)
    {
        var t = Peek(offset);
        return t.Kind == TokenKind.Identifier && t.Keyword == keyword;
    }

    private bool IsSymbol(string symbol, int offset = 0)
    {
        var t = Peek(offset);
        return t.Kind == TokenKind.Symbol && t.Text == symbol;
    }

    private bool AcceptKeyword(Keyword keyword)
    {
        if (!IsKeyword(keyword))
        {
            return false;
        }

        Advance();
        return true;
    }

    private bool AcceptSymbol(string symbol)
    {
        if (!IsSymbol(symbol))
        {
            return false;
        }

        Advance();
        return true;
    }

    private Token ExpectKeyword(Keyword keyword)
    {
        if (!IsKeyword(keyword))
        {
            throw Error(Current);
        }

        return Advance();
    }

    private Token ExpectSymbol(string symbol)
    {
        if (!IsSymbol(symbol))
        {
            throw Error(Current);
        }

        return Advance();
    }

    private static SqlQueryException Error(Token t) =>
        t.Kind == TokenKind.End
            ? new SqlQueryException($"Syntax error: unexpected end of query at position {t.Position}.")
            : Lexer.SyntaxError(t.Text, t.Position);

    private void Enter()
    {
        if (++_recursion > MaxRecursion)
        {
            throw new SqlQueryException("The query is too deeply nested.");
        }
    }

    private void Leave() => _recursion--;

    private static T Check<T>(T node)
        where T : SqlExpr
    {
        if (node.Depth > MaxExpressionDepth)
        {
            throw new SqlQueryException("The query expression is too deeply nested.");
        }

        return node;
    }

    // ------------------------------------------------------------------ query

    private QueryAst ParseQuery()
    {
        Enter();
        ExpectKeyword(Keyword.Select);
        bool distinct = false;
        CountSpec? top = null;
        while (true)
        {
            if (!distinct && AcceptKeyword(Keyword.Distinct))
            {
                distinct = true;
                continue;
            }

            if (top is null && AcceptKeyword(Keyword.Top))
            {
                top = ParseCount();
                continue;
            }

            break;
        }

        var query = new QueryAst { Select = ParseSelectClause(), Distinct = distinct, Top = top };

        if (AcceptKeyword(Keyword.From))
        {
            query.From.Add(ParseSource());
            while (AcceptKeyword(Keyword.Join))
            {
                query.From.Add(ParseSource());
            }
        }

        if (AcceptKeyword(Keyword.Where))
        {
            query.Where = ParseExpression();
        }

        if (AcceptKeyword(Keyword.Group))
        {
            ExpectKeyword(Keyword.By);
            var groupBy = new List<SqlExpr>();
            do
            {
                groupBy.Add(ParseExpression());
            }
            while (AcceptSymbol(","));
            query.GroupBy = groupBy;
        }

        if (AcceptKeyword(Keyword.Order))
        {
            ExpectKeyword(Keyword.By);
            query.OrderBy = ParseOrderBy();
        }

        if (AcceptKeyword(Keyword.Offset))
        {
            query.Offset = ParseCount();
            ExpectKeyword(Keyword.Limit);
            query.Limit = ParseCount();
        }

        query.Depth = ComputeDepth(query);
        Leave();
        return query;
    }

    private static int ComputeDepth(QueryAst q)
    {
        int max = 0;
        void Visit(SqlExpr? e)
        {
            if (e is not null)
            {
                max = Math.Max(max, e.Depth);
            }
        }

        Visit(q.Select.ValueExpr);
        foreach (var item in q.Select.Items)
        {
            Visit(item.Expr);
        }

        foreach (var source in q.From)
        {
            Visit(source.Expr);
            if (source.Subquery is not null)
            {
                max = Math.Max(max, source.Subquery.Depth);
            }
        }

        Visit(q.Where);
        q.GroupBy?.ForEach(Visit);
        q.OrderBy?.ForEach(o => Visit(o.Expr));
        if (max + 1 > MaxExpressionDepth)
        {
            throw new SqlQueryException("The query expression is too deeply nested.");
        }

        return max + 1;
    }

    private SelectClause ParseSelectClause()
    {
        int position = Current.Position;
        if (AcceptSymbol("*"))
        {
            return new SelectClause { Kind = SelectKind.Star, Position = position };
        }

        if (AcceptKeyword(Keyword.Value))
        {
            return new SelectClause { Kind = SelectKind.Value, ValueExpr = ParseExpression(), Position = position };
        }

        var items = new List<SelectItem>();
        do
        {
            var expr = ParseExpression();
            items.Add(new SelectItem(expr, ParseOptionalAlias()));
        }
        while (AcceptSymbol(","));

        return new SelectClause { Kind = SelectKind.List, Items = items, Position = position };
    }

    private string? ParseOptionalAlias()
    {
        if (AcceptKeyword(Keyword.As))
        {
            var t = Current;
            if (!t.IsWord)
            {
                throw Error(t);
            }

            Advance();
            return t.Text;
        }

        return Current.IsPlainIdentifier ? Advance().Text : null;
    }

    private FromSource ParseSource()
    {
        int position = Current.Position;
        if (IsSymbol("(") && IsKeyword(Keyword.Select, 1))
        {
            Advance();
            var subquery = ParseQuery();
            ExpectSymbol(")");
            return new FromSource { Kind = SourceKind.Subquery, Subquery = subquery, Alias = ParseOptionalAlias(), Position = position };
        }

        if (Current.IsPlainIdentifier && IsKeyword(Keyword.In, 1))
        {
            string alias = Advance().Text;
            Advance();
            return new FromSource { Kind = SourceKind.In, Expr = ParsePostfix(), Alias = alias, Position = position };
        }

        var expr = ParsePostfix();
        return new FromSource { Kind = SourceKind.Expr, Expr = expr, Alias = ParseOptionalAlias(), Position = position };
    }

    private List<OrderByItem> ParseOrderBy()
    {
        var items = new List<OrderByItem>();
        do
        {
            var t = Current;
            if (t.IsPlainIdentifier && string.Equals(t.Text, "RANK", StringComparison.OrdinalIgnoreCase) && Peek(1).IsPlainIdentifier)
            {
                throw new SqlQueryException("ORDER BY RANK (full-text / hybrid search ranking) is not supported by DbEmulator.");
            }

            var expr = ParseExpression();
            bool descending = false;
            if (AcceptKeyword(Keyword.Desc))
            {
                descending = true;
            }
            else
            {
                AcceptKeyword(Keyword.Asc);
            }

            items.Add(new OrderByItem(expr, descending));
        }
        while (AcceptSymbol(","));

        return items;
    }

    private CountSpec ParseCount()
    {
        var t = Current;
        if (t.Kind == TokenKind.Number)
        {
            Advance();
            double v = t.NumberValue;
            if (v < 0 || v != Math.Floor(v) || v > long.MaxValue)
            {
                throw new SqlQueryException($"Syntax error: '{t.Text}' at position {t.Position} is not a valid non-negative integer.");
            }

            return new CountSpec((long)v, null, t.Position);
        }

        if (t.Kind == TokenKind.Parameter)
        {
            Advance();
            return new CountSpec(0, t.Text, t.Position);
        }

        throw Error(t);
    }

    // ------------------------------------------------------------------ expressions (lowest to highest precedence)

    private SqlExpr ParseExpression()
    {
        Enter();
        var e = ParseTernary();
        Leave();
        return e;
    }

    private SqlExpr ParseTernary()
    {
        var condition = ParseCoalesce();
        if (!IsSymbol("?"))
        {
            return condition;
        }

        var q = Advance();
        var ifTrue = ParseExpression();
        ExpectSymbol(":");
        var ifFalse = ParseExpression();
        return Check(new TernaryExpr(condition, ifTrue, ifFalse, q.Position));
    }

    private SqlExpr ParseCoalesce()
    {
        var left = ParseOr();
        while (IsSymbol("??"))
        {
            var t = Advance();
            var right = ParseOr();
            left = Check(new BinaryExpr(BinaryOp.Coalesce, left, right, t.Position));
        }

        return left;
    }

    private SqlExpr ParseOr()
    {
        var left = ParseAnd();
        while (IsKeyword(Keyword.Or))
        {
            var t = Advance();
            var right = ParseAnd();
            left = Check(new BinaryExpr(BinaryOp.Or, left, right, t.Position));
        }

        return left;
    }

    private SqlExpr ParseAnd()
    {
        var left = ParseNot();
        while (IsKeyword(Keyword.And))
        {
            var t = Advance();
            var right = ParseNot();
            left = Check(new BinaryExpr(BinaryOp.And, left, right, t.Position));
        }

        return left;
    }

    private SqlExpr ParseNot()
    {
        if (!IsKeyword(Keyword.Not))
        {
            return ParseComparison();
        }

        var t = Advance();
        Enter();
        var operand = ParseNot();
        Leave();
        return Check(new UnaryExpr(UnaryOp.Not, operand, t.Position));
    }

    private SqlExpr ParseComparison()
    {
        var left = ParseBinary(0);
        while (true)
        {
            var t = Current;
            if (t.Kind == TokenKind.Symbol)
            {
                BinaryOp? op = t.Text switch
                {
                    "=" => BinaryOp.Equal,
                    "!=" or "<>" => BinaryOp.NotEqual,
                    "<" => BinaryOp.Less,
                    "<=" => BinaryOp.LessOrEqual,
                    ">" => BinaryOp.Greater,
                    ">=" => BinaryOp.GreaterOrEqual,
                    _ => null,
                };
                if (op is null)
                {
                    return left;
                }

                Advance();
                var right = ParseBinary(0);
                left = Check(new BinaryExpr(op.Value, left, right, t.Position));
                continue;
            }

            bool negated = false;
            if (IsKeyword(Keyword.Not) && (IsKeyword(Keyword.In, 1) || IsKeyword(Keyword.Between, 1) || IsKeyword(Keyword.Like, 1)))
            {
                Advance();
                negated = true;
            }

            if (AcceptKeyword(Keyword.In))
            {
                ExpectSymbol("(");
                var items = new List<SqlExpr>();
                do
                {
                    items.Add(ParseExpression());
                }
                while (AcceptSymbol(","));
                ExpectSymbol(")");
                left = Check(new InExpr(left, items, negated, t.Position));
            }
            else if (AcceptKeyword(Keyword.Between))
            {
                var low = ParseBinary(0);
                ExpectKeyword(Keyword.And);
                var high = ParseBinary(0);
                left = Check(new BetweenExpr(left, low, high, negated, t.Position));
            }
            else if (AcceptKeyword(Keyword.Like))
            {
                var pattern = ParseBinary(0);
                SqlExpr? escape = AcceptKeyword(Keyword.Escape) ? ParseBinary(0) : null;
                left = Check(new LikeExpr(left, pattern, escape, negated, t.Position));
            }
            else
            {
                return left;
            }
        }
    }

    /// <summary>Left-associative binary levels: | ^ &amp; shifts additive multiplicative.</summary>
    private SqlExpr ParseBinary(int level)
    {
        if (level == s_binaryLevels.Length)
        {
            return ParseUnary();
        }

        var left = ParseBinary(level + 1);
        var ops = s_binaryLevels[level];
        while (Current.Kind == TokenKind.Symbol && ops.TryGetValue(Current.Text, out var op))
        {
            var t = Advance();
            var right = ParseBinary(level + 1);
            left = Check(new BinaryExpr(op, left, right, t.Position));
        }

        return left;
    }

    private SqlExpr ParseUnary()
    {
        var t = Current;
        if (t.Kind != TokenKind.Symbol || (t.Text != "-" && t.Text != "+" && t.Text != "~"))
        {
            return ParsePostfix();
        }

        Advance();
        Enter();
        var operand = ParseUnary();
        Leave();
        if (t.Text == "-" && operand is LiteralExpr { Value.Kind: QKind.Number } literal)
        {
            return new LiteralExpr(QValue.Num(-literal.Value.AsNumber), t.Position);
        }

        var op = t.Text switch
        {
            "-" => UnaryOp.Negate,
            "+" => UnaryOp.Plus,
            _ => UnaryOp.BitNot,
        };
        return Check(new UnaryExpr(op, operand, t.Position));
    }

    private SqlExpr ParsePostfix()
    {
        var e = ParsePrimary();
        while (true)
        {
            if (IsSymbol("."))
            {
                var dot = Advance();
                var name = Current;
                if (!name.IsWord)
                {
                    throw Error(name);
                }

                Advance();
                e = Check(new MemberExpr(e, name.Text, dot.Position));
            }
            else if (IsSymbol("["))
            {
                var open = Advance();
                var index = ParseExpression();
                ExpectSymbol("]");
                e = Check(new IndexExpr(e, index, open.Position));
            }
            else
            {
                return e;
            }
        }
    }

    private SqlExpr ParsePrimary()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new LiteralExpr(QValue.Num(t.NumberValue), t.Position);
            case TokenKind.String:
                Advance();
                return new LiteralExpr(QValue.Str(t.StringValue!), t.Position);
            case TokenKind.Parameter:
                Advance();
                return new ParameterExpr(t.Text, t.Position);
            case TokenKind.Identifier:
                switch (t.Keyword)
                {
                    case Keyword.True:
                        Advance();
                        return new LiteralExpr(QValue.True, t.Position);
                    case Keyword.False:
                        Advance();
                        return new LiteralExpr(QValue.False, t.Position);
                    case Keyword.Null:
                        Advance();
                        return new LiteralExpr(QValue.Null, t.Position);
                    case Keyword.Undefined:
                        Advance();
                        return new LiteralExpr(default, t.Position);
                    case Keyword.Exists:
                        Advance();
                        return ParseSubqueryCall(SubqueryKind.Exists, t.Position);
                    case Keyword.Array when IsSymbol("(", 1):
                        Advance();
                        return ParseSubqueryCall(SubqueryKind.Array, t.Position);
                    case Keyword.None:
                        return ParseIdentifierOrCall();
                    default:
                        throw Error(t);
                }

            case TokenKind.Symbol:
                switch (t.Text)
                {
                    case "(":
                    {
                        Advance();
                        if (IsKeyword(Keyword.Select))
                        {
                            var query = ParseQuery();
                            ExpectSymbol(")");
                            return new SubqueryExpr(SubqueryKind.Scalar, query, t.Position);
                        }

                        var inner = ParseExpression();
                        ExpectSymbol(")");
                        return inner;
                    }

                    case "[":
                        return ParseArrayLiteral();
                    case "{":
                        return ParseObjectLiteral();
                    default:
                        throw Error(t);
                }

            default:
                throw Error(t);
        }
    }

    private SubqueryExpr ParseSubqueryCall(SubqueryKind kind, int position)
    {
        ExpectSymbol("(");
        if (!IsKeyword(Keyword.Select))
        {
            throw Error(Current);
        }

        var query = ParseQuery();
        ExpectSymbol(")");
        return new SubqueryExpr(kind, query, position);
    }

    private SqlExpr ParseIdentifierOrCall()
    {
        var t = Advance();
        if (string.Equals(t.Text, "udf", StringComparison.OrdinalIgnoreCase) && IsSymbol(".") && Peek(1).IsWord && IsSymbol("(", 2))
        {
            Advance();
            string name = Advance().Text;
            return new CallExpr(name, ParseArguments(), isUdf: true, t.Position);
        }

        if (IsSymbol("("))
        {
            return Check(new CallExpr(t.Text, ParseArguments(), isUdf: false, t.Position));
        }

        return new IdentifierExpr(t.Text, t.Position);
    }

    private List<SqlExpr> ParseArguments()
    {
        ExpectSymbol("(");
        var args = new List<SqlExpr>();
        if (!IsSymbol(")"))
        {
            do
            {
                args.Add(ParseExpression());
            }
            while (AcceptSymbol(","));
        }

        ExpectSymbol(")");
        return args;
    }

    private SqlExpr ParseArrayLiteral()
    {
        var open = ExpectSymbol("[");
        var items = new List<SqlExpr>();
        if (!IsSymbol("]"))
        {
            do
            {
                items.Add(ParseExpression());
            }
            while (AcceptSymbol(","));
        }

        ExpectSymbol("]");
        return Check(new ArrayLiteralExpr(items, open.Position));
    }

    private SqlExpr ParseObjectLiteral()
    {
        var open = ExpectSymbol("{");
        var properties = new List<KeyValuePair<string, SqlExpr>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!IsSymbol("}"))
        {
            do
            {
                var key = Current;
                string name;
                if (key.Kind == TokenKind.String)
                {
                    name = key.StringValue!;
                }
                else if (key.IsWord)
                {
                    name = key.Text;
                }
                else
                {
                    throw Error(key);
                }

                Advance();
                ExpectSymbol(":");
                var value = ParseExpression();
                if (!names.Add(name))
                {
                    throw new SqlQueryException($"The object property name '{name}' is specified more than once.");
                }

                properties.Add(new KeyValuePair<string, SqlExpr>(name, value));
            }
            while (AcceptSymbol(","));
        }

        ExpectSymbol("}");
        return Check(new ObjectLiteralExpr(properties, open.Position));
    }
}
