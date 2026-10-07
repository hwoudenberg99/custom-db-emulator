namespace DbEmulator.Query.Tests;

public class QueryTests
{
    // ------------------------------------------------------------------ WHERE

    [Theory]
    [InlineData("c.status = 'active'", "['1','3','4','6','8','10']")]
    [InlineData("c.age > 28", "['1','3','6','7','9']")]
    [InlineData("c.age >= '3'", "['4']")]
    [InlineData("c.age = null", "['5']")]
    [InlineData("c.name = 'Alice' OR c.age = 25", "['1','2']")]
    [InlineData("NOT (c.age > 28)", "['2','10']")]
    [InlineData("c.age != 30", "['2','3','4','5','6','7','8','10']")]
    [InlineData("c.missing != 1", "[]")]
    [InlineData("c.age BETWEEN 25 AND 30", "['1','2','6','9']")]
    [InlineData("c.status IN ('pending', 'inactive')", "['2','5','7']")]
    [InlineData("c.status NOT IN ('pending', 'inactive')", "['1','3','4','6','8','9','10']")]
    [InlineData("c.name LIKE '%a%'", "['3','6','8','9']")]
    [InlineData("c.address.city = 'Seattle'", "['1','3']")]
    [InlineData("c.tags[0] = 'x'", "['1','4','9']")]
    [InlineData("c.age", "['8']")]
    [InlineData("c.status", "[]")]
    [InlineData("true", "['1','2','3','4','5','6','7','8','9','10']")]
    [InlineData("false", "[]")]
    [InlineData("undefined", "[]")]
    [InlineData("IS_DEFINED(c.name) AND c.name = null", "['5']")]
    [InlineData("c.age = 30 AND c.status = 'active'", "['1']")]
    [InlineData("c.age > 40 OR c.age < 23", "['7','10']")]
    [InlineData("c.price = 1.1", "['1']")]
    [InlineData("c.big = 12345678901234567890", "['1']")]
    public void Where(string condition, string expected) =>
        Assert.Equal(T.J(expected), T.Run("SELECT VALUE c.id FROM c WHERE " + condition));

    [Fact]
    public void WhereWithParameters() =>
        Assert.Equal(
            T.J("['1','2','3','6','9']"),
            T.Run("SELECT VALUE c.id FROM c WHERE c.age >= @min AND c.age <= @max AND c.status != @s", """{"@min":25,"@max":35,"@s":"pending"}"""));

    [Fact]
    public void WhereWithArrayParameter() =>
        Assert.Equal(T.J("['2','3']"), T.Run("SELECT VALUE c.id FROM c WHERE ARRAY_CONTAINS(@ids, c.id)", """{"@ids":["2","3","99"]}"""));

    // ------------------------------------------------------------------ SELECT *

    [Fact]
    public void SelectStarReturnsDocumentsVerbatim() =>
        Assert.Equal("[" + string.Join(",", T.FixtureJson) + "]", T.Run("SELECT * FROM c"));

    [Fact]
    public void SelectStarKeepsOriginalNumberText()
    {
        var result = SqlQuery.Parse("SELECT * FROM c WHERE c.id = '1'").Execute(T.Fixture).Single();
        string json = result.ToJsonString();
        Assert.Contains("\"price\":1.10", json);
        Assert.Contains("\"big\":12345678901234567890", json);
        Assert.True(result.TryGetElement(out var element));
        Assert.Equal(T.Fixture[0].GetRawText(), element.GetRawText());
    }

    [Fact]
    public void SelectStarWithAnyCollectionName() =>
        Assert.Equal("[" + T.FixtureJson[2] + "]", T.Run("SELECT * FROM Families f WHERE f.id = '3'"));

    [Fact]
    public void SelectStarFromRoot() =>
        Assert.Equal("[" + T.FixtureJson[2] + "]", T.Run("SELECT * FROM root WHERE root.id = '3'"));

    // ------------------------------------------------------------------ FROM forms

