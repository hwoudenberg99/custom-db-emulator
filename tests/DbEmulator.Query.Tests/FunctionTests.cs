using System.Globalization;
using System.Text.RegularExpressions;

namespace DbEmulator.Query.Tests;

public class FunctionTests
{
    [Theory]
    [InlineData("IS_ARRAY([1])", "true")]
    [InlineData("IS_ARRAY({})", "false")]
    [InlineData("IS_BOOL(false)", "true")]
    [InlineData("IS_BOOL(0)", "false")]
    [InlineData("IS_DEFINED(undefined)", "false")]
    [InlineData("IS_DEFINED(null)", "true")]
    [InlineData("IS_NULL(null)", "true")]
    [InlineData("IS_NULL(undefined)", "false")]
    [InlineData("IS_NUMBER(1.5)", "true")]
    [InlineData("IS_NUMBER('1')", "false")]
    [InlineData("IS_OBJECT({'a':1})", "true")]
    [InlineData("IS_OBJECT([])", "false")]
    [InlineData("IS_PRIMITIVE('x')", "true")]
    [InlineData("IS_PRIMITIVE(null)", "true")]
    [InlineData("IS_PRIMITIVE([])", "false")]
    [InlineData("IS_PRIMITIVE(undefined)", "false")]
    [InlineData("IS_STRING('x')", "true")]
    [InlineData("is_string('x')", "true")]
    [InlineData("IS_FINITE_NUMBER(1)", "true")]
    [InlineData("IS_FINITE_NUMBER('1')", "false")]
    [InlineData("IS_INTEGER(5)", "true")]
    [InlineData("IS_INTEGER(5.5)", "false")]
    [InlineData("IS_INTEGER('5')", "false")]
    [InlineData("IIF(true, 'a', 'b')", "\"a\"")]
    [InlineData("IIF(1, 'a', 'b')", "\"b\"")]
    [InlineData("IIF(undefined, 'a', 'b')", "\"b\"")]
    [InlineData("IIF(false, 'a', undefined)", "undefined")]
    public void TypeChecksAndConditionals(string expression, string expected) => Assert.Equal(expected, T.Eval(expression));

