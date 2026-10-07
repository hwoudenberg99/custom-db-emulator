namespace DbEmulator.Query.Tests;

public class ParserTests
{
    [Fact]
    public void SyntaxErrorReportsTokenAndPosition()
    {
        var ex = Assert.Throws<SqlQueryException>(() => SqlQuery.Parse("SELECT * FORM c"));
        Assert.Contains("Syntax error near 'FORM' at position 9", ex.Message);
    }

    [Fact]
    public void SyntaxErrorAtEndOfInputReportsPosition()
    {
        var ex = Assert.Throws<SqlQueryException>(() => SqlQuery.Parse("SELECT * FROM c WHERE"));
        Assert.Contains("position 21", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SELECT")]
    [InlineData("DELETE FROM c")]
    [InlineData("SELECT * FROM")]
    [InlineData("SELECT * FROM c WHERE")]
    [InlineData("SELECT c.id FROM c WHERE c.id = 1 AND")]
    [InlineData("SELECT (1 FROM c")]
    [InlineData("SELECT [1, 2 FROM c")]
    [InlineData("SELECT {\"a\" 1} FROM c")]
    [InlineData("SELECT 'abc FROM c")]
    [InlineData("SELECT # FROM c")]
    [InlineData("SELECT * FROM c ORDER c.id")]
    [InlineData("SELECT * FROM c OFFSET 1")]
    [InlineData("SELECT * FROM c LIMIT 1")]
    [InlineData("SELECT TOP -1 * FROM c")]
    [InlineData("SELECT TOP 1.5 * FROM c")]
    [InlineData("SELECT TOP 'a' * FROM c")]
    [InlineData("SELECT * FROM c WHERE c.a IN ()")]
    [InlineData("SELECT 'a\\q' FROM c")]
    [InlineData("SELECT /* unterminated FROM c")]
    [InlineData("SELECT * FROM c c2 c3")]
    [InlineData("SELECT * FROM c WHERE c.a BETWEEN 1")]
    [InlineData("SELECT * FROM c WHERE c.a ? 1")]
    [InlineData("SELECT COUNT(*) FROM c")]
    [InlineData("SELECT * FROM c JOIN")]
    [InlineData("SELECT VALUE FROM c")]
    public void InvalidSyntaxThrows(string sql) => Assert.Throws<SqlQueryException>(() => SqlQuery.Parse(sql));

    [Fact]
    public void KeywordsAreValidPropertyNamesAfterDot()
    {
        string[] docs = ["""{"value":1,"order":2,"from":3,"select":4,"top":5,"by":6,"in":7,"join":8}"""];
        Assert.Equal(
            """[{"value":1,"order":2,"from":3,"select":4,"top":5,"by":6,"in":7,"join":8}]""",
            T.RunDocs(docs, "SELECT c.value, c.order, c.from, c.select, c.top, c.by, c.in, c.join FROM c"));
    }

    [Fact]
    public void RootOrderPropertyParses()
    {
        string[] docs = ["""{"order":{"total":5}}"""];
        Assert.Equal("[5]", T.RunDocs(docs, "SELECT VALUE root.order.total FROM root"));
    }

    [Fact]
    public void KeywordsAreCaseInsensitive() =>
        Assert.Equal(T.J("['7']"), T.Run("select value c.id from c where c.age > 40 order by c.id desc"));

    [Fact]
    public void MixedCaseKeywords() =>
        Assert.Equal(T.J("['1','2']"), T.Run("SeLeCt ToP 2 VaLuE c.id FrOm c"));

    [Fact]
    public void IdentifiersAreCaseSensitive()
    {
        var ex = T.Error("SELECT VALUE C.id FROM c");
        Assert.Contains("Identifier 'C' could not be resolved", ex.Message);
    }

    [Fact]
    public void PropertyNamesAreCaseSensitive() => Assert.Equal("[]", T.Run("SELECT VALUE c.ID FROM c"));

    [Fact]
    public void CommentsAreIgnored() =>
        Assert.Equal(
            T.J("['3']"),
            T.Run("SELECT /* block\n comment */ VALUE c.id -- line comment\nFROM c WHERE c.id = '3' -- trailing"));

    [Fact]
    public void WhitespaceAndNewlinesAreIgnored() =>
        Assert.Equal(T.J("['2']"), T.Run("\r\n\tSELECT\r\n VALUE\tc.id\nFROM\n c\n WHERE\n c.id\n=\n'2'\n"));

    [Theory]
    [InlineData("0x1F", "31")]
    [InlineData("0xff", "255")]
    [InlineData("1.5e2", "150")]
    [InlineData("2E-1", "0.2")]
    [InlineData(".5", "0.5")]
    [InlineData("-3", "-3")]
    [InlineData("- - 3", "3")]
    [InlineData("'it\\'s'", "\"it's\"")]
    [InlineData("\"say \\\"hi\\\"\"", "\"say \\\"hi\\\"\"")]
    [InlineData("'a\\u0041\\tb'", "\"aA\\tb\"")]
    [InlineData("'\\/\\\\'", "\"/\\\\\"")]
    [InlineData("\"it's\"", "\"it's\"")]
    [InlineData("true", "true")]
    [InlineData("FALSE", "false")]
    [InlineData("Null", "null")]
    [InlineData("undefined", "undefined")]
    [InlineData("[]", "[]")]
    [InlineData("{}", "{}")]
    [InlineData("[1, 'a', null, [true]]", "[1,\"a\",null,[true]]")]
    [InlineData("{a: 1, \"b c\": 'x', value: {n: null}}", "{\"a\":1,\"b c\":\"x\",\"value\":{\"n\":null}}")]
    public void Literals(string expression, string expected) => Assert.Equal(expected, T.Eval(expression));

    [Fact]
    public void ProjectionNames() =>
        Assert.Equal(
            """[{"name":"Alice","city":"Seattle","first name":"Al","$1":"x","$2":3,"low":"alice","id2":"1"}]""",
            T.Run("""SELECT c.name, c.address.city, c["first name"], c.tags[0], 1+2, LOWER(c.name) AS low, c.id id2 FROM c WHERE c.id = '1'"""));

    [Fact]
    public void ProjectionOfAliasUsesAliasName() =>
        Assert.Equal("[{\"c\":" + T.FixtureJson[1] + "}]", T.Run("SELECT c FROM c WHERE c.id = '2'"));

    [Fact]
    public void ProjectionOmitsUndefinedProperties() =>
        Assert.Equal("""[{"id":"4"}]""", T.Run("SELECT c.id, c.name FROM c WHERE c.id = '4'"));

    [Fact]
    public void ProjectionOfOnlyUndefinedIsEmptyObject() =>
        Assert.Equal("[{}]", T.Run("SELECT c.missing FROM c WHERE c.id = '1'"));

    [Fact]
    public void ProjectionKeepsSelectListOrder() =>
        Assert.Equal("""[{"b":"2","a":25}]""", T.Run("SELECT c.id AS b, c.age AS a FROM c WHERE c.id = '2'"));

    [Fact]
    public void SelectWithoutFrom() => Assert.Equal("""[{"$1":1}]""", T.Run("SELECT 1"));

    [Fact]
    public void SelectListWithoutFromNumbersUnnamedItems() =>
        Assert.Equal("""[{"$1":1,"x":2,"$2":"a"}]""", T.Run("SELECT 1, 2 AS x, 'a'"));

    [Fact]
    public void SelectValueArrayWithoutFrom() => Assert.Equal("[[1,2]]", T.Run("SELECT VALUE [1,2]"));

    [Fact]
    public void AliasAfterAsMayBeAKeyword() =>
        Assert.Equal("""[{"value":"1"}]""", T.Run("SELECT c.id AS value FROM c WHERE c.id = '1'"));

    [Fact]
    public void DuplicateProjectionNameThrows() => T.Error("SELECT c.id, c.id FROM c");

    [Fact]
    public void DuplicateObjectLiteralKeyThrows() => T.Error("SELECT VALUE {'a': 1, 'a': 2}");

    [Fact]
    public void DuplicateFromAliasThrows() => T.Error("SELECT * FROM c JOIN c IN c.tags");

    [Fact]
    public void SelectStarWithoutFromThrows() => T.Error("SELECT *");

    [Fact]
    public void SelectStarWithJoinThrows()
    {
        var ex = T.Error("SELECT * FROM c JOIN t IN c.tags");
        Assert.Contains("'SELECT *' is only valid with a single input set.", ex.Message);
    }

    [Fact]
    public void TopBeforeDistinct() => Assert.Equal(T.J("['a','b']"), T.Run("SELECT TOP 2 DISTINCT VALUE c.category FROM c"));

    [Fact]
    public void DistinctBeforeTop() => Assert.Equal(T.J("['a','b']"), T.Run("SELECT DISTINCT TOP 2 VALUE c.category FROM c"));

    [Fact]
    public void DeepNestingThrowsQueryExceptionInsteadOfOverflowing()
    {
        string sql = "SELECT VALUE " + new string('(', 5000) + "1" + new string(')', 5000);
        Assert.Throws<SqlQueryException>(() => SqlQuery.Parse(sql));
    }

    [Fact]
    public void LongOperatorChainsWork()
    {
        string sql = "SELECT VALUE " + string.Join(" + ", Enumerable.Repeat("1", 500));
        Assert.Equal("[500]", T.Run(sql));
    }

    [Fact]
    public void LongInListWorks()
    {
        string sql = "SELECT VALUE c.id FROM c WHERE c._ts IN (" + string.Join(", ", Enumerable.Range(0, 5000)) + ")";
        Assert.Equal(T.J("['1','2','3','4','5','6','7','8','9','10']"), T.Run(sql));
    }

    [Fact]
    public void TextPropertyReturnsOriginalQuery()
    {
        const string sql = "SELECT * FROM c";
        Assert.Equal(sql, SqlQuery.Parse(sql).Text);
    }

    [Fact]
    public void UnknownFunctionThrowsAtParse()
    {
        var ex = Assert.Throws<SqlQueryException>(() => SqlQuery.Parse("SELECT VALUE FOO(c.id) FROM c"));
        Assert.Contains("FOO", ex.Message);
    }

    [Fact]
    public void WrongArgumentCountThrows() => Assert.Throws<SqlQueryException>(() => SqlQuery.Parse("SELECT VALUE LOWER('a', 'b')"));
}
