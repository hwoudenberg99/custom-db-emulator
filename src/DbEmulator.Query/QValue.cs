using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DbEmulator.Query;

// Internal representation of QValue. A value is either:
//  - backed by a source JsonElement (strings, numbers, arrays, objects taken unchanged from a document or parameter), or
//  - constructed: bool/number in _num, string in _ref, array as QValue[] in _ref, object as QObject in _ref.
// default(QValue) is Undefined. Constructed arrays/objects never contain Undefined members.
public readonly partial struct QValue
{
    private static readonly QValue s_true = new(QKind.Boolean, 1d);
    private static readonly QValue s_false = new(QKind.Boolean, 0d);
    private static readonly QValue s_null = new(QKind.Null, 0d);

    internal static readonly JsonWriterOptions CompactWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private readonly JsonElement _element;
    private readonly object? _ref;
    private readonly double _num;
    private readonly QKind _kind;
    private readonly bool _isElement;

    private QValue(QKind kind, double num)
    {
        _element = default;
        _ref = null;
        _num = num;
        _kind = kind;
        _isElement = false;
    }

    private QValue(QKind kind, object reference)
    {
        _element = default;
        _ref = reference;
        _num = 0;
        _kind = kind;
        _isElement = false;
    }

    private QValue(JsonElement element, QKind kind)
    {
        _element = element;
        _ref = null;
        _num = 0;
        _kind = kind;
        _isElement = true;
    }

    // ------------------------------------------------------------------ public factories

    /// <summary>The Undefined value (same as <c>default(QValue)</c>).</summary>
    public static QValue Undefined => default;

    /// <summary>The JSON <c>null</c> value.</summary>
    public static QValue Null => s_null;

    /// <summary>A boolean value.</summary>
    public static QValue FromBoolean(bool value) => value ? s_true : s_false;

    /// <summary>A computed number. NaN and infinities yield Undefined.</summary>
    public static QValue FromNumber(double value) => Num(value);

    /// <summary>A string value.</summary>
    public static QValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new QValue(QKind.String, value);
    }

    /// <summary>A constructed array. Undefined items are omitted.</summary>
    public static QValue FromArray(IEnumerable<QValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = new List<QValue>();
        foreach (var item in items)
        {
            if (!item.IsUndefined)
            {
                list.Add(item);
            }
        }

        return Arr(list.ToArray());
    }

    /// <summary>A constructed object (property order preserved). Undefined values are omitted; a repeated name replaces the earlier value.</summary>
    public static QValue FromObject(IEnumerable<KeyValuePair<string, QValue>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var names = new List<string>();
        var values = new List<QValue>();
        foreach (var (name, value) in properties)
        {
            ArgumentNullException.ThrowIfNull(name, nameof(properties));
            if (value.IsUndefined)
            {
                continue;
            }

            int existing = names.IndexOf(name);
            if (existing >= 0)
            {
                values[existing] = value;
            }
            else
            {
                names.Add(name);
                values.Add(value);
            }
        }

        return Obj(new QObject(names.ToArray(), values.ToArray()));
    }

    /// <summary>
    /// Returns the source <see cref="JsonElement"/> when the value was taken unchanged from a document or parameter
    /// (lets callers write documents verbatim without re-serialization).
    /// </summary>
    public bool TryGetElement(out JsonElement element)
    {
        element = _element;
        return _isElement;
    }

    /// <summary>
    /// Looks up a property. Returns true only when <see cref="Kind"/> is Object and the property exists
    /// (works for element-backed and constructed objects).
    /// </summary>
    public bool TryGetProperty(string name, out QValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        value = GetProperty(name);
        return !value.IsUndefined;
    }

    /// <summary>Returns true (and the string) only when <see cref="Kind"/> is String.</summary>
    public bool TryGetString([MaybeNullWhen(false)] out string value)
    {
        if (_kind == QKind.String)
        {
            value = AsString;
            return true;
        }

        value = null!;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => ToJsonString();

    // ------------------------------------------------------------------ internal factories

    internal static QValue True => s_true;

    internal static QValue False => s_false;

    internal static QValue Bool(bool value) => value ? s_true : s_false;

    internal static QValue Num(double value) => double.IsFinite(value) ? new QValue(QKind.Number, value) : default;

    internal static QValue Str(string value) => new(QKind.String, value);

    /// <summary>Wraps an item array (must not contain Undefined; the array must not be mutated afterwards).</summary>
    internal static QValue Arr(QValue[] items) => new(QKind.Array, items);

    internal static QValue Obj(QObject obj) => new(QKind.Object, obj);

    private static QValue FromElementCore(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return new QValue(element, QKind.Object);
            case JsonValueKind.Array:
                return new QValue(element, QKind.Array);
            case JsonValueKind.String:
                return new QValue(element, QKind.String);
            case JsonValueKind.Number:
                return new QValue(element, QKind.Number);
            case JsonValueKind.True:
                return s_true;
            case JsonValueKind.False:
                return s_false;
            case JsonValueKind.Null:
                return s_null;
            default:
                return default;
        }
    }

    // ------------------------------------------------------------------ internal accessors

    internal bool IsElement => _isElement;

    internal JsonElement Element => _element;

    internal bool IsTrue => _kind == QKind.Boolean && _num != 0;

    internal bool IsFalse => _kind == QKind.Boolean && _num == 0;

    /// <summary>Boolean payload (only meaningful when Kind is Boolean).</summary>
    internal bool AsBool => _num != 0;

    /// <summary>Numeric payload (only meaningful when Kind is Number).</summary>
    internal double AsNumber => _isElement ? ReadDouble(_element) : _num;

    /// <summary>String payload (only meaningful when Kind is String). Allocates for element-backed strings.</summary>
    internal string AsString => _isElement ? _element.GetString()! : (string)_ref!;

    /// <summary>Array length (only meaningful when Kind is Array).</summary>
    internal int ArrayLength => _isElement ? _element.GetArrayLength() : ((QValue[])_ref!).Length;

    /// <summary>Property count (only meaningful when Kind is Object).</summary>
    internal int PropertyCount => _isElement ? _element.GetPropertyCount() : ((QObject)_ref!).Count;

    internal QValue GetIndex(int index)
    {
        if (_kind != QKind.Array || index < 0)
        {
            return default;
        }

        if (_isElement)
        {
            return index < _element.GetArrayLength() ? FromElementCore(_element[index]) : default;
        }

        var items = (QValue[])_ref!;
        return index < items.Length ? items[index] : default;
    }

    internal QValue GetProperty(string name)
    {
        if (_kind != QKind.Object)
        {
            return default;
        }

        if (_isElement)
        {
            return _element.TryGetProperty(name, out var p) ? FromElementCore(p) : default;
        }

        return ((QObject)_ref!).Get(name);
    }

    internal QValue GetProperty(string name, byte[] utf8Name)
    {
        if (_kind != QKind.Object)
        {
            return default;
        }

        if (_isElement)
        {
            return _element.TryGetProperty(utf8Name, out var p) ? FromElementCore(p) : default;
        }

        return ((QObject)_ref!).Get(name);
    }

    /// <summary>Enumerates array items (Kind must be Array).</summary>
    internal ArrayEnumerator EnumerateArray() => new(this);

    /// <summary>Enumerates object properties (Kind must be Object).</summary>
    internal ObjectEnumerator EnumerateObject() => new(this);

    /// <summary>
    /// Items of an array value. For constructed arrays this returns the backing array itself, which must not be mutated.
    /// </summary>
    internal QValue[] ToItemArray()
    {
        if (!_isElement)
        {
            return (QValue[])_ref!;
        }

        var result = new QValue[_element.GetArrayLength()];
        int i = 0;
        foreach (var item in _element.EnumerateArray())
        {
            result[i++] = FromElementCore(item);
        }

        return result;
    }

    // ------------------------------------------------------------------ JSON output

    internal void WriteCore(Utf8JsonWriter writer)
    {
        if (_isElement)
        {
            _element.WriteTo(writer);
            return;
        }

        switch (_kind)
        {
            case QKind.Null:
                writer.WriteNullValue();
                break;
            case QKind.Boolean:
                writer.WriteBooleanValue(_num != 0);
                break;
            case QKind.Number:
                JsonText.WriteNumber(writer, _num);
                break;
            case QKind.String:
                writer.WriteStringValue((string)_ref!);
                break;
            case QKind.Array:
                writer.WriteStartArray();
                foreach (var item in (QValue[])_ref!)
                {
                    if (!item.IsUndefined)
                    {
                        item.WriteCore(writer);
                    }
                }

                writer.WriteEndArray();
                break;
            case QKind.Object:
                var obj = (QObject)_ref!;
                writer.WriteStartObject();
                for (int i = 0; i < obj.Names.Length; i++)
                {
                    var v = obj.Values[i];
                    if (v.IsUndefined)
                    {
                        continue;
                    }

                    writer.WritePropertyName(obj.Names[i]);
                    v.WriteCore(writer);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException("An undefined value cannot be written as JSON.");
        }
    }

    internal string ToJsonCore()
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer, CompactWriterOptions))
        {
            WriteCore(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static double ReadDouble(JsonElement element)
    {
        if (element.TryGetDouble(out double d))
        {
            return d;
        }

        // Out-of-range literal such as 1e400: double.Parse yields +/-Infinity.
        return double.Parse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ enumerators

    internal struct ArrayEnumerator
    {
        private readonly QValue[]? _items;
        private JsonElement.ArrayEnumerator _inner;
        private int _index;
        private QValue _current;

        internal ArrayEnumerator(QValue array)
        {
            if (array._isElement)
            {
                _inner = array._element.EnumerateArray();
                _items = null;
            }
            else
            {
                _inner = default;
                _items = (QValue[])array._ref!;
            }

            _index = -1;
            _current = default;
        }

        public readonly ArrayEnumerator GetEnumerator() => this;

        public readonly QValue Current => _current;

        public bool MoveNext()
        {
            if (_items is null)
            {
                if (_inner.MoveNext())
                {
                    _current = FromElementCore(_inner.Current);
                    return true;
                }

                return false;
            }

            if (++_index < _items.Length)
            {
                _current = _items[_index];
                return true;
            }

            return false;
        }
    }

    internal struct ObjectEnumerator
    {
        private readonly QObject? _obj;
        private JsonElement.ObjectEnumerator _inner;
        private int _index;
        private QProperty _current;

        internal ObjectEnumerator(QValue value)
        {
            if (value._isElement)
            {
                _inner = value._element.EnumerateObject();
                _obj = null;
            }
            else
            {
                _inner = default;
                _obj = (QObject)value._ref!;
            }

            _index = -1;
            _current = default;
        }

        public readonly ObjectEnumerator GetEnumerator() => this;

        public readonly QProperty Current => _current;

        public bool MoveNext()
        {
            if (_obj is null)
            {
                if (_inner.MoveNext())
                {
                    var p = _inner.Current;
                    _current = new QProperty(p.Name, FromElementCore(p.Value));
                    return true;
                }

                return false;
            }

            if (++_index < _obj.Names.Length)
            {
                _current = new QProperty(_obj.Names[_index], _obj.Values[_index]);
                return true;
            }

            return false;
        }
    }
}

/// <summary>A name/value pair produced when enumerating an object value.</summary>
internal readonly struct QProperty
{
    public QProperty(string name, QValue value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public QValue Value { get; }
}

/// <summary>A constructed object: ordered names and values (no Undefined values, no duplicate names).</summary>
internal sealed class QObject
{
    public QObject(string[] names, QValue[] values)
    {
        Names = names;
        Values = values;
    }

    public string[] Names { get; }

    public QValue[] Values { get; }

    public int Count => Names.Length;

    public QValue Get(string name)
    {
        var names = Names;
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return Values[i];
            }
        }

        return default;
    }
}