    [Theory]
    [InlineData("CONCAT('a', 'b', 'c')", "\"abc\"")]
    [InlineData("CONCAT('a', 1)", "undefined")]
    [InlineData("CONTAINS('Hello', 'ell')", "true")]
    [InlineData("CONTAINS('Hello', 'ELL')", "false")]
    [InlineData("CONTAINS('Hello', 'ELL', true)", "true")]
    [InlineData("CONTAINS('Hello', 'ELL', 1)", "undefined")]
    [InlineData("CONTAINS(1, 'a')", "undefined")]
    [InlineData("STARTSWITH('Hello', 'he')", "false")]
    [InlineData("STARTSWITH('Hello', 'he', true)", "true")]
    [InlineData("ENDSWITH('Hello', 'llo')", "true")]
    [InlineData("ENDSWITH('Hello', 'LLO', false)", "false")]
    [InlineData("STRINGEQUALS('abc', 'ABC')", "false")]
    [InlineData("STRINGEQUALS('abc', 'ABC', true)", "true")]
    [InlineData("INDEX_OF('abcabc', 'c')", "2")]
    [InlineData("INDEX_OF('abcabc', 'c', 3)", "5")]
    [InlineData("INDEX_OF('abc', 'z')", "-1")]
    [InlineData("INDEX_OF('abc', 'a', 10)", "-1")]
    [InlineData("LEFT('abcdef', 2)", "\"ab\"")]
    [InlineData("LEFT('ab', 5)", "\"ab\"")]
    [InlineData("RIGHT('abcdef', 2)", "\"ef\"")]
    [InlineData("RIGHT('ab', 5)", "\"ab\"")]
    [InlineData("LENGTH('abc')", "3")]
    [InlineData("LENGTH('')", "0")]
    [InlineData("LENGTH(1)", "undefined")]
    [InlineData("LOWER('AbC')", "\"abc\"")]
    [InlineData("UPPER('AbC')", "\"ABC\"")]
    [InlineData("LTRIM('  a ')", "\"a \"")]
    [InlineData("RTRIM('  a ')", "\"  a\"")]
    [InlineData("TRIM('  a ')", "\"a\"")]
    [InlineData("REPLACE('a-b-c', '-', '+')", "\"a+b+c\"")]
    [InlineData("REPLACE('abc', '', 'x')", "\"abc\"")]
    [InlineData("REPLICATE('ab', 3)", "\"ababab\"")]
    [InlineData("REPLICATE('a', 0)", "\"\"")]
    [InlineData("REPLICATE('a', -1)", "undefined")]
    [InlineData("REPLICATE('a', 10001)", "undefined")]
    [InlineData("LENGTH(REPLICATE('a', 10000))", "10000")]
    [InlineData("REVERSE('abc')", "\"cba\"")]
    [InlineData("SUBSTRING('abcdef', 1, 3)", "\"bcd\"")]
    [InlineData("SUBSTRING('abc', 1, 10)", "\"bc\"")]
    [InlineData("SUBSTRING('abc', 5, 1)", "\"\"")]
    [InlineData("SUBSTRING('abc', 0, 0)", "\"\"")]
    [InlineData("TOSTRING(1)", "\"1\"")]
    [InlineData("TOSTRING(1.5)", "\"1.5\"")]
    [InlineData("TOSTRING(0.1 + 0.2)", "\"0.30000000000000004\"")]
    [InlineData("TOSTRING(true)", "\"true\"")]
    [InlineData("TOSTRING(null)", "\"null\"")]
    [InlineData("TOSTRING('s')", "\"s\"")]
    [InlineData("TOSTRING([1,'a'])", "\"[1,\\\"a\\\"]\"")]
    [InlineData("TOSTRING({'a':1})", "\"{\\\"a\\\":1}\"")]
    [InlineData("TOSTRING(undefined)", "undefined")]
    [InlineData("REGEXMATCH('abc', '^a.c$')", "true")]
    [InlineData("REGEXMATCH('ABC', '^abc$')", "false")]
    [InlineData("REGEXMATCH('ABC', '^abc$', 'i')", "true")]
    [InlineData("REGEXMATCH('a\\nb', '^b$', 'm')", "true")]
    [InlineData("REGEXMATCH('abc', '[')", "undefined")]
    [InlineData("REGEXMATCH('abc', 'a', 'q')", "undefined")]
    [InlineData("STRINGTONUMBER('1.5')", "1.5")]
    [InlineData("STRINGTONUMBER(' 42 ')", "42")]
    [InlineData("STRINGTONUMBER('-1e3')", "-1000")]
    [InlineData("STRINGTONUMBER('abc')", "undefined")]
    [InlineData("STRINGTONUMBER('0x1')", "undefined")]
    [InlineData("STRINGTONUMBER('01')", "undefined")]
    [InlineData("STRINGTOBOOLEAN('true')", "true")]
    [InlineData("STRINGTOBOOLEAN(' false ')", "false")]
    [InlineData("STRINGTOBOOLEAN('TRUE')", "undefined")]
    [InlineData("STRINGTONULL('null')", "null")]
    [InlineData("STRINGTONULL('x')", "undefined")]
    [InlineData("STRINGTOARRAY('[1,\"a\",{\"b\":2}]')", "[1,\"a\",{\"b\":2}]")]
    [InlineData("STRINGTOARRAY('[1,')", "undefined")]
    [InlineData("STRINGTOARRAY('{}')", "undefined")]
    [InlineData("STRINGTOOBJECT('{\"a\":[1,2]}')", "{\"a\":[1,2]}")]
    [InlineData("STRINGTOOBJECT(\"{'a':1}\")", "undefined")]
    [InlineData("STRINGTOOBJECT('[]')", "undefined")]
    [InlineData("STRINGJOIN(['a','b','c'], '-')", "\"a-b-c\"")]
    [InlineData("STRINGJOIN(['a', 1], '-')", "undefined")]
    [InlineData("STRINGSPLIT('a,b,,c', ',')", "[\"a\",\"b\",\"\",\"c\"]")]
    [InlineData("STRINGSPLIT('abc', ',')", "[\"abc\"]")]
    public void StringFunctions(string expression, string expected) => Assert.Equal(expected, T.Eval(expression));

