using System.Runtime.CompilerServices;
using System.Text;

namespace DbEmulator.Query;

internal delegate QValue UnaryOperator(in QValue a);

internal delegate QValue BinaryOperator(in QValue a, in QValue b);

/// <summary>A compiled expression plus compile-time facts used for folding and fast paths.</summary>
internal sealed class CExpr
{
    private CExpr(Eval fn, bool isConst, QValue value, int slot)
    {
        Fn = fn;
        IsConst = isConst;
        Value = value;
        Slot = slot;
    }

    public Eval Fn { get; }

    public bool IsConst { get; }

    /// <summary>The constant value (when <see cref="IsConst"/>).</summary>
    public QValue Value { get; }

    /// <summary>The slot index when the expression is a plain slot read, else -1.</summary>
    public int Slot { get; }

    public static CExpr Const(QValue value) => new(_ => value, true, value, -1);

    public static CExpr Dynamic(Eval fn) => new(fn, false, default, -1);

    public static CExpr SlotRead(int slot) => new(ctx => ctx.Slots[slot], false, default, slot);
}

/// <summary>Name resolution scope: the aliases of one (sub)query, chained to the enclosing query's scope.</summary>
internal sealed class Scope(Scope? parent)
{
    private readonly List<KeyValuePair<string, int>> _aliases = [];

    public void Add(string name, int slot) => _aliases.Add(new KeyValuePair<string, int>(name, slot));

    public bool TryResolve(string name, out int slot)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            var aliases = scope._aliases;
            for (int i = aliases.Count - 1; i >= 0; i--)
            {
                if (string.Equals(aliases[i].Key, name, StringComparison.Ordinal))
                {
                    slot = aliases[i].Value;
                    return true;
                }
            }
        }

        slot = -1;
        return false;
    }

    private Scope? Parent { get; } = parent;
}

/// <summary>Collects the aggregate calls of one query's SELECT (and ORDER BY) clause.</summary>
internal sealed class AggregateCollector
{
    public List<AggregateSpec> Items { get; } = [];
}

internal readonly record struct ExprEnv(Scope Scope, AggregateCollector? Aggregates, bool InAggregate);

[InlineArray(4)]
internal struct ArgBuffer4
{
    private QValue _element0;
}

/// <summary>Compiles a parsed query into a tree of <see cref="CompiledQuery"/> plans and <see cref="Eval"/> delegates.</summary>
internal sealed class QueryCompiler
{
    private readonly List<string> _parameters = [];
    private int _slotCount;

    public int SlotCount => _slotCount;

    public string[] ParameterNames => _parameters.ToArray();

    private int NewSlot() => _slotCount++;

    private int ParameterIndex(string name)
    {
        int index = _parameters.IndexOf(name);
        if (index < 0)
        {
            _parameters.Add(name);
            index = _parameters.Count - 1;
        }

        return index;
    }

    // ------------------------------------------------------------------ queries

    public CompiledQuery CompileQuery(QueryAst ast, Scope? parent, bool topLevel)
    {
        var plan = new CompiledQuery { IsTopLevel = topLevel, Distinct = ast.Distinct };
        var scope = new Scope(parent);
        var ownSlots = new List<int>();
        var sources = new List<SourcePlan>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < ast.From.Count; i++)
        {
            var source = ast.From[i];
            string? alias = source.Alias;
            SourcePlan sourcePlan;
            if (i == 0 && topLevel)
            {
                sourcePlan = CompileRootSource(plan, source, ownSlots, ref alias);
            }
            else if (source.Kind == SourceKind.Subquery)
            {
                var subquery = CompileQuery(source.Subquery!, scope, topLevel: false);
                sourcePlan = new SourcePlan(SourceMode.Subquery, null, subquery, NewSlot());
            }
            else
            {
                var compiled = Compile(source.Expr!, new ExprEnv(scope, null, false));
                var mode = source.Kind == SourceKind.In ? SourceMode.Iterate : SourceMode.Bind;
                sourcePlan = new SourcePlan(mode, compiled.Fn, null, NewSlot());
                if (source.Kind == SourceKind.Expr)
                {
                    alias ??= ImplicitAlias(source.Expr!);
                }
            }

            if (!ownSlots.Contains(sourcePlan.AliasSlot))
            {
                ownSlots.Add(sourcePlan.AliasSlot);
            }

            sources.Add(sourcePlan);
            if (alias is not null)
            {
                if (!aliases.Add(alias))
                {
                    throw new SqlQueryException($"The alias '{alias}' is specified more than once in the FROM clause.");
                }

                scope.Add(alias, sourcePlan.AliasSlot);
            }
        }

