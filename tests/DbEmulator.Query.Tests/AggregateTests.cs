using System.Globalization;

namespace DbEmulator.Query.Tests;

public class AggregateTests
{
    private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    [Fact]
    public void AggregatesWithoutGroupByYieldOneResult() =>
        Assert.Equal(
            "[{\"n\":10,\"s\":" + Num(1.1 + 2.5 + 3 + 10) + ",\"mn\":null,\"mx\":\"40\"}]",
            T.Run("SELECT COUNT(1) AS n, SUM(c.price) AS s, AVG(c.age) AS a, MIN(c.age) AS mn, MAX(c.age) AS mx FROM c"));

    [Fact]
    public void AverageOfNumbers() =>
        Assert.Equal("[" + Num((30 + 25 + 35 + 28.5 + 45 + 30 + 22) / 7) + "]", T.Run("SELECT VALUE AVG(c.age) FROM c WHERE IS_NUMBER(c.age)"));

    [Theory]
    [InlineData("SELECT COUNT(1) AS n FROM c WHERE false", "[{'n':0}]")]
    [InlineData("SELECT VALUE COUNT(1) FROM c WHERE false", "[0]")]
    [InlineData("SELECT VALUE SUM(c.price) FROM c WHERE false", "[0]")]
    [InlineData("SELECT VALUE MAX(c.x) FROM c WHERE false", "[]")]
    [InlineData("SELECT VALUE MIN(c.x) FROM c WHERE false", "[]")]
    [InlineData("SELECT VALUE AVG(c.x) FROM c WHERE false", "[]")]
    [InlineData("SELECT VALUE [{'item': MIN(c.x)}] FROM c WHERE false", "[[{}]]")]
    [InlineData("SELECT c.id, COUNT(1) AS n FROM c WHERE false", "[{'n':0}]")]
    [InlineData("SELECT VALUE MAKELIST(c.x) FROM c WHERE false", "[[]]")]
    public void AggregatesOverZeroRows(string sql, string expected) => Assert.Equal(T.J(expected), T.Run(sql));

    [Theory]
    [InlineData("SELECT VALUE COUNT(1) FROM c", "[10]")]
    [InlineData("SELECT VALUE COUNT(c.name) FROM c", "[9]")]
    [InlineData("SELECT VALUE COUNT(c.items) FROM c", "[2]")]
    [InlineData("SELECT VALUE SUM(c.age) FROM c", "[]")]
    [InlineData("SELECT VALUE SUM(c.missing) FROM c", "[0]")]
    [InlineData("SELECT VALUE AVG(c.missing) FROM c", "[]")]
    [InlineData("SELECT VALUE AVG(c.age) FROM c WHERE c.id IN ('1', '4')", "[]")]
    [InlineData("SELECT VALUE SUM(c._ts) FROM c", "[5500]")]
    [InlineData("SELECT VALUE AVG(c._ts) FROM c", "[550]")]
    [InlineData("SELECT VALUE MIN(c._ts) FROM c", "[100]")]
    [InlineData("SELECT VALUE MAX(c._ts) FROM c", "[1000]")]
    [InlineData("SELECT VALUE MAX(c.name) FROM c", "['dave']")]
    [InlineData("SELECT VALUE MIN(c.name) FROM c", "[null]")]
    [InlineData("SELECT VALUE MIN(c.name) FROM c WHERE IS_STRING(c.name)", "['Alice']")]
    [InlineData("SELECT VALUE MIN(m) FROM c JOIN m IN c.mixed", "[]")]
    [InlineData("SELECT VALUE MAX(m) FROM c JOIN m IN c.mixed WHERE IS_PRIMITIVE(m)", "['a']")]
    [InlineData("SELECT VALUE MIN(m) FROM c JOIN m IN c.mixed WHERE IS_PRIMITIVE(m)", "[null]")]
    [InlineData("SELECT VALUE MAX(c.isMissing ?? c.age) FROM c WHERE IS_BOOL(c.age) OR IS_NUMBER(c.age)", "[45]")]
    [InlineData("SELECT VALUE MAKELIST(c.category) FROM c", "[['a','b','a','c','b','a','c','a','b']]")]
    [InlineData("SELECT VALUE MAKESET(c.category) FROM c", "[['a','b','c']]")]
    [InlineData("SELECT VALUE COUNT(1) FROM c JOIN t IN c.tags", "[10]")]
    [InlineData("SELECT VALUE {'n': {'item': SUM(c.price)}} FROM c WHERE c.category = 'b'", "[{'n':{'item':2.5}}]")]
    [InlineData("SELECT VALUE [COUNT(1), MIN(c._ts), MAX(c._ts)] FROM c WHERE c.category = 'a'", "[[4,100,900]]")]
    [InlineData("SELECT VALUE COUNT(1) + 1 FROM c", "[11]")]
    public void Aggregates(string sql, string expected) => Assert.Equal(T.J(expected), T.Run(sql));

    [Fact]
    public void MinMaxReturnSourceValuesVerbatim() =>
        Assert.Equal("[1.10]", T.Run("SELECT VALUE MIN(c.price) FROM c"));