    [Theory]
    [InlineData("ARRAY_CONCAT([1,2], [3], [])", "[1,2,3]")]
    [InlineData("ARRAY_CONCAT([1], 'a')", "undefined")]
    [InlineData("ARRAY_CONTAINS([1,2,3], 2)", "true")]
    [InlineData("ARRAY_CONTAINS([1,2,3], '2')", "false")]
    [InlineData("ARRAY_CONTAINS([[1],[2]], [2])", "true")]
    [InlineData("ARRAY_CONTAINS([{'a':1,'b':2}], {'a':1})", "false")]
    [InlineData("ARRAY_CONTAINS([{'a':1,'b':2}], {'a':1}, true)", "true")]
    [InlineData("ARRAY_CONTAINS([{'a':1,'b':2}], {'a':2}, true)", "false")]
    [InlineData("ARRAY_CONTAINS([{'a':1,'b':2}], {'b':2,'a':1})", "true")]
    [InlineData("ARRAY_CONTAINS('abc', 'a')", "undefined")]
    [InlineData("ARRAY_CONTAINS_ANY([1,2,3], 4, 3)", "true")]
    [InlineData("ARRAY_CONTAINS_ANY([1,2,3], 4, 5)", "false")]
    [InlineData("ARRAY_CONTAINS_ALL([1,2,3], 1, 3)", "true")]
    [InlineData("ARRAY_CONTAINS_ALL([1,2,3], 1, 4)", "false")]
    [InlineData("ARRAY_LENGTH([1,2,3])", "3")]
    [InlineData("ARRAY_LENGTH([])", "0")]
    [InlineData("ARRAY_LENGTH('abc')", "undefined")]
    [InlineData("ARRAY_SLICE(['a','b','c','d'], 1, 2)", "[\"b\",\"c\"]")]
    [InlineData("ARRAY_SLICE(['a','b','c'], -2, 1)", "[\"b\"]")]
    [InlineData("ARRAY_SLICE(['a','b','c'], -2, 2)", "[\"b\",\"c\"]")]
    [InlineData("ARRAY_SLICE(['a','b','c'], 1)", "[\"b\",\"c\"]")]
    [InlineData("ARRAY_SLICE(['a','b','c'], 1, 0)", "[]")]
    [InlineData("ARRAY_SLICE(['a','b','c'], 1, -100)", "[]")]
    [InlineData("ARRAY_SLICE([1,2,3], 1, 1000)", "[2,3]")]
    [InlineData("ARRAY_SLICE([1,2,3], -10)", "[1,2,3]")]
    [InlineData("ARRAY_SLICE([1,2,3], 5)", "[]")]
    [InlineData("SETINTERSECT([1,2,2,3], [2,3,4])", "[2,3]")]
    [InlineData("SETINTERSECT([1], 'a')", "undefined")]
    [InlineData("SETUNION([1,2,2], [2,3])", "[1,2,3]")]
    [InlineData("OBJECTTOARRAY({'a':1,'b':[2]})", "[{\"k\":\"a\",\"v\":1},{\"k\":\"b\",\"v\":[2]}]")]
    [InlineData("OBJECTTOARRAY({'a':1}, 'name', 'value')", "[{\"name\":\"a\",\"value\":1}]")]
    [InlineData("OBJECTTOARRAY([1])", "undefined")]
    [InlineData("CHOOSE(2, 'a', 'b', 'c')", "\"b\"")]
    [InlineData("CHOOSE(4, 'a', 'b', 'c')", "undefined")]
    [InlineData("CHOOSE(0, 'a')", "undefined")]
    public void ArrayFunctions(string expression, string expected) => Assert.Equal(expected, T.Eval(expression));

