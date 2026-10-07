using System.Text.Json;

namespace DbEmulator.Query;

/// <summary>
/// A parsed Azure Cosmos DB for NoSQL query (the "Cosmos SQL" dialect).
/// Immutable after <see cref="Parse"/>; <see cref="Execute"/> may be called concurrently.
/// </summary>
public sealed class SqlQuery
{
    private readonly CompiledQuery _plan;
    private readonly int _slotCount;
    private readonly string[] _parameterNames;

    private SqlQuery(string text)
    {
        Text = text;
        _plan = new CompiledQuery();
        _parameterNames = [];
    }

    private SqlQuery(string text, CompiledQuery plan, int slotCount, string[] parameterNames)
    {
        Text = text;
        _plan = plan;
        _slotCount = slotCount;
        _parameterNames = parameterNames;
    }

    /// <summary>The original query text.</summary>
    public string Text { get; }

    /// <summary>
    /// Parses a query. Throws <see cref="SqlQueryException"/> for syntax errors or unsupported constructs.
    /// </summary>
    public static SqlQuery Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ast = Parser.Parse(text);
        var compiler = new QueryCompiler();
        var plan = compiler.CompileQuery(ast, parent: null, topLevel: true);
        return new SqlQuery(text, plan, compiler.SlotCount, compiler.ParameterNames);
    }

    /// <summary>
    /// Evaluates the query over <paramref name="documents"/> (the documents of one container, already
    /// scoped to a partition key by the caller when applicable). Results are produced lazily where the
    /// query shape allows it (no ORDER BY / GROUP BY / aggregates), so TOP and paging can stop early.
    /// Undefined results are never yielded. Parameter names include the leading '@'.
    /// Throws <see cref="SqlQueryException"/> for runtime errors (e.g. missing parameter, unknown function).
    /// </summary>
    public IEnumerable<QValue> Execute(
        IEnumerable<JsonElement> documents,
        IReadOnlyDictionary<string, JsonElement>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(documents);

        // Missing parameters are reported eagerly, before enumeration starts.
        var values = new QValue[_parameterNames.Length];
        for (int i = 0; i < values.Length; i++)
        {
            string name = _parameterNames[i];
            if (parameters is null || !parameters.TryGetValue(name, out var element))
            {
                throw new SqlQueryException($"The query parameter '{name}' is referenced in the query but no value was supplied.");
            }

            values[i] = QValue.FromElement(element);
        }

        return ExecuteCore(documents, values);
    }

    private IEnumerable<QValue> ExecuteCore(IEnumerable<JsonElement> documents, QValue[] parameters)
    {
        // One context per enumeration: concurrent or repeated enumerations never share state.
        var ctx = new ExecContext(_slotCount, parameters, DateTime.UtcNow);
        foreach (var value in _plan.Run(ctx, documents))
        {
            yield return value;
        }
    }
}

/// <summary>Raised for query syntax errors, unsupported features and query runtime errors (maps to HTTP 400).</summary>
public sealed class SqlQueryException : Exception
{
    public SqlQueryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The type of a query value. Undefined is a first-class value in Cosmos SQL.</summary>
public enum QKind
{
    Undefined,
    Null,
    Boolean,
    Number,
    String,
    Array,
    Object,
}

/// <summary>A query value: either backed by a source <see cref="JsonElement"/> or constructed during evaluation.</summary>
public readonly partial struct QValue
{
    public QKind Kind => _kind;

    public bool IsUndefined => Kind == QKind.Undefined;

    /// <summary>Wraps a JSON element without copying it.</summary>
    public static QValue FromElement(JsonElement element) => FromElementCore(element);

    /// <summary>
    /// Writes the value as JSON. Values taken unchanged from a source document are written verbatim
    /// (numbers keep their original text). Throws <see cref="InvalidOperationException"/> for Undefined.
    /// </summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (IsUndefined)
        {
            throw new InvalidOperationException("An undefined value cannot be written as JSON.");
        }

        WriteCore(writer);
    }

    /// <summary>Compact JSON text of the value (for diagnostics and tests). Undefined yields "undefined".</summary>
    public string ToJsonString() => IsUndefined ? "undefined" : ToJsonCore();
}
