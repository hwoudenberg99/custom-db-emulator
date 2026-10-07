using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace DbEmulator.Query.Tests;

public sealed class LargeCollectionFixture : IDisposable
{
    public const int Count = 100_000;

    private readonly JsonDocument _document;

    public LargeCollectionFixture()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(CultureInfo.InvariantCulture,
                $$"""{"id":"{{i}}","n":{{i}},"status":"{{(i % 10 == 0 ? "x" : "y")}}","name":"user{{i}}","tags":["a","b"],"nested":{"v":{{i % 100}}},"_rid":"rid{{i:D6}}","_ts":{{1000 + i}}}""");
        }

        sb.Append(']');
        _document = JsonDocument.Parse(sb.ToString());
        Documents = _document.RootElement.EnumerateArray().ToArray();
    }

    public JsonElement[] Documents { get; }

    public void Dispose() => _document.Dispose();
}

public class ExecutionTests(LargeCollectionFixture large, ITestOutputHelper output) : IClassFixture<LargeCollectionFixture>
{
    private sealed class CountingEnumerable(IEnumerable<JsonElement> inner) : IEnumerable<JsonElement>
    {
        public int Pulled { get; private set; }

        public IEnumerator<JsonElement> GetEnumerator()
        {
            foreach (var item in inner)
            {
                Pulled++;
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void TopStopsScanningAtFirstMatch()
    {
        var source = new CountingEnumerable(large.Documents);
        var results = SqlQuery.Parse("SELECT TOP 1 * FROM c WHERE c.n = 5").Execute(source).ToList();
        Assert.Single(results);
        Assert.Equal(6, source.Pulled);
    }

    [Fact]
    public void ExecuteIsLazyForPaging()
    {
        var source = new CountingEnumerable(large.Documents);
        var page = SqlQuery.Parse("SELECT * FROM c").Execute(source).Take(3).ToList();
        Assert.Equal(3, page.Count);
        Assert.Equal(3, source.Pulled);
    }

    [Fact]
    public void ExecuteDoesNotScanBeforeEnumeration()
    {
        var source = new CountingEnumerable(large.Documents);
        var results = SqlQuery.Parse("SELECT * FROM c ORDER BY c.n").Execute(source);
        Assert.Equal(0, source.Pulled);
        Assert.Equal("""{"id":"0","n":0,"status":"x","name":"user0","tags":["a","b"],"nested":{"v":0},"_rid":"rid000000","_ts":1000}""", results.First().ToJsonString());
        Assert.Equal(LargeCollectionFixture.Count, source.Pulled);
    }

    [Fact]
    public void DistinctAndJoinStreamLazily()
    {
        var source = new CountingEnumerable(large.Documents);
        var results = SqlQuery.Parse("SELECT DISTINCT VALUE t FROM c JOIN t IN c.tags").Execute(source).Take(2).ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(1, source.Pulled);
    }

    [Fact]
    public void FilterOverHundredThousandDocumentsIsFast()
    {
        var query = SqlQuery.Parse("SELECT * FROM c WHERE c.status = \"x\"");
        Assert.Equal(LargeCollectionFixture.Count / 10, query.Execute(large.Documents).Count()); // warm-up
        var sw = Stopwatch.StartNew();
        int count = query.Execute(large.Documents).Count();
        sw.Stop();
        output.WriteLine($"Filter over {LargeCollectionFixture.Count} documents: {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(LargeCollectionFixture.Count / 10, count);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Took {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("SELECT * FROM c WHERE c.status = @s AND c.n > 100")]
    [InlineData("SELECT c.id, c.name FROM c WHERE STARTSWITH(c.name, 'user9')")]
    [InlineData("SELECT VALUE c.id FROM c WHERE c.nested.v IN (1, 2, 3, 4, 5, 6, 7, 8)")]
    [InlineData("SELECT VALUE c.id FROM c WHERE ARRAY_CONTAINS(c.tags, 'b') AND c.name LIKE '%99%'")]
    [InlineData("SELECT VALUE COUNT(1) FROM c JOIN t IN c.tags WHERE t = 'a'")]
    [InlineData("SELECT c._rid, [{\"item\": c.name}] AS orderByItems, c AS payload FROM c WHERE true ORDER BY c.name ASC")]
    [InlineData("SELECT DISTINCT VALUE c.nested.v FROM c")]
    public void TypicalQueriesOverHundredThousandDocuments(string sql)
    {
        var query = SqlQuery.Parse(sql);
        var parameters = T.Parameters("""{"@s":"x"}""");
        query.Execute(large.Documents, parameters).Count(); // warm-up
        var sw = Stopwatch.StartNew();
        int count = query.Execute(large.Documents, parameters).Count();
        sw.Stop();
        output.WriteLine($"{sw.Elapsed.TotalMilliseconds,8:F1} ms  ({count} results)  {sql}");
        Assert.True(count > 0);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void OrderByWithTopOverHundredThousandDocuments()
    {
        var sw = Stopwatch.StartNew();
        var results = T.RunOn(large.Documents, "SELECT TOP 3 VALUE c.n FROM c WHERE c.nested.v = 7 ORDER BY c._ts DESC");
        sw.Stop();
        Assert.Equal("[99907,99807,99707]", results);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AggregateOverHundredThousandDocuments() =>
        Assert.Equal(
            """[{"status":"x","n":10000,"maxN":99990},{"status":"y","n":90000,"maxN":99999}]""",
            T.RunOn(large.Documents, "SELECT c.status, COUNT(1) AS n, MAX(c.n) AS maxN FROM c GROUP BY c.status"));

    [Fact]
    public void ConcurrentExecutionsDoNotInterfere()
    {
        var query = SqlQuery.Parse("SELECT VALUE COUNT(1) FROM c JOIN t IN c.tags WHERE c.nested.v = @v AND EXISTS(SELECT VALUE 1 FROM x IN c.tags WHERE x = @tag)");
        var results = new string[16];
        Parallel.For(0, results.Length, i =>
        {
            string parameters = $$"""{"@v":{{i}},"@tag":"{{(i % 2 == 0 ? "a" : "zzz")}}"}""";
            results[i] = T.Format(query.Execute(large.Documents, T.Parameters(parameters)));
        });

        for (int i = 0; i < results.Length; i++)
        {
            // 1000 documents per nested.v value, two tags each; odd i never matches the tag.
            Assert.Equal(i % 2 == 0 ? "[2000]" : "[0]", results[i]);
        }
    }

    [Fact]
    public void ConcurrentEnumerationsOfTheSameResultAreIndependent()
    {
        var results = SqlQuery.Parse("SELECT VALUE c.id FROM c WHERE c.age > 20 ORDER BY c._ts").Execute(T.Fixture);
        var outputs = new string[8];
        Parallel.For(0, outputs.Length, i => outputs[i] = T.Format(results));
        Assert.All(outputs, o => Assert.Equal(outputs[0], o));
    }

    [Fact]
    public void MissingParameterThrowsEagerly()
    {
        var query = SqlQuery.Parse("SELECT * FROM c WHERE c.id = @id");
        var ex = Assert.Throws<SqlQueryException>(() => query.Execute(T.Fixture));
        Assert.Contains("@id", ex.Message);
        Assert.Throws<SqlQueryException>(() => query.Execute(T.Fixture, T.Parameters("""{"@other":1}""")));
    }

    [Fact]
    public void MissingParameterThrowsEvenWithoutDocuments() =>
        Assert.Throws<SqlQueryException>(() => SqlQuery.Parse("SELECT VALUE @x").Execute([]));

    [Fact]
    public void ParametersOfAllTypes() =>
        Assert.Equal(
            """[[1.50,"s",true,null,[1,{"a":2}],{"k":[]}]]""",
            T.Run("SELECT VALUE [@n, @s, @b, @z, @a, @o]", """{"@n":1.50,"@s":"s","@b":true,"@z":null,"@a":[1,{"a":2}],"@o":{"k":[]}}"""));

    [Fact]
    public void UnresolvedIdentifierThrowsAtParse() =>
        Assert.Throws<SqlQueryException>(() => SqlQuery.Parse("SELECT * FROM c WHERE d.id = 1"));

    [Fact]
    public void NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => SqlQuery.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => SqlQuery.Parse("SELECT * FROM c").Execute(null!));
    }
}