    [Fact]
    public void FromPathBindsAliasToPathValue() =>
        Assert.Equal(T.J("['Seattle','Portland','Seattle']"), T.Run("SELECT VALUE x.city FROM root.address x"));

    [Fact]
    public void FromPathUsesImplicitAliasOfLastProperty() =>
        Assert.Equal(T.J("['Seattle','Portland','Seattle']"), T.Run("SELECT VALUE address.city FROM Families.address"));

    [Fact]
    public void FromIndexedPathDropsUndefinedRows() =>
        Assert.Equal(T.J("['x','y','x','z','x','y']"), T.Run("SELECT VALUE t FROM c.tags[0] t"));

    [Fact]
    public void FromInIteratesArrays() =>
        Assert.Equal(T.J("['a','b','c']"), T.Run("SELECT VALUE ch.sku FROM ch IN Families.items"));

    [Fact]
    public void FromInWithSameNameAsCollection() =>
        Assert.Equal(T.J("['x','y','y','x','z','x','y','x','y','y']"), T.Run("SELECT VALUE c FROM c IN c.tags"));

    [Fact]
    public void SelectStarFromIn() =>
        Assert.Equal("""[{"sku":"a","qty":2},{"sku":"b","qty":3},{"sku":"c","qty":1}]""", T.Run("SELECT * FROM i IN c.items"));

    [Fact]
    public void CollectionNameIsNotInScopeAfterAliasing() => T.Error("SELECT VALUE root.id FROM root r");

    // ------------------------------------------------------------------ JOIN

    [Fact]
    public void JoinIteratesArraysAndDropsNonArrays() =>
        Assert.Equal(
            """[{"id":"1","tag":"x"},{"id":"1","tag":"y"},{"id":"2","tag":"y"},{"id":"4","tag":"x"},{"id":"6","tag":"z"},{"id":"6","tag":"x"},{"id":"6","tag":"y"},{"id":"9","tag":"x"},{"id":"10","tag":"y"},{"id":"10","tag":"y"}]""",
            T.Run("SELECT c.id, t AS tag FROM c JOIN t IN c.tags"));

    [Fact]
    public void JoinWithFilter() =>
        Assert.Equal(T.J("['x','y','z','x','y','x']"), T.Run("SELECT VALUE t FROM c JOIN t IN c.tags WHERE c.category = 'a'"));

    [Fact]
    public void JoinFilterOnJoinedAlias() =>
        Assert.Equal(T.J("['6']"), T.Run("SELECT VALUE c.id FROM c JOIN t IN c.tags WHERE t = 'z'"));

    [Fact]
    public void DoubleJoinIsCrossProduct() =>
        Assert.Equal(
            T.J("[['a','z'],['a','x'],['a','y'],['b','z'],['b','x'],['b','y'],['c','y'],['c','y']]"),
            T.Run("SELECT VALUE [i.sku, t] FROM c JOIN i IN c.items JOIN t IN c.tags"));

    [Fact]
    public void JoinOverNestedAlias() =>
        Assert.Equal("[2,3,1]", T.Run("SELECT VALUE q FROM c JOIN i IN c.items JOIN q IN [i.qty]"));

    [Fact]
    public void JoinExpressionBindsValue() =>
        Assert.Equal(
            """[{"id":"1","city":"Seattle"},{"id":"2","city":"Portland"},{"id":"3","city":"Seattle"}]""",
            T.Run("SELECT c.id, a.city FROM c JOIN c.address a"));

    [Fact]
    public void JoinOnEmptyArrayDropsRow() => Assert.Equal("[]", T.Run("SELECT VALUE t FROM c JOIN t IN c.tags WHERE c.id = '3'"));

    [Fact]
    public void JoinSubqueryIteratesResults() =>
        Assert.Equal(T.J("['6','6']"), T.Run("SELECT VALUE c.id FROM c JOIN (SELECT VALUE i FROM i IN c.items WHERE i.qty > 1) big"));

    // ------------------------------------------------------------------ ORDER BY