        plan.Sources = sources.ToArray();
        plan.OwnSlots = ownSlots.ToArray();

        var plainEnv = new ExprEnv(scope, null, false);
        if (ast.Where is not null)
        {
            var where = Compile(ast.Where, plainEnv);
            plan.Where = where.IsConst && where.Value.IsTrue ? null : where.Fn;
        }

        if (ast.GroupBy is not null)
        {
            plan.GroupBy = ast.GroupBy.Select(g => Compile(g, plainEnv).Fn).ToArray();
        }

        var aggregates = new AggregateCollector();
        var selectEnv = new ExprEnv(scope, aggregates, false);
        switch (ast.Select.Kind)
        {
            case SelectKind.Star:
                if (ast.From.Count == 0)
                {
                    throw new SqlQueryException("'SELECT *' is not valid if FROM clause is omitted.");
                }

                if (ast.From.Count > 1)
                {
                    throw new SqlQueryException("'SELECT *' is only valid with a single input set.");
                }

                if (ast.GroupBy is not null)
                {
                    throw new SqlQueryException("'SELECT *' is not valid with GROUP BY.");
                }

                int starSlot = plan.Sources[0].AliasSlot;
                plan.Projection = ctx => ctx.Slots[starSlot];
                break;
            case SelectKind.Value:
                plan.Projection = Compile(ast.Select.ValueExpr!, selectEnv).Fn;
                break;
            default:
                plan.Projection = CompileSelectList(ast.Select.Items, selectEnv);
                break;
        }

        plan.IsAggregate = aggregates.Items.Count > 0 || ast.GroupBy is not null;

        if (ast.OrderBy is not null)
        {
            var orderEnv = new ExprEnv(scope, plan.IsAggregate ? aggregates : null, false);
            plan.OrderBy = ast.OrderBy.Select(o => new OrderKey(Compile(o.Expr, orderEnv).Fn, o.Descending)).ToArray();
        }