    [Fact]
    public void ArrayFunctionsOnDocuments() =>
        Assert.Equal(
            """[{"id":"6","n":3,"hasX":true,"partial":true,"slice":["x","y"]}]""",
            T.Run("SELECT c.id, ARRAY_LENGTH(c.tags) AS n, ARRAY_CONTAINS(c.tags, 'x') AS hasX, ARRAY_CONTAINS(c.items, {'sku': 'b'}, true) AS partial, ARRAY_SLICE(c.tags, 1) AS slice FROM c WHERE c.id = '6'"));

    [Theory]
    [InlineData("ABS(-5)", "5")]
    [InlineData("ABS('a')", "undefined")]
    [InlineData("CEILING(1.2)", "2")]
    [InlineData("FLOOR(-1.2)", "-2")]
    [InlineData("ROUND(2.5)", "3")]
    [InlineData("ROUND(-2.5)", "-3")]
    [InlineData("ROUND(2.4)", "2")]
    [InlineData("TRUNC(-2.7)", "-2")]
    [InlineData("SIGN(-3)", "-1")]
    [InlineData("SIGN(0)", "0")]
    [InlineData("SQRT(16)", "4")]
    [InlineData("SQRT(-1)", "undefined")]
    [InlineData("SQUARE(3)", "9")]
    [InlineData("POWER(2, 10)", "1024")]
    [InlineData("POWER(2, -1)", "0.5")]
    [InlineData("EXP(0)", "1")]
    [InlineData("LOG(1)", "0")]
    [InlineData("LOG(8, 2)", "3")]
    [InlineData("LOG(0)", "undefined")]
    [InlineData("LOG10(1000)", "3")]
    [InlineData("PI()", "3.141592653589793")]
    [InlineData("DEGREES(PI())", "180")]
    [InlineData("RADIANS(180)", "3.141592653589793")]
    [InlineData("SIN(0)", "0")]
    [InlineData("COS(0)", "1")]
    [InlineData("TAN(0)", "0")]
    [InlineData("COT(0)", "undefined")]
    [InlineData("ACOS(1)", "0")]
    [InlineData("ASIN(0)", "0")]
    [InlineData("ATAN(0)", "0")]
    [InlineData("ATN2(1, 0)", "0")]
    [InlineData("ATN2(0, 1)", "1.5707963267948966")]
    [InlineData("NUMBERBIN(7, 5)", "5")]
    [InlineData("NUMBERBIN(-7, 5)", "-10")]
    [InlineData("NUMBERBIN(7.8)", "7")]
    [InlineData("NUMBERBIN(7, 0)", "undefined")]
    [InlineData("INTADD(2, 3)", "5")]
    [InlineData("INTADD(1.5, 1)", "undefined")]
    [InlineData("INTSUB(2, 3)", "-1")]
    [InlineData("INTMUL(4, 5)", "20")]
    [InlineData("INTDIV(7, 2)", "3")]
    [InlineData("INTDIV(-7, 2)", "-3")]
    [InlineData("INTDIV(1, 0)", "undefined")]
    [InlineData("INTMOD(7, 3)", "1")]
    [InlineData("INTMOD(7, 0)", "undefined")]
    [InlineData("INTBITAND(6, 3)", "2")]
    [InlineData("INTBITOR(6, 3)", "7")]
    [InlineData("INTBITXOR(6, 3)", "5")]
    [InlineData("INTBITNOT(5)", "-6")]
    [InlineData("INTBITLEFTSHIFT(1, 3)", "8")]
    [InlineData("INTBITRIGHTSHIFT(16, 2)", "4")]
    public void MathFunctions(string expression, string expected) => Assert.Equal(expected, T.Eval(expression));

    [Fact]
    public void RandReturnsNumberInUnitInterval()
    {
        for (int i = 0; i < 20; i++)
        {
            double d = double.Parse(T.Eval("RAND()"), CultureInfo.InvariantCulture);
            Assert.InRange(d, 0, 0.9999999999999999);
        }
    }