    [Fact]
    public void OrderByMixedTypesAscending() =>
        Assert.Equal(T.J("['5','8','10','2','6','1','9','3','7','4']"), T.Run("SELECT VALUE c.id FROM c ORDER BY c.age"));

    [Fact]
    public void OrderByMixedTypesDescendingReversesTies() =>
        Assert.Equal(T.J("['4','7','3','9','1','6','2','10','8','5']"), T.Run("SELECT VALUE c.id FROM c ORDER BY c.age DESC"));

    [Fact]
    public void OrderByUndefinedSortsFirstAndIsIncluded() =>
        Assert.Equal(T.J("['4','5','1','2','3','7','8','9','10','6']"), T.Run("SELECT VALUE c.id FROM c ORDER BY c.name ASC"));

    [Fact]
    public void OrderByMultipleKeys() =>
        Assert.Equal(T.J("['7','9','6','3','1','10','5','2','8','4']"), T.Run("SELECT VALUE c.id FROM c ORDER BY c.category, c._ts DESC"));

    [Fact]
    public void OrderByKeyNotInProjection() =>
        Assert.Equal(T.J("['Heidi','Bob']"), T.Run("SELECT TOP 2 VALUE c.name FROM c WHERE IS_NUMBER(c.age) ORDER BY c.age"));

    [Fact]
    public void OrderByJoinAlias() =>
        Assert.Equal(T.J("['x','x','x','x','y','y','y','y','y','z']"), T.Run("SELECT VALUE t FROM c JOIN t IN c.tags ORDER BY t"));

    [Fact]
    public void OrderByAllKindsOfValues()
    {
        string[] docs =
        [
            """{"id":"obj","v":{"a":1}}""", """{"id":"arr","v":[1]}""", """{"id":"str","v":"s"}""", """{"id":"num","v":2}""",
            """{"id":"null","v":null}""", """{"id":"true","v":true}""", """{"id":"false","v":false}""", """{"id":"undef"}""",
        ];
        Assert.Equal(
            T.J("['undef','null','false','true','num','str','arr','obj']"),
            T.RunDocs(docs, "SELECT VALUE c.id FROM c ORDER BY c.v"));
        Assert.Equal(
            T.J("['obj','arr','str','num','true','false','null','undef']"),
            T.RunDocs(docs, "SELECT VALUE c.id FROM c ORDER BY c.v DESC"));
    }

    [Fact]
    public void OrderByTieBreaksOnInputOrderFollowingFirstItemDirection()
    {
        string[] docs =
        [
            """{"id":"A","age":27,"name":"n"}""", """{"id":"B","age":27,"name":"n"}""",
            """{"id":"C","age":27,"name":"n"}""", """{"id":"D","age":26,"name":"n"}""",
        ];
        Assert.Equal(T.J("['C','B','A','D']"), T.RunDocs(docs, "SELECT VALUE c.id FROM c ORDER BY c.age DESC"));
        Assert.Equal(T.J("['D','A','B','C']"), T.RunDocs(docs, "SELECT VALUE c.id FROM c ORDER BY c.age ASC"));
        Assert.Equal(T.J("['C','B','A','D']"), T.RunDocs(docs, "SELECT VALUE c.id FROM c ORDER BY c.age DESC, c.name ASC"));
        Assert.Equal(T.J("['D','A','B','C']"), T.RunDocs(docs, "SELECT VALUE c.id FROM c ORDER BY c.age ASC, c.name DESC"));
    }

    [Fact]
    public void OrderByStringsIsOrdinal()
    {
        string[] docs = ["""{"s":"b"}""", """{"s":"B"}""", """{"s":"a"}""", """{"s":"A"}""", """{"s":"ab"}"""];
        Assert.Equal(T.J("['A','B','a','ab','b']"), T.RunDocs(docs, "SELECT VALUE c.s FROM c ORDER BY c.s"));
    }

    // ------------------------------------------------------------------ DISTINCT / TOP / OFFSET LIMIT