        plan.Aggregates = aggregates.Items.ToArray();
        plan.Top = CompileCount(ast.Top, "TOP");
        plan.Offset = CompileCount(ast.Offset, "OFFSET");
        plan.Limit = CompileCount(ast.Limit, "LIMIT");
        return plan;
    }

    /// <summary>
    /// The first source of a top-level query reads the container: its leading identifier (whatever it is called)
    /// binds to the current document.
    /// </summary>
    private SourcePlan CompileRootSource(CompiledQuery plan, FromSource source, List<int> ownSlots, ref string? alias)
    {
        if (source.Kind == SourceKind.Subquery)
        {
            plan.FromSubquery = CompileQuery(source.Subquery!, null, topLevel: true);
            return new SourcePlan(SourceMode.External, null, null, NewSlot());
        }

        int rootSlot = NewSlot();
        plan.RootSlot = rootSlot;
        ownSlots.Add(rootSlot);
        var expr = source.Expr!;
        if (source.Kind == SourceKind.Expr && expr is IdentifierExpr identifier)
        {
            plan.FirstSourceIsRoot = true;
            alias ??= identifier.Name;
            return new SourcePlan(SourceMode.Bind, null, null, rootSlot);
        }

        var rootScope = new Scope(null);
        if (LeadingIdentifier(expr) is { } leading)
        {
            rootScope.Add(leading, rootSlot);
        }

        var compiled = Compile(expr, new ExprEnv(rootScope, null, false));
        if (source.Kind == SourceKind.Expr)
        {
            alias ??= ImplicitAlias(expr);
        }

        var mode = source.Kind == SourceKind.In ? SourceMode.Iterate : SourceMode.Bind;
        return new SourcePlan(mode, compiled.Fn, null, NewSlot());
    }

    private CountPlan? CompileCount(CountSpec? spec, string clause)
    {
        if (spec is null)
        {
            return null;
        }

        return spec.ParameterName is null
            ? new CountPlan(spec.Literal, -1, clause)
            : new CountPlan(0, ParameterIndex(spec.ParameterName), clause);
    }

    private Eval CompileSelectList(List<SelectItem> items, ExprEnv env)
    {
        var names = new string[items.Count];
        var fns = new Eval[items.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int unnamed = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            string name = item.Alias ?? InferName(item.Expr) ?? "$" + (++unnamed).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!seen.Add(name))
            {
                throw new SqlQueryException($"The object property name '{name}' is specified more than once.");
            }

            names[i] = name;
            fns[i] = Compile(item.Expr, env).Fn;
        }

        return BuildObject(names, fns);
    }

    /// <summary>Projection name of a select item: last property of a property path, or a bare identifier.</summary>
    private static string? InferName(SqlExpr expr) => expr switch
    {
        MemberExpr member => member.Name,
        IndexExpr { Index: LiteralExpr { Value.Kind: QKind.String } literal } => literal.Value.AsString,
        IdentifierExpr identifier => identifier.Name,
        _ => null,
    };

    private static string? LeadingIdentifier(SqlExpr expr) => expr switch
    {
        IdentifierExpr identifier => identifier.Name,
        MemberExpr member => LeadingIdentifier(member.Target),
        IndexExpr index => LeadingIdentifier(index.Target),
        _ => null,
    };

    private static string? ImplicitAlias(SqlExpr expr) => expr switch
    {
        IdentifierExpr identifier => identifier.Name,
        MemberExpr member => member.Name,
        IndexExpr { Index: LiteralExpr { Value.Kind: QKind.String } literal } => literal.Value.AsString,
        IndexExpr index => ImplicitAlias(index.Target),
        _ => null,
    };

    // ------------------------------------------------------------------ expressions

    private CExpr Compile(SqlExpr expr, ExprEnv env)
    {
        switch (expr)
        {
            case LiteralExpr literal:
                return CExpr.Const(literal.Value);
            case ParameterExpr parameter:
            {
                int index = ParameterIndex(parameter.Name);
                return CExpr.Dynamic(ctx => ctx.Parameters[index]);
            }

            case IdentifierExpr identifier:
                if (!env.Scope.TryResolve(identifier.Name, out int slot))
                {
                    throw new SqlQueryException($"Identifier '{identifier.Name}' could not be resolved.");
                }

                return CExpr.SlotRead(slot);
            case MemberExpr member:
                return CompileMember(Compile(member.Target, env), member.Name);
            case IndexExpr index:
                return CompileIndex(Compile(index.Target, env), Compile(index.Index, env));
            case ArrayLiteralExpr array:
                return CompileArray(array.Items.Select(item => Compile(item, env)).ToArray());
            case ObjectLiteralExpr obj:
                return CompileObject(
                    obj.Properties.Select(p => p.Key).ToArray(),
                    obj.Properties.Select(p => Compile(p.Value, env)).ToArray());
            case UnaryExpr unary:
                return CompileUnary(unary.Op, Compile(unary.Operand, env));
            case BinaryExpr binary:
                return CompileBinary(binary.Op, Compile(binary.Left, env), Compile(binary.Right, env));
            case TernaryExpr ternary:
                return CompileTernary(Compile(ternary.Condition, env), Compile(ternary.IfTrue, env), Compile(ternary.IfFalse, env));
            case InExpr inExpr:
                return CompileIn(Compile(inExpr.Target, env), inExpr.Items.Select(item => Compile(item, env)).ToArray(), inExpr.Negated);
            case BetweenExpr between:
                return CompileBetween(Compile(between.Target, env), Compile(between.Low, env), Compile(between.High, env), between.Negated);
            case LikeExpr like:
                return CompileLike(
                    Compile(like.Target, env),
                    Compile(like.Pattern, env),
                    like.Escape is null ? null : Compile(like.Escape, env),
                    like.Negated);
            case CallExpr call:
                return CompileCall(call, env);
            case SubqueryExpr subquery:
                return CompileSubquery(subquery, env);
            default:
                throw new SqlQueryException($"Unsupported expression at position {expr.Position}.");
        }
    }

    private static CExpr CompileMember(CExpr target, string name)
    {
        if (target.IsConst)
        {
            return CExpr.Const(target.Value.GetProperty(name));
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        if (target.Slot >= 0)
        {
            int slot = target.Slot;
            return CExpr.Dynamic(ctx => ctx.Slots[slot].GetProperty(name, utf8));
        }

        var fn = target.Fn;
        return CExpr.Dynamic(ctx => fn(ctx).GetProperty(name, utf8));
    }

    private static CExpr CompileIndex(CExpr target, CExpr index)
    {
        if (index.IsConst)
        {
            var value = index.Value;
            if (value.Kind == QKind.String)
            {
                return CompileMember(target, value.AsString);
            }

            if (value.Kind == QKind.Number && QOps.TryGetIndex(value.AsNumber, out int i))
            {
                if (target.IsConst)
                {
                    return CExpr.Const(target.Value.GetIndex(i));
                }

                var fn = target.Fn;
                return CExpr.Dynamic(ctx => fn(ctx).GetIndex(i));
            }

            return CExpr.Const(default);
        }

        var targetFn = target.Fn;
        var indexFn = index.Fn;
        return CExpr.Dynamic(ctx => QOps.IndexAccess(targetFn(ctx), indexFn(ctx)));
    }

    private static CExpr CompileArray(CExpr[] items)
    {
        if (items.All(i => i.IsConst))
        {
            return CExpr.Const(QValue.Arr(items.Select(i => i.Value).Where(v => !v.IsUndefined).ToArray()));
        }

        var fns = items.Select(i => i.Fn).ToArray();
        return CExpr.Dynamic(ctx =>
        {
            var buffer = new QValue[fns.Length];
            int n = 0;
            foreach (var fn in fns)
            {
                var v = fn(ctx);
                if (!v.IsUndefined)
                {
                    buffer[n++] = v;
                }
            }

            if (n != buffer.Length)
            {
                Array.Resize(ref buffer, n);
            }

            return QValue.Arr(buffer);
        });
    }

    private static CExpr CompileObject(string[] names, CExpr[] values)
    {
        var build = BuildObject(names, values.Select(v => v.Fn).ToArray());
        return values.All(v => v.IsConst) ? CExpr.Const(build(ExecContext.Empty)) : CExpr.Dynamic(build);
    }

    /// <summary>Builds an object from named values, omitting undefined ones (the names array is shared when possible).</summary>
    private static Eval BuildObject(string[] names, Eval[] fns)
    {
        int count = fns.Length;
        return ctx =>
        {
            var values = new QValue[count];
            int defined = 0;
            for (int i = 0; i < count; i++)
            {
                var v = fns[i](ctx);
                values[i] = v;
                if (!v.IsUndefined)
                {
                    defined++;
                }
            }

            if (defined == count)
            {
                return QValue.Obj(new QObject(names, values));
            }

            var definedNames = new string[defined];
            var definedValues = new QValue[defined];
            int k = 0;
            for (int i = 0; i < count; i++)
            {
                if (!values[i].IsUndefined)
                {
                    definedNames[k] = names[i];
                    definedValues[k++] = values[i];
                }
            }

            return QValue.Obj(new QObject(definedNames, definedValues));
        };
    }

    private static CExpr CompileUnary(UnaryOp op, CExpr operand)
    {
        UnaryOperator f = op switch
        {
            UnaryOp.Negate => QOps.Negate,
            UnaryOp.Plus => QOps.UnaryPlus,
            UnaryOp.BitNot => QOps.BitNot,
            _ => QOps.Not,
        };
        if (operand.IsConst)
        {
            return CExpr.Const(f(operand.Value));
        }

        var fn = operand.Fn;
        return CExpr.Dynamic(ctx => f(fn(ctx)));
    }

    private static CExpr CompileBinary(BinaryOp op, CExpr left, CExpr right)
    {
        switch (op)
        {
            case BinaryOp.And:
                return CompileAnd(left, right);
            case BinaryOp.Or:
                return CompileOr(left, right);
            case BinaryOp.Coalesce:
                return CompileCoalesce(left, right);
            case BinaryOp.Equal:
                return CompileEquality(left, right, negate: false);
            case BinaryOp.NotEqual:
                return CompileEquality(left, right, negate: true);
        }

        BinaryOperator f = op switch
        {
            BinaryOp.Add => QOps.Add,
            BinaryOp.Subtract => QOps.Subtract,
            BinaryOp.Multiply => QOps.Multiply,
            BinaryOp.Divide => QOps.Divide,
            BinaryOp.Modulo => QOps.Modulo,
            BinaryOp.Concat => QOps.Concat,
            BinaryOp.BitAnd => QOps.BitAnd,
            BinaryOp.BitOr => QOps.BitOr,
            BinaryOp.BitXor => QOps.BitXor,
            BinaryOp.ShiftLeft => QOps.ShiftLeft,
            BinaryOp.ShiftRight => QOps.ShiftRight,
            BinaryOp.ShiftRightZeroFill => QOps.ShiftRightZeroFill,
            BinaryOp.Less => QOps.Less,
            BinaryOp.LessOrEqual => QOps.LessOrEqual,
            BinaryOp.Greater => QOps.Greater,
            _ => QOps.GreaterOrEqual,
        };

        if (left.IsConst && right.IsConst)
        {
            return CExpr.Const(f(left.Value, right.Value));
        }

        var lf = left.Fn;
        if (right.IsConst)
        {
            var constant = right.Value;
            return CExpr.Dynamic(ctx => f(lf(ctx), constant));
        }

        var rf = right.Fn;
        return CExpr.Dynamic(ctx => f(lf(ctx), rf(ctx)));
    }

    private static CExpr CompileAnd(CExpr left, CExpr right)
    {
        if (left.IsConst && right.IsConst)
        {
            return CExpr.Const(QOps.And(left.Value, right.Value));
        }

        var lf = left.Fn;
        var rf = right.Fn;
        return CExpr.Dynamic(ctx =>
        {
            var a = lf(ctx);
            if (a.IsFalse)
            {
                return QValue.False;
            }

            var b = rf(ctx);
            if (b.IsFalse)
            {
                return QValue.False;
            }

            return a.IsTrue && b.IsTrue ? QValue.True : default;
        });
    }

    private static CExpr CompileOr(CExpr left, CExpr right)
    {
        if (left.IsConst && right.IsConst)
        {
            return CExpr.Const(QOps.Or(left.Value, right.Value));
        }

        var lf = left.Fn;
        var rf = right.Fn;
        return CExpr.Dynamic(ctx =>
        {
            var a = lf(ctx);
            if (a.IsTrue)
            {
                return QValue.True;
            }

            var b = rf(ctx);
            if (b.IsTrue)
            {
                return QValue.True;
            }

            return a.IsFalse && b.IsFalse ? QValue.False : default;
        });
    }

    private static CExpr CompileCoalesce(CExpr left, CExpr right)
    {
        if (left.IsConst)
        {
            return left.Value.IsUndefined ? right : left;
        }

        var lf = left.Fn;
        var rf = right.Fn;
        return CExpr.Dynamic(ctx =>
        {
            var a = lf(ctx);
            return a.IsUndefined ? rf(ctx) : a;
        });
    }

    private static CExpr CompileTernary(CExpr condition, CExpr ifTrue, CExpr ifFalse)
    {
        if (condition.IsConst)
        {
            return condition.Value.IsTrue ? ifTrue : ifFalse;
        }

        var cf = condition.Fn;
        var tf = ifTrue.Fn;
        var ff = ifFalse.Fn;
        return CExpr.Dynamic(ctx => cf(ctx).IsTrue ? tf(ctx) : ff(ctx));
    }

    private static CExpr CompileEquality(CExpr left, CExpr right, bool negate)
    {
        if (left.IsConst && right.IsConst)
        {
            return CExpr.Const(negate ? QOps.NotEqual(left.Value, right.Value) : QOps.Equal(left.Value, right.Value));
        }

        // Fast path: comparison with a string constant compares UTF-8 bytes without materializing document strings.
        if (right.IsConst && right.Value.Kind == QKind.String)
        {
            return CompileStringEquality(left, right.Value.AsString, negate);
        }

        if (left.IsConst && left.Value.Kind == QKind.String)
        {
            return CompileStringEquality(right, left.Value.AsString, negate);
        }

        var lf = left.Fn;
        var rf = right.Fn;
        return CExpr.Dynamic(ctx =>
        {
            var a = lf(ctx);
            var b = rf(ctx);
            if (a.IsUndefined || b.IsUndefined)
            {
                return default;
            }

            return QValue.Bool(QOps.DeepEquals(a, b) != negate);
        });
    }

    private static CExpr CompileStringEquality(CExpr operand, string constant, bool negate)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(constant);
        var fn = operand.Fn;
        return CExpr.Dynamic(ctx =>
        {
            var v = fn(ctx);
            if (v.Kind != QKind.String)
            {
                return v.IsUndefined ? default : QValue.Bool(negate);
            }

            bool equal = v.IsElement
                ? v.Element.ValueEquals(utf8)
                : string.Equals(v.AsString, constant, StringComparison.Ordinal);
            return QValue.Bool(equal != negate);
        });
    }

    private static CExpr CompileIn(CExpr target, CExpr[] items, bool negated)
    {
        var tf = target.Fn;
        if (items.All(i => i.IsConst))
        {
            var values = items.Select(i => i.Value).Where(v => !v.IsUndefined).ToArray();
            if (target.IsConst)
            {
                if (target.Value.IsUndefined)
                {
                    return CExpr.Const(default);
                }

                bool found = values.Any(v => QOps.DeepEquals(target.Value, v));
                return CExpr.Const(QValue.Bool(found != negated));
            }

            if (values.Length > 4)
            {
                var set = new HashSet<QValue>(values, QValueEqualityComparer.Instance);
                return CExpr.Dynamic(ctx =>
                {
                    var v = tf(ctx);
                    return v.IsUndefined ? default : QValue.Bool(set.Contains(v) != negated);
                });
            }

            return CExpr.Dynamic(ctx =>
            {
                var v = tf(ctx);
                if (v.IsUndefined)
                {
                    return default;
                }

                foreach (var candidate in values)
                {
                    if (QOps.DeepEquals(v, candidate))
                    {
                        return QValue.Bool(!negated);
                    }
                }

                return QValue.Bool(negated);
            });
        }

        var fns = items.Select(i => i.Fn).ToArray();
        return CExpr.Dynamic(ctx =>
        {
            var v = tf(ctx);
            if (v.IsUndefined)
            {
                return default;
            }

            foreach (var fn in fns)
            {
                var candidate = fn(ctx);
                if (!candidate.IsUndefined && QOps.DeepEquals(v, candidate))
                {
                    return QValue.Bool(!negated);
                }
            }

            return QValue.Bool(negated);
        });
    }

    private static CExpr CompileBetween(CExpr target, CExpr low, CExpr high, bool negated)
    {
        static QValue Between(in QValue v, in QValue lo, in QValue hi, bool negated)
        {
            var result = QOps.And(QOps.GreaterOrEqual(v, lo), QOps.LessOrEqual(v, hi));
            return negated ? QOps.Not(result) : result;
        }

        if (target.IsConst && low.IsConst && high.IsConst)
        {
            return CExpr.Const(Between(target.Value, low.Value, high.Value, negated));
        }

        var tf = target.Fn;
        var lf = low.Fn;
        var hf = high.Fn;
        return CExpr.Dynamic(ctx => Between(tf(ctx), lf(ctx), hf(ctx), negated));
    }

    private static CExpr CompileLike(CExpr target, CExpr pattern, CExpr? escape, bool negated)
    {
        var tf = target.Fn;
        if (pattern.IsConst && (escape is null || escape.IsConst))
        {
            if (pattern.Value.Kind != QKind.String)
            {
                return CExpr.Const(default);
            }

            char? escapeChar = null;
            if (escape is not null)
            {
                if (escape.Value.Kind != QKind.String || escape.Value.AsString.Length != 1)
                {
                    throw new SqlQueryException("The ESCAPE argument of LIKE must be a single-character string.");
                }

                escapeChar = escape.Value.AsString[0];
            }

            var compiled = LikePattern.Compile(pattern.Value.AsString, escapeChar);
            if (target.IsConst)
            {
                return CExpr.Const(target.Value.Kind == QKind.String
                    ? QValue.Bool(compiled.IsMatch(target.Value.AsString) != negated)
                    : default);
            }

            return CExpr.Dynamic(ctx =>
            {
                var v = tf(ctx);
                return v.Kind == QKind.String ? QValue.Bool(compiled.IsMatch(v.AsString) != negated) : default;
            });
        }

        var pf = pattern.Fn;
        var ef = escape?.Fn;
        var cache = new LikePatternCache();
        return CExpr.Dynamic(ctx =>
        {
            var v = tf(ctx);
            var p = pf(ctx);
            if (v.Kind != QKind.String || p.Kind != QKind.String)
            {
                return default;
            }

            char? escapeChar = null;
            if (ef is not null)
            {
                var e = ef(ctx);
                if (e.Kind != QKind.String || e.AsString.Length != 1)
                {
                    return default;
                }

                escapeChar = e.AsString[0];
            }

            return QValue.Bool(cache.Get(p.AsString, escapeChar).IsMatch(v.AsString) != negated);
        });
    }

    /// <summary>Remembers the last compiled pattern (patterns from parameters repeat on every row). Thread-safe.</summary>
    private sealed class LikePatternCache
    {
        private Entry? _last;

        public LikePattern Get(string pattern, char? escape)
        {
            var last = Volatile.Read(ref _last);
            if (last is not null && last.Escape == escape && string.Equals(last.Pattern, pattern, StringComparison.Ordinal))
            {
                return last.Compiled;
            }

            var compiled = LikePattern.Compile(pattern, escape);
            Volatile.Write(ref _last, new Entry(pattern, escape, compiled));
            return compiled;
        }

        private sealed record Entry(string Pattern, char? Escape, LikePattern Compiled);
    }

    // ------------------------------------------------------------------ calls

    private CExpr CompileCall(CallExpr call, ExprEnv env)
    {
        if (call.IsUdf)
        {
            foreach (var arg in call.Args)
            {
                Compile(arg, env);
            }

            return CExpr.Dynamic(static _ => throw new SqlQueryException("User-defined functions are not supported by DbEmulator"));
        }

        if (AggregateSpec.TryGetKind(call.Name, out var kind))
        {
            return CompileAggregate(call, kind, env);
        }

        if (Builtins.GetUnsupportedMessage(call.Name) is { } unsupported)
        {
            throw new SqlQueryException(unsupported);
        }

        if (!Builtins.TryGet(call.Name, out var info))
        {
            throw new SqlQueryException($"'{call.Name}' is not a recognized built-in function name.");
        }

        int argc = call.Args.Count;
        if (argc < info.MinArgs || argc > info.MaxArgs)
        {
            throw new SqlQueryException(ArityMessage(info));
        }

        var args = call.Args.Select(a => Compile(a, env)).ToArray();
        var impl = info.Implementation;
        if (info.Deterministic && args.All(a => a.IsConst))
        {
            return CExpr.Const(impl(args.Select(a => a.Value).ToArray(), ExecContext.Empty));
        }

        var fns = args.Select(a => a.Fn).ToArray();
        switch (argc)
        {
            case 0:
                return CExpr.Dynamic(ctx => impl(ReadOnlySpan<QValue>.Empty, ctx));
            case 1:
            {
                var f0 = fns[0];
                return CExpr.Dynamic(ctx =>
                {
                    var a0 = f0(ctx);
                    return impl(new ReadOnlySpan<QValue>(in a0), ctx);
                });
            }

            case 2:
            case 3:
            case 4:
                return CExpr.Dynamic(ctx =>
                {
                    var buffer = default(ArgBuffer4);
                    Span<QValue> span = buffer;
                    for (int i = 0; i < fns.Length; i++)
                    {
                        span[i] = fns[i](ctx);
                    }

                    return impl(span[..fns.Length], ctx);
                });
            default:
                return CExpr.Dynamic(ctx =>
                {
                    var values = new QValue[fns.Length];
                    for (int i = 0; i < fns.Length; i++)
                    {
                        values[i] = fns[i](ctx);
                    }

                    return impl(values, ctx);
                });
        }
    }

    private static string ArityMessage(FunctionInfo info)
    {
        string name = info.Name.ToUpperInvariant();
        if (info.MinArgs == info.MaxArgs)
        {
            return $"The {name} function requires {info.MinArgs} argument(s).";
        }

        return info.MaxArgs == int.MaxValue
            ? $"The {name} function requires at least {info.MinArgs} argument(s)."
            : $"The {name} function requires between {info.MinArgs} and {info.MaxArgs} arguments.";
    }

    private CExpr CompileAggregate(CallExpr call, AggregateKind kind, ExprEnv env)
    {
        string name = call.Name.ToUpperInvariant();
        if (env.Aggregates is null)
        {
            throw new SqlQueryException($"Aggregate function '{name}' is not allowed in this clause; aggregates may only appear in the SELECT clause.");
        }

        if (env.InAggregate)
        {
            throw new SqlQueryException("Cannot perform an aggregate function on an expression containing an aggregate.");
        }

        if (call.Args.Count != 1)
        {
            throw new SqlQueryException($"The {name} function requires 1 argument(s).");
        }

        var argument = Compile(call.Args[0], env with { InAggregate = true });
        int slot = NewSlot();
        env.Aggregates.Items.Add(new AggregateSpec(kind, argument.Fn, slot));
        return CExpr.SlotRead(slot);
    }

    private CExpr CompileSubquery(SubqueryExpr subquery, ExprEnv env)
    {
        var plan = CompileQuery(subquery.Query, env.Scope, topLevel: false);
        switch (subquery.Kind)
        {
            case SubqueryKind.Exists:
                return CExpr.Dynamic(ctx =>
                {
                    using var e = plan.Run(ctx, null).GetEnumerator();
                    return QValue.Bool(e.MoveNext());
                });
            case SubqueryKind.Array:
                return CExpr.Dynamic(ctx => QValue.Arr(plan.Run(ctx, null).ToArray()));
            default:
                return CExpr.Dynamic(ctx =>
                {
                    foreach (var value in plan.Run(ctx, null))
                    {
                        return value;
                    }

                    return default;
                });
        }
    }
}