    [Theory]
    [InlineData("DATETIMEADD('dd', 1, '2020-07-09T23:20:13.4575530Z')", "\"2020-07-10T23:20:13.4575530Z\"")]
    [InlineData("DATETIMEADD('mm', 1, '2020-01-31T00:00:00.0000000Z')", "\"2020-02-29T00:00:00.0000000Z\"")]
    [InlineData("DATETIMEADD('yyyy', -1, '2020-02-29T00:00:00Z')", "\"2019-02-28T00:00:00.0000000Z\"")]
    [InlineData("DATETIMEADD('hh', -1, '2020-01-01T00:00:00Z')", "\"2019-12-31T23:00:00.0000000Z\"")]
    [InlineData("DATETIMEADD('mi', 90, '2020-01-01T00:00:00Z')", "\"2020-01-01T01:30:00.0000000Z\"")]
    [InlineData("DATETIMEADD('ss', 1, '2020-01-01T00:00:59Z')", "\"2020-01-01T00:01:00.0000000Z\"")]
    [InlineData("DATETIMEADD('ms', 1, '2020-01-01T00:00:00.123Z')", "\"2020-01-01T00:00:00.1240000Z\"")]
    [InlineData("DATETIMEADD('mcs', 1, '2020-01-01T00:00:00Z')", "\"2020-01-01T00:00:00.0000010Z\"")]
    [InlineData("DATETIMEADD('ns', 100, '2020-01-01T00:00:00Z')", "\"2020-01-01T00:00:00.0000001Z\"")]
    [InlineData("DATETIMEADD('hh', 0, '2020-01-01T10:00:00+02:00')", "\"2020-01-01T08:00:00.0000000Z\"")]
    [InlineData("DATETIMEADD('yyyy', 1, 'not a date')", "undefined")]
    [InlineData("DATETIMEADD('fortnight', 1, '2020-01-01T00:00:00Z')", "undefined")]
    [InlineData("DATETIMEADD('yyyy', 1, '9999-06-01T00:00:00Z')", "undefined")]
    [InlineData("DATETIMEDIFF('yyyy', '2019-12-31T23:59:59Z', '2020-01-01T00:00:00Z')", "1")]
    [InlineData("DATETIMEDIFF('month', '2020-03-01T00:00:00Z', '2020-01-15T00:00:00Z')", "-2")]
    [InlineData("DATETIMEDIFF('dd', '2020-01-01T23:00:00Z', '2020-01-02T01:00:00Z')", "1")]
    [InlineData("DATETIMEDIFF('hh', '2020-01-01T00:59:00Z', '2020-01-01T01:00:00Z')", "1")]
    [InlineData("DATETIMEDIFF('mi', '2020-01-01T00:00:00Z', '2020-01-01T01:30:00Z')", "90")]
    [InlineData("DATETIMEDIFF('ss', '2020-01-01T00:00:00.9Z', '2020-01-01T00:00:01.1Z')", "1")]
    [InlineData("DATETIMEDIFF('ms', '2020-01-01T00:00:00Z', '2020-01-01T00:00:01Z')", "1000")]
    [InlineData("DATETIMEPART('year', '2016-05-29T08:30:00.1301617Z')", "2016")]
    [InlineData("DATETIMEPART('mm', '2016-05-29T08:30:00.1301617Z')", "5")]
    [InlineData("DATETIMEPART('dd', '2016-05-29T08:30:00.1301617Z')", "29")]
    [InlineData("DATETIMEPART('hh', '2016-05-29T08:30:00.1301617Z')", "8")]
    [InlineData("DATETIMEPART('n', '2016-05-29T08:30:00.1301617Z')", "30")]
    [InlineData("DATETIMEPART('s', '2016-05-29T08:30:00.1301617Z')", "0")]
    [InlineData("DATETIMEPART('ms', '2016-05-29T08:30:00.1301617Z')", "130")]
    [InlineData("DATETIMEPART('mcs', '2016-05-29T08:30:00.1301617Z')", "130161")]
    [InlineData("DATETIMEPART('ns', '2016-05-29T08:30:00.1301617Z')", "130161700")]
    [InlineData("DATETIMEPART('year', 'bad')", "undefined")]
    [InlineData("DATETIMEFROMPARTS(2020, 9, 4)", "\"2020-09-04T00:00:00.0000000Z\"")]
    [InlineData("DATETIMEFROMPARTS(2020, 9, 4, 10, 30, 15, 1234567)", "\"2020-09-04T10:30:15.1234567Z\"")]
    [InlineData("DATETIMEFROMPARTS(2020, 2, 30)", "undefined")]
    [InlineData("DATETIMEFROMPARTS(2020, 13, 1)", "undefined")]
    [InlineData("DATETIMETOTICKS('1970-01-01T00:00:01Z')", "10000000")]
    [InlineData("DATETIMETOTIMESTAMP('1970-01-01T00:00:01.5Z')", "1500")]
    [InlineData("DATETIMETOTIMESTAMP('2020-01-01T00:00:00.0000000Z')", "1577836800000")]
    [InlineData("TICKSTODATETIME(10000000)", "\"1970-01-01T00:00:01.0000000Z\"")]
    [InlineData("TIMESTAMPTODATETIME(1500)", "\"1970-01-01T00:00:01.5000000Z\"")]
    [InlineData("TIMESTAMPTODATETIME(1577836800000)", "\"2020-01-01T00:00:00.0000000Z\"")]
    [InlineData("TIMESTAMPTODATETIME('x')", "undefined")]
    [InlineData("DATETIMEBIN('2021-06-28T17:24:29.2991234Z', 'hh')", "\"2021-06-28T17:00:00.0000000Z\"")]
    [InlineData("DATETIMEBIN('2021-06-28T17:24:29.2991234Z', 'mi', 15)", "\"2021-06-28T17:15:00.0000000Z\"")]
    [InlineData("DATETIMEBIN('2021-06-28T17:24:29.2991234Z', 'dd', 7)", "\"2021-06-24T00:00:00.0000000Z\"")]
    [InlineData("DATETIMEBIN('2021-06-28T17:24:29.2991234Z', 'dd', 1, '2021-01-01T06:00:00Z')", "\"2021-06-28T06:00:00.0000000Z\"")]
    [InlineData("DATETIMEBIN('2021-06-28T17:24:29Z', 'yyyy')", "\"2021-01-01T00:00:00.0000000Z\"")]
    [InlineData("DATETIMEBIN('2021-06-28T17:24:29Z', 'mm', 2)", "\"2021-05-01T00:00:00.0000000Z\"")]
    [InlineData("DATETIMEBIN('2021-06-28T17:24:29Z', 'hh', 0)", "undefined")]
    public void DateTimeFunctions(string expression, string expected) => Assert.Equal(expected, T.Eval(expression));