    [Fact]
    public void DistinctValue() => Assert.Equal(T.J("['a','b','c']"), T.Run("SELECT DISTINCT VALUE c.category FROM c"));

    [Fact]
    public void DistinctObjectsIncludeEmptyObject() =>
        Assert.Equal(
            """[{"category":"a"},{"category":"b"},{"category":"c"},{}]""",
            T.Run("SELECT DISTINCT c.category FROM c"));

    [Fact]
    public void DistinctWithOrderBy() =>
        Assert.Equal(
            """[{},{"category":"a"},{"category":"b"},{"category":"c"}]""",
            T.Run("SELECT DISTINCT c.category FROM c ORDER BY c.category"));

    [Fact]
    public void DistinctUsesDeepEquality()
    {
        string[] docs = ["""{"o":{"a":1,"b":[1,2]}}""", """{"o":{"b":[1,2.0],"a":1.0}}""", """{"o":{"a":1,"b":[2,1]}}"""];
        Assert.Equal("""[{"a":1,"b":[1,2]},{"a":1,"b":[2,1]}]""", T.RunDocs(docs, "SELECT DISTINCT VALUE c.o FROM c"));
    }

    [Fact]
    public void DistinctDistinguishesTypes()
    {
        string[] docs = ["""{"v":1}""", """{"v":"1"}""", """{"v":true}""", """{"v":null}""", """{"v":1.0}""", """{"v":"1"}"""];
        Assert.Equal("""[1,"1",true,null]""", T.RunDocs(docs, "SELECT DISTINCT VALUE c.v FROM c"));
    }

    [Fact]
    public void Top() => Assert.Equal(T.J("['1','2','3']"), T.Run("SELECT TOP 3 VALUE c.id FROM c"));

    [Fact]
    public void TopZero() => Assert.Equal("[]", T.Run("SELECT TOP 0 * FROM c"));

    [Fact]
    public void TopWithParameter() => Assert.Equal(T.J("['1','2']"), T.Run("SELECT TOP @n VALUE c.id FROM c", """{"@n":2}"""));

    [Fact]
    public void TopWithOrderBy() => Assert.Equal(T.J("['10','9']"), T.Run("SELECT TOP 2 VALUE c.id FROM c ORDER BY c._ts DESC"));

    [Fact]
    public void OffsetLimit() => Assert.Equal(T.J("['3','4','5']"), T.Run("SELECT VALUE c.id FROM c OFFSET 2 LIMIT 3"));

    [Fact]
    public void OffsetLimitWithParameters() =>
        Assert.Equal(T.J("['9','10']"), T.Run("SELECT VALUE c.id FROM c OFFSET @o LIMIT @l", """{"@o":8,"@l":5}"""));

    [Fact]
    public void OffsetLimitAfterOrderBy() =>
        Assert.Equal(T.J("['8','7']"), T.Run("SELECT VALUE c.id FROM c ORDER BY c._ts DESC OFFSET 2 LIMIT 2"));

    [Fact]
    public void OffsetLimitAppliesAfterDistinct() =>
        Assert.Equal(T.J("['b','c']"), T.Run("SELECT DISTINCT VALUE c.category FROM c OFFSET 1 LIMIT 5"));

    [Fact]
    public void OffsetBeyondEnd() => Assert.Equal("[]", T.Run("SELECT VALUE c.id FROM c OFFSET 100 LIMIT 5"));

    [Fact]
    public void UndefinedResultsAreNotCountedOrYielded() =>
        // Values: Carol (3), undefined (4, skipped), null (5).
        Assert.Equal("[null]", T.Run("SELECT VALUE c.name FROM c WHERE c.id IN ('3', '4', '5') OFFSET 1 LIMIT 1"));

    [Fact]
    public void UndefinedTopLevelResultsAreSkipped() => Assert.Equal("[]", T.Run("SELECT VALUE c.missing FROM c"));