    [Fact]
    public void GroupByCountsInFirstAppearanceOrder() =>
        Assert.Equal(
            """[{"category":"a","n":4},{"category":"b","n":3},{"category":"c","n":2},{"n":1}]""",
            T.Run("SELECT c.category, COUNT(1) AS n FROM c GROUP BY c.category"));

    [Fact]
    public void GroupBySum() =>
        Assert.Equal(
            "[{\"k\":\"a\",\"s\":" + Num(1.1 + 3 + 10) + "},{\"k\":\"b\",\"s\":2.5},{\"k\":\"c\",\"s\":0},{\"s\":0}]",
            T.Run("SELECT VALUE {'k': c.category, 's': SUM(c.price)} FROM c GROUP BY c.category"));

    [Fact]
    public void GroupByMultipleKeys() =>
        Assert.Equal(
            """[{"category":"a","status":"active","n":3},{"category":"a","status":"ACTIVE","n":1}]""",
            T.Run("SELECT c.category, c.status, COUNT(1) AS n FROM c WHERE c.category = 'a' GROUP BY c.category, c.status"));

    [Fact]
    public void GroupByWithZeroRowsYieldsNothing() =>
        Assert.Equal("[]", T.Run("SELECT COUNT(1) AS n FROM c WHERE false GROUP BY c.category"));

    [Fact]
    public void GroupByNonAggregateExpressionsUseFirstRow() =>
        Assert.Equal(
            """[{"category":"a","firstId":"1"},{"category":"b","firstId":"2"},{"category":"c","firstId":"4"},{"firstId":"7"}]""",
            T.Run("SELECT c.category, c.id AS firstId FROM c GROUP BY c.category"));

    [Fact]
    public void GroupByWithOrderBy() =>
        Assert.Equal(T.J("['c','b','a']"), T.Run("SELECT VALUE c.category FROM c GROUP BY c.category ORDER BY c.category DESC"));

    [Fact]
    public void GroupByWithTop() =>
        Assert.Equal(T.J("['a','b']"), T.Run("SELECT TOP 2 VALUE c.category FROM c GROUP BY c.category"));

    [Fact]
    public void GroupByWithDistinct() =>
        Assert.Equal("[6,2,1]", T.Run("SELECT DISTINCT VALUE COUNT(1) FROM c GROUP BY c.status"));

    [Fact]
    public void GroupByExpressionKey() =>
        Assert.Equal(
            """[{"bucket":false,"n":5},{"bucket":true,"n":5}]""",
            T.Run("SELECT VALUE {'bucket': c._ts > 500, 'n': COUNT(1)} FROM c GROUP BY c._ts > 500"));

    [Fact]
    public void GroupByObjectKeysUsesDeepEquality() =>
        Assert.Equal("[1,1,1,7]", T.Run("SELECT VALUE COUNT(1) FROM c GROUP BY c.address"));

    [Fact]
    public void GroupByJoinAlias() =>
        Assert.Equal(
            """[{"tag":"x","n":4},{"tag":"y","n":5},{"tag":"z","n":1}]""",
            T.Run("SELECT t AS tag, COUNT(1) AS n FROM c JOIN t IN c.tags GROUP BY t"));

    [Fact]
    public void GroupByDocumentPropertyOverJoin() =>
        Assert.Equal(
            """[{"category":"a","tagCount":6},{"category":"b","tagCount":3},{"category":"c","tagCount":1}]""",
            T.Run("SELECT c.category, COUNT(1) AS tagCount FROM c JOIN t IN c.tags GROUP BY c.category"));

    [Fact]
    public void SubqueryAggregateInsideAggregate() =>
        Assert.Equal(
            """[{"category":"a","maxTags":3},{"category":"b","maxTags":2},{"category":"c","maxTags":1},{"maxTags":0}]""",
            T.Run("SELECT c.category, MAX((SELECT VALUE COUNT(1) FROM t IN c.tags)) AS maxTags FROM c GROUP BY c.category"));

    [Fact]
    public void AggregateOverFromSubquery() =>
        Assert.Equal("[2]", T.Run("SELECT VALUE COUNT(1) FROM (SELECT VALUE c.id FROM c WHERE c.status = 'inactive') x"));

    [Fact]
    public void AggregateSubqueryAndGroupByInSubquery() =>
        Assert.Equal(
            """[{"id":"10","counts":[{"t":"y","n":2}]}]""",
            T.Run("SELECT c.id, ARRAY(SELECT t, COUNT(1) AS n FROM t IN c.tags GROUP BY t) AS counts FROM c WHERE c.id = '10'"));

    [Theory]
    [InlineData("SELECT * FROM c WHERE COUNT(1) > 1")]
    [InlineData("SELECT VALUE SUM(COUNT(1)) FROM c")]
    [InlineData("SELECT VALUE COUNT(1) FROM c GROUP BY COUNT(1)")]
    [InlineData("SELECT VALUE c.id FROM c ORDER BY COUNT(1)")]
    [InlineData("SELECT VALUE COUNT(1, 2) FROM c")]
    [InlineData("SELECT * FROM c GROUP BY c.category")]
    public void InvalidAggregateUsageThrows(string sql) => T.Error(sql);
}