    [Fact]
    public void CurrentDateTimeFunctionsAreFixedPerExecution()
    {
        var result = T.Eval("[GETCURRENTDATETIME(), GETCURRENTDATETIMESTATIC(), GETCURRENTTIMESTAMP(), GETCURRENTTIMESTAMPSTATIC(), GETCURRENTTICKS(), GETCURRENTTICKSSTATIC()]");
        var parts = result.Trim('[', ']').Split(',');
        Assert.Equal(6, parts.Length);
        Assert.Equal(parts[0], parts[1]);
        Assert.Equal(parts[2], parts[3]);
        Assert.Equal(parts[4], parts[5]);
        Assert.Matches(new Regex("^\"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{7}Z\"$"), parts[0]);

        long timestamp = long.Parse(parts[2], CultureInfo.InvariantCulture);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange(timestamp, now - 60_000, now + 1_000);
        long ticks = long.Parse(parts[4], CultureInfo.InvariantCulture);
        Assert.Equal(timestamp, ticks / 10_000);
    }

    [Fact]
    public void CurrentDateTimeIsTheSameForAllRows() =>
        Assert.Equal("[1]", T.Run("SELECT VALUE COUNT(1) FROM (SELECT DISTINCT VALUE GETCURRENTTICKS() FROM c)"));