    [Theory]
    [InlineData("""{"@n":-1}""")]
    [InlineData("""{"@n":1.5}""")]
    [InlineData("""{"@n":"2"}""")]
    public void InvalidTopParameterThrows(string parameters) => T.Error("SELECT TOP @n * FROM c", parameters);

    // ------------------------------------------------------------------ subqueries

    [Fact]
    public void ExistsSubquery() =>
        Assert.Equal(T.J("['6']"), T.Run("SELECT VALUE c.id FROM c WHERE EXISTS(SELECT VALUE t FROM t IN c.tags WHERE t = 'z')"));

    [Fact]
    public void NotExistsSubquery() =>
        Assert.Equal(T.J("['2','3','5','7','8','10']"), T.Run("SELECT VALUE c.id FROM c WHERE NOT EXISTS(SELECT VALUE t FROM t IN c.tags WHERE t = 'x')"));

    [Fact]
    public void ExistsInSelect() =>
        Assert.Equal(
            """[{"id":"1","hasX":true},{"id":"2","hasX":false}]""",
            T.Run("SELECT c.id, EXISTS(SELECT VALUE t FROM t IN c.tags WHERE t = 'x') AS hasX FROM c WHERE c.id IN ('1', '2')"));

    [Fact]
    public void ArraySubquery() =>
        Assert.Equal(
            """[{"id":"6","skus":["a","b"]},{"id":"10","skus":["c"]}]""",
            T.Run("SELECT c.id, ARRAY(SELECT VALUE i.sku FROM i IN c.items) AS skus FROM c WHERE IS_DEFINED(c.items)"));

    [Fact]
    public void ArraySubqueryWithOrderBy() =>
        Assert.Equal(T.J("[['z','y','x']]"), T.Run("SELECT VALUE ARRAY(SELECT VALUE t FROM t IN c.tags ORDER BY t DESC) FROM c WHERE c.id = '6'"));

    [Fact]
    public void ArraySubqueryWithTop() =>
        Assert.Equal(T.J("[['z','x']]"), T.Run("SELECT VALUE ARRAY(SELECT TOP 2 VALUE t FROM t IN c.tags) FROM c WHERE c.id = '6'"));

    [Fact]
    public void ScalarSubqueryWithAggregateIsScopedToSubquery() =>
        Assert.Equal(
            """[{"id":"2","n":1},{"id":"5","n":0},{"id":"10","n":2}]""",
            T.Run("SELECT c.id, (SELECT VALUE COUNT(1) FROM t IN c.tags) AS n FROM c WHERE c.category = 'b'"));

    [Fact]
    public void ScalarSubqueryInWhere() =>
        Assert.Equal(T.J("['10']"), T.Run("SELECT VALUE c.id FROM c WHERE (SELECT VALUE COUNT(1) FROM t IN c.tags WHERE t = 'y') >= 2"));

    [Fact]
    public void ScalarSubqueryWithoutFrom() => Assert.Equal("1", T.Eval("(SELECT VALUE 1)"));

    [Fact]
    public void ScalarSubqueryReturningNothingIsUndefined() =>
        Assert.Equal("[{\"id\":\"3\"}]", T.Run("SELECT c.id, (SELECT VALUE t FROM t IN c.tags) AS first FROM c WHERE c.id = '3'"));

    [Fact]
    public void NestedCorrelatedSubqueries() =>
        Assert.Equal(
            T.J("['6']"),
            T.Run("SELECT VALUE c.id FROM c WHERE EXISTS(SELECT VALUE i FROM i IN c.items WHERE EXISTS(SELECT VALUE t FROM t IN c.tags WHERE t = i.sku || 'z' OR t = 'z' AND i.qty = 3))"));

    [Fact]
    public void SubqueryFromBindsOuterAlias() =>
        Assert.Equal(T.J("['1','4','6','9']"), T.Run("SELECT VALUE c.id FROM c WHERE EXISTS(SELECT VALUE t0 FROM c JOIN t0 IN c['tags'] WHERE (t0 = 'x'))"));

    [Fact]
    public void SubqueryFromUnknownIdentifierThrows() => T.Error("SELECT * FROM c WHERE EXISTS(SELECT VALUE 1 FROM x)");

