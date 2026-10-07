namespace DbEmulator.Query.Tests;

/// <summary>Query shapes produced by the .NET SDK's query plan rewrites and by its LINQ provider.</summary>
public class SdkQueryTests
{
    private static readonly string[] s_orderByDocs =
    [
        """{"id":"a","_rid":"ra","name":"Zed","age":30,"tags":["b","a"]}""",
        """{"id":"b","_rid":"rb","age":20}""",
        """{"id":"c","_rid":"rc","name":"Amy","age":40,"tags":["c"]}""",
    ];

    [Fact]
    public void OrderByRewriteWithMissingKeyProducesEmptyItem() =>
        Assert.Equal(
            "[" +
            """{"_rid":"rb","orderByItems":[{}],"payload":""" + s_orderByDocs[1] + "}," +
            """{"_rid":"rc","orderByItems":[{"item":"Amy"}],"payload":""" + s_orderByDocs[2] + "}," +
            """{"_rid":"ra","orderByItems":[{"item":"Zed"}],"payload":""" + s_orderByDocs[0] + "}]",
            T.RunDocs(s_orderByDocs, """SELECT c._rid, [{"item": c.name}] AS orderByItems, c AS payload FROM c WHERE true ORDER BY c.name ASC"""));

    [Fact]
    public void OrderByRewriteWithFilterAndTrue() =>
        Assert.Equal(
            "[" +
            """{"_rid":"rc","orderByItems":[{"item":40}],"payload":""" + s_orderByDocs[2] + "}," +
            """{"_rid":"ra","orderByItems":[{"item":30}],"payload":""" + s_orderByDocs[0] + "}]",
            T.RunDocs(s_orderByDocs, """SELECT c._rid, [{"item": c.age}] AS orderByItems, c AS payload FROM c WHERE (c.age > 21) AND (true) ORDER BY c.age DESC"""));

    [Fact]
    public void OrderByRewriteWithMultipleItems() =>
        // category b: r2 (25), r5 (null), r10 (22). ORDER BY category DESC, age DESC => 25, 22, null.
        Assert.Equal(
            """[{"_rid":"r2","orderByItems":[{"item":"b"},{"item":25}]},{"_rid":"r10","orderByItems":[{"item":"b"},{"item":22}]},{"_rid":"r5","orderByItems":[{"item":"b"},{"item":null}]}]""",
            T.Run("""SELECT c._rid, [{"item": c.category}, {"item": c.age}] AS orderByItems FROM c WHERE c.category = "b" ORDER BY c.category DESC, c.age DESC"""));

    [Fact]
    public void OrderByRewriteWithJoin() =>
        Assert.Equal(
            """[{"_rid":"ra","orderByItems":[{"item":"a"}],"payload":{"t":"a"}},{"_rid":"ra","orderByItems":[{"item":"b"}],"payload":{"t":"b"}},{"_rid":"rc","orderByItems":[{"item":"c"}],"payload":{"t":"c"}}]""",
            T.RunDocs(s_orderByDocs, """SELECT r._rid, [{"item": t}] AS orderByItems, {"t": t} AS payload FROM r JOIN t IN r.tags ORDER BY t"""));

    [Fact]
    public void OrderByRewriteWithContinuationFilter() =>
        Assert.Equal(
            T.J("['rc','ra']"),
            T.RunDocs(s_orderByDocs, """SELECT VALUE c._rid FROM c WHERE ((c.name >= "Amy")) AND (true) ORDER BY c.name ASC"""));

    [Theory]
    [InlineData("""SELECT VALUE [{"item": COUNT(1)}] FROM c""", "[[{'item':10}]]")]
    [InlineData("""SELECT VALUE [{"item": {"sum": SUM(c._ts), "count": COUNT(c._ts)}}] FROM c""", "[[{'item':{'sum':5500,'count':10}}]]")]
    [InlineData("""SELECT VALUE [{"item": {"min": MIN(c.age), "count": COUNT(c.age)}}] FROM c WHERE IS_NUMBER(c.age)""", "[[{'item':{'min':22,'count':7}}]]")]
    [InlineData("""SELECT VALUE [{"item": {"max": MAX(c.x), "count": COUNT(c.x)}}] FROM c""", "[[{'item':{'count':0}}]]")]
    [InlineData("""SELECT VALUE [{"item": COUNT(1)}, {"item": MAX(c._ts)}, {"item": {"sum": SUM(c.price), "count": COUNT(c.price)}}] FROM c WHERE c.category = 'b'""", "[[{'item':3},{'item':1000},{'item':{'sum':2.5,'count':1}}]]")]
    [InlineData("""SELECT VALUE [{"item": COUNT(1)}] FROM c WHERE false""", "[[{'item':0}]]")]
    [InlineData("""SELECT VALUE [{"item": MIN(c.x)}] FROM c""", "[[{}]]")]
    public void AggregateRewrites(string sql, string expected) => Assert.Equal(T.J(expected), T.Run(sql));

    [Fact]
    public void GroupByRewrite() =>
        Assert.Equal(
            """[{"groupByItems":[{"item":"a"}],"payload":{"category":"a","n":{"item":4}}},{"groupByItems":[{"item":"b"}],"payload":{"category":"b","n":{"item":3}}},{"groupByItems":[{"item":"c"}],"payload":{"category":"c","n":{"item":2}}},{"groupByItems":[{}],"payload":{"n":{"item":1}}}]""",
            T.Run("""SELECT [{"item": c.category}] AS groupByItems, {"category": c.category, "n": {"item": COUNT(1)}} AS payload FROM c GROUP BY c.category"""));