    [Theory]
    [InlineData("VECTORDISTANCE([1,0], [0,1])", "0")]
    [InlineData("VECTORDISTANCE([1,0], [2,0])", "1")]
    [InlineData("VECTORDISTANCE([1,2], [3,4], false, {'distanceFunction': 'dotproduct'})", "11")]
    [InlineData("VECTORDISTANCE([0,0], [3,4], false, {'distanceFunction': 'euclidean'})", "5")]
    [InlineData("VECTORDISTANCE([0,0], [3,4], true, {'distanceFunction': 'Euclidean'})", "5")]
    [InlineData("VECTORDISTANCE([1,2], [1])", "undefined")]
    [InlineData("VECTORDISTANCE([1], 'a')", "undefined")]
    [InlineData("VECTORDISTANCE([1, 'a'], [1, 2])", "undefined")]
    [InlineData("FULLTEXTCONTAINS('The Quick Fox', 'quick')", "true")]
    [InlineData("FULLTEXTCONTAINS('The Quick Fox', 'dog')", "false")]
    [InlineData("FULLTEXTCONTAINSALL('The Quick Fox', 'quick', 'fox')", "true")]
    [InlineData("FULLTEXTCONTAINSALL('The Quick Fox', 'quick', 'dog')", "false")]
    [InlineData("FULLTEXTCONTAINSANY('The Quick Fox', 'dog', 'fox')", "true")]
    [InlineData("FULLTEXTCONTAINSANY('The Quick Fox', 'dog', 'cat')", "false")]
    public void SearchFunctions(string expression, string expected) => Assert.Equal(expected, T.Eval(expression));

    [Fact]
    public void VectorDistanceOrdering()
    {
        string[] docs = ["""{"id":"far","v":[0,1]}""", """{"id":"near","v":[1,0.1]}""", """{"id":"mid","v":[1,1]}"""];
        Assert.Equal(
            T.J("['near','mid','far']"),
            T.RunDocs(docs, "SELECT VALUE c.id FROM c ORDER BY VECTORDISTANCE(c.v, [1, 0]) DESC"));
    }

    [Theory]
    [InlineData("SELECT VALUE ST_DISTANCE(c.a, c.b) FROM c")]
    [InlineData("SELECT VALUE ST_WITHIN(c.a, c.b) FROM c")]
    [InlineData("SELECT VALUE FULLTEXTSCORE(c.text, 'a') FROM c")]
    [InlineData("SELECT * FROM c ORDER BY RANK FullTextScore(c.text, 'a')")]
    public void UnsupportedFunctionsThrowClearError(string sql)
    {
        var ex = Assert.Throws<SqlQueryException>(() => SqlQuery.Parse(sql));
        Assert.Contains("not supported by DbEmulator", ex.Message);
    }

    [Fact]
    public void UdfParsesButThrowsWhenEvaluated()
    {
        var query = SqlQuery.Parse("SELECT VALUE udf.tax(c.price) FROM c");
        Assert.Empty(query.Execute([]));
        var ex = Assert.Throws<SqlQueryException>(() => query.Execute(T.Fixture).ToList());
        Assert.Contains("User-defined functions are not supported by DbEmulator", ex.Message);
    }

    [Fact]
    public void FunctionNamesAreCaseInsensitive() =>
        Assert.Equal("[\"ALICE\"]", T.Run("SELECT VALUE upper(c.name) FROM c WHERE StartsWith(c.name, 'Al')"));

    [Fact]
    public void FunctionArgumentTypeMismatchYieldsUndefined() =>
        Assert.Equal("[{\"id\":\"4\"}]", T.Run("SELECT c.id, UPPER(c._ts) AS a, ABS(c.age) AS b FROM c WHERE c.id = '4'"));
}
