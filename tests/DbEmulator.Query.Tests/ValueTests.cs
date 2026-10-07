using System.Buffers;
using System.Text;
using System.Text.Json;

namespace DbEmulator.Query.Tests;

public class ValueTests
{
    [Theory]
    [InlineData(1.0, "1")]
    [InlineData(-0.0, "0")]
    [InlineData(0.1, "0.1")]
    [InlineData(2.5, "2.5")]
    [InlineData(-3.0, "-3")]
    [InlineData(123456789012.0, "123456789012")]
    [InlineData(1e15, "1000000000000000")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    public void ComputedNumbersUseShortestRoundTripForm(double value, string expected) =>
        Assert.Equal(expected, QValue.FromNumber(value).ToJsonString());

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteNumbersAreUndefined(double value) => Assert.True(QValue.FromNumber(value).IsUndefined);

    [Theory]
    [InlineData("{\"a\":1}", QKind.Object)]
    [InlineData("[1]", QKind.Array)]
    [InlineData("\"s\"", QKind.String)]
    [InlineData("1.5", QKind.Number)]
    [InlineData("true", QKind.Boolean)]
    [InlineData("false", QKind.Boolean)]
    [InlineData("null", QKind.Null)]
    public void FromElementKinds(string json, QKind kind)
    {
        var value = QValue.FromElement(T.ParseJson(json));
        Assert.Equal(kind, value.Kind);
        Assert.Equal(json, value.ToJsonString());
    }

    [Fact]
    public void DefaultIsUndefined()
    {
        Assert.True(default(QValue).IsUndefined);
        Assert.Equal(QKind.Undefined, QValue.Undefined.Kind);
        Assert.True(QValue.FromElement(default).IsUndefined);
        Assert.Equal("undefined", default(QValue).ToJsonString());
    }

    [Fact]
    public void ElementNumbersAreWrittenVerbatim()
    {
        var value = QValue.FromElement(T.ParseJson("[1.10, 12345678901234567890, 1e2, -0.0]"));
        Assert.Equal("[1.10,12345678901234567890,1e2,-0.0]", value.ToJsonString());
    }

    [Fact]
    public void QueryNumbersFromDocumentsAreVerbatimButComputedNumbersAreNormalized()
    {
        Assert.Equal("[1.10]", T.Run("SELECT VALUE c.price FROM c WHERE c.id = '1'"));
        Assert.Equal("[12345678901234567890]", T.Run("SELECT VALUE c.big FROM c WHERE c.id = '1'"));
        Assert.Equal("[1.1]", T.Run("SELECT VALUE c.price * 1 FROM c WHERE c.id = '1'"));
        Assert.Equal("[30.5]", T.Run("SELECT VALUE c.age + 0.5 FROM c WHERE c.id = '1'"));
        Assert.Equal("[6]", T.Run("SELECT VALUE 2.0 * 3"));
        Assert.Equal("""[{"p":1.10,"q":[1.10]}]""", T.Run("SELECT c.price AS p, [c.price] AS q FROM c WHERE c.id = '1'"));
    }

    [Fact]
    public void FactoryMethods()
    {
        Assert.Equal("true", QValue.FromBoolean(true).ToJsonString());
        Assert.Equal("null", QValue.Null.ToJsonString());
        Assert.Equal("\"a\\\"b\"", QValue.FromString("a\"b").ToJsonString());
        Assert.Equal("[1,\"x\"]", QValue.FromArray([QValue.FromNumber(1), QValue.Undefined, QValue.FromString("x")]).ToJsonString());
        var obj = QValue.FromObject(
        [
            new KeyValuePair<string, QValue>("b", QValue.FromNumber(1)),
            new KeyValuePair<string, QValue>("skip", QValue.Undefined),
            new KeyValuePair<string, QValue>("a", QValue.FromString("x")),
            new KeyValuePair<string, QValue>("b", QValue.FromNumber(2)),
        ]);
        Assert.Equal("{\"b\":2,\"a\":\"x\"}", obj.ToJsonString());
    }

    [Fact]
    public void TryGetPropertyOnElementAndConstructedObjects()
    {
        var element = QValue.FromElement(T.Fixture[0]);
        Assert.True(element.TryGetProperty("_rid", out var rid));
        Assert.True(rid.TryGetString(out var ridText));
        Assert.Equal("r1", ridText);
        Assert.False(element.TryGetProperty("missing", out var missing));
        Assert.True(missing.IsUndefined);

        var row = SqlQuery.Parse("""SELECT c._rid, [{"item": c.name}] AS orderByItems, c AS payload FROM c WHERE c.id = '2'""").Execute(T.Fixture).Single();
        Assert.True(row.TryGetProperty("_rid", out var rowRid));
        Assert.True(rowRid.TryGetString(out var rowRidText));
        Assert.Equal("r2", rowRidText);
        Assert.True(row.TryGetProperty("payload", out var payload));
        Assert.True(payload.TryGetElement(out var payloadElement));
        Assert.Equal(T.FixtureJson[1], payloadElement.GetRawText());

        Assert.False(QValue.FromNumber(1).TryGetProperty("x", out _));
        Assert.False(QValue.FromNumber(1).TryGetString(out _));
        Assert.True(QValue.FromString("s").TryGetString(out var s));
        Assert.Equal("s", s);
    }

    [Fact]
    public void TryGetElementOnlyForElementBackedValues()
    {
        Assert.True(QValue.FromElement(T.Fixture[0]).TryGetElement(out _));
        Assert.False(QValue.FromString("x").TryGetElement(out _));
        Assert.False(QValue.FromNumber(1).TryGetElement(out _));
    }

    [Fact]
    public void WriteToUndefinedThrows()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        Assert.Throws<InvalidOperationException>(() => QValue.Undefined.WriteTo(writer));
    }

    [Fact]
    public void WriteToComposesWithCallerWriter()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Documents");
            writer.WriteStartArray();
            foreach (var value in SqlQuery.Parse("SELECT c.id, c.price FROM c WHERE c.id IN ('1', '2')").Execute(T.Fixture))
            {
                value.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Assert.Equal("""{"Documents":[{"id":"1","price":1.10},{"id":"2","price":2.5}]}""", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void StringsAreEscapedByTheWriter()
    {
        var value = QValue.FromString("line\nquote\"tab\t");
        Assert.Equal("\"line\\nquote\\\"tab\\t\"", value.ToJsonString());
    }
}