    [Fact]
    public void GroupByRewriteWithSumAndAvg() =>
        Assert.Equal(
            """[{"groupByItems":[{"item":"inactive"}],"payload":{"status":"inactive","total":{"item":{"sum":900,"count":2}}}}]""",
            T.Run("""SELECT [{"item": c.status}] AS groupByItems, {"status": c.status, "total": {"item": {"sum": SUM(c._ts), "count": COUNT(c._ts)}}} AS payload FROM c WHERE c.status = 'inactive' GROUP BY c.status"""));

    [Fact]
    public void DistinctValueRewrite() => Assert.Equal(T.J("['b','a','c']"), T.Run("SELECT DISTINCT VALUE c.category FROM c ORDER BY c._ts DESC"));

    [Fact]
    public void DistinctOrderByRewrite() =>
        Assert.Equal("""[{},{"category":"a"},{"category":"b"},{"category":"c"}]""", T.Run("SELECT DISTINCT c.category FROM c ORDER BY c.category"));

    [Fact]
    public void TopRewrite() => Assert.Equal(T.J("['1','2','3','4','5']"), T.Run("SELECT TOP 5 VALUE c.id FROM c"));

    [Fact]
    public void OffsetLimitRewrite() =>
        Assert.Equal(T.J("['1','2','3','4','5','6','7','8','9','10']"), T.Run("SELECT VALUE c.id FROM c OFFSET 0 LIMIT 15"));

    // ------------------------------------------------------------------ LINQ provider shapes

    [Fact]
    public void LinqWhereStartsWithOrderByDesc() =>
        Assert.Equal(
            "[" + T.FixtureJson[0] + "]",
            T.Run("""SELECT VALUE root FROM root WHERE ((root["status"] = "active") AND STARTSWITH(root["name"], "A")) ORDER BY root["_ts"] DESC"""));

    [Fact]
    public void LinqAnonymousProjection() =>
        Assert.Equal(
            """[{"Name":"Alice","Age":30},{"Age":"40"}]""",
            T.Run("""SELECT VALUE {"Name": root["name"], "Age": root["age"]} FROM root WHERE root["id"] IN ("1", "4")"""));

    [Fact]
    public void LinqCount() => Assert.Equal("[2]", T.Run("""SELECT VALUE COUNT(1) FROM root WHERE IS_DEFINED(root["items"])"""));

    [Fact]
    public void LinqAnyOverArray() =>
        Assert.Equal(
            T.J("['1','4','6','9']"),
            T.Run("""SELECT VALUE root["id"] FROM root WHERE EXISTS(SELECT VALUE t0 FROM root JOIN t0 IN root["tags"] WHERE (t0 = "x"))"""));

    [Fact]
    public void LinqAnyReturningDocuments() =>
        Assert.Equal(
            "[" + T.FixtureJson[5] + "]",
            T.Run("""SELECT VALUE root FROM root WHERE EXISTS(SELECT VALUE t0 FROM root JOIN t0 IN root["tags"] WHERE (t0 = "z"))"""));

    [Fact]
    public void LinqCoalesce() =>
        Assert.Equal(T.J("['Alice',null,null]"), T.Run("""SELECT VALUE (root["name"] ?? null) FROM root WHERE root["id"] IN ("1", "4", "5")"""));

    [Fact]
    public void LinqArrayContainsParameter() =>
        Assert.Equal(T.J("['2','3']"), T.Run("""SELECT VALUE root["id"] FROM root WHERE ARRAY_CONTAINS(@p, root["id"])""", """{"@p":["2","3","99"]}"""));

    [Fact]
    public void LinqInList() => Assert.Equal(T.J("['1','2']"), T.Run("""SELECT VALUE root["id"] FROM root WHERE root["id"] IN ("a", "1", "2")"""));

    [Fact]
    public void LinqSelectMany() =>
        Assert.Equal(T.J("['a','b','c']"), T.Run("""SELECT VALUE t0["sku"] FROM root JOIN t0 IN root["items"]"""));

    [Fact]
    public void LinqTakeAndSkip() =>
        Assert.Equal(T.J("['8','7']"), T.Run("""SELECT VALUE root["id"] FROM root ORDER BY root["_ts"] DESC OFFSET 2 LIMIT 2"""));

    [Fact]
    public void LinqFirstOrDefault() => Assert.Equal(T.J("['3']"), T.Run("""SELECT TOP 1 VALUE root["id"] FROM root WHERE (root["age"] > 30)"""));

    [Fact]
    public void LinqStringFunctions() =>
        Assert.Equal(
            T.J("['1','9']"),
            T.Run("""SELECT VALUE root["id"] FROM root WHERE (CONTAINS(LOWER(root["status"]), "act") AND (root["age"] = 30))"""));

    [Fact]
    public void LinqConditional() =>
        Assert.Equal(T.J("['old','young']"), T.Run("""SELECT VALUE ((root["age"] > 26) ? "old" : "young") FROM root WHERE root["id"] IN ("1", "2")"""));

    [Fact]
    public void LinqNullCheck() =>
        Assert.Equal(T.J("['4','5']"), T.Run("""SELECT VALUE root["id"] FROM root WHERE ((root["name"] = null) OR NOT IS_DEFINED(root["name"]))"""));

    [Fact]
    public void LinqStringEqualsIgnoreCase() =>
        Assert.Equal(T.J("['1','3','4','6','8','9','10']"), T.Run("""SELECT VALUE root["id"] FROM root WHERE STRINGEQUALS(root["status"], "Active", true)"""));

    [Fact]
    public void LinqGroupBy() =>
        Assert.Equal(
            """[{"Key":"a","Count":4},{"Key":"b","Count":3},{"Key":"c","Count":2},{"Count":1}]""",
            T.Run("""SELECT VALUE {"Key": root["category"], "Count": COUNT(1)} FROM root GROUP BY root["category"]"""));
}