    [Fact]
    public void FromSubqueryCountDistinct() =>
        Assert.Equal("[3]", T.Run("SELECT VALUE COUNT(1) FROM (SELECT DISTINCT VALUE c.category FROM c)"));

    [Fact]
    public void FromSubqueryWithAlias() =>
        Assert.Equal("""[{"n":"Carol"},{"n":"Eve"}]""", T.Run("SELECT t.n FROM (SELECT c.name AS n FROM c WHERE c.age > 30) t"));

    [Fact]
    public void SelectStarFromSubquery() =>
        Assert.Equal(T.J("['4','8']"), T.Run("SELECT * FROM (SELECT VALUE c.id FROM c WHERE c.category = 'c') AS t"));

    [Fact]
    public void FromSubqueryWithOuterOrderByAndJoin() =>
        Assert.Equal(
            T.J("['c','b','a']"),
            T.Run("SELECT VALUE i.sku FROM (SELECT VALUE c FROM c WHERE IS_DEFINED(c.items)) d JOIN i IN d.items ORDER BY i.sku DESC"));

    [Fact]
    public void CorrelatedAggregateSubqueryResetsItsAliasesPerOuterRow() =>
        // For doc 3 (no tags) the subquery has zero rows: t must be undefined, not left over from doc 1.
        Assert.Equal("""[["x",2],[0]]""", T.Run("SELECT VALUE (SELECT VALUE [t, COUNT(1)] FROM t IN c.tags) FROM c WHERE c.id IN ('1', '3')"));

    [Fact]
    public void GroupedSubqueryPerOuterRow() =>
        Assert.Equal(
            """[[{"t":"x","n":1},{"t":"y","n":1}],[],[{"t":"y","n":2}]]""",
            T.Run("SELECT VALUE ARRAY(SELECT t, COUNT(1) AS n FROM t IN c.tags GROUP BY t) FROM c WHERE c.id IN ('1', '3', '10')"));

    [Fact]
    public void SubqueryCorrelatedWithJoinAlias() =>
        Assert.Equal(
            """[["6","z"],["6","x"],["6","y"],["10","y"],["10","y"]]""",
            T.Run("SELECT VALUE [c.id, t] FROM c JOIN t IN c.tags WHERE EXISTS(SELECT VALUE 1 FROM i IN c.items WHERE i.sku < t)"));

    [Fact]
    public void OrderBySubqueryThenProperty() =>
        Assert.Equal(
            T.J("['6','1','10','2','4','9','3']"),
            T.Run("SELECT VALUE c.id FROM c WHERE IS_ARRAY(c.tags) ORDER BY (SELECT VALUE COUNT(1) FROM t IN c.tags) DESC, c.id"));

    [Fact]
    public void JoinOverConstructedArray() =>
        Assert.Equal("[1,2]", T.Run("SELECT VALUE x FROM c JOIN x IN [1, 2] WHERE c.id = '1'"));

    [Fact]
    public void OrderByAllUndefinedKeepsInputOrder() =>
        Assert.Equal(T.J("['1','2','3']"), T.Run("SELECT TOP 3 VALUE c.id FROM c ORDER BY c.missing"));

    // ------------------------------------------------------------------ re-execution

    [Fact]
    public void QueryCanBeExecutedRepeatedly()
    {
        var query = SqlQuery.Parse("SELECT VALUE c.id FROM c WHERE c.age > @a ORDER BY c.id");
        var first = T.Format(query.Execute(T.Fixture, T.Parameters("""{"@a":30}""")));
        var second = T.Format(query.Execute(T.Fixture, T.Parameters("""{"@a":40}""")));
        var results = query.Execute(T.Fixture, T.Parameters("""{"@a":30}"""));
        Assert.Equal(T.J("['3','7']"), first);
        Assert.Equal(T.J("['7']"), second);
        Assert.Equal(T.Format(results), T.Format(results));
    }
}
