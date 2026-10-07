using System.Text.Json;

namespace DbEmulator.Query.Tests;

/// <summary>Fixture documents and helpers. Results are compared as compact JSON arrays.</summary>
internal static class T
{
    // id | name    | age   | status   | category | tags          | price | items
    // 1  | Alice   | 30    | active   | a        | [x,y]         | 1.10  |
    // 2  | Bob     | 25    | inactive | b        | [y]           | 2.5   |
    // 3  | Carol   | 35    | active   | a        | []            | 3     |
    // 4  | -       | "40"  | active   | c        | [x]           |       |
    // 5  | null    | null  | pending  | b        | null          |       |
    // 6  | dave    | 28.5  | active   | a        | [z,x,y]       |       | [{a,2},{b,3}]
    // 7  | Eve     | 45    | inactive | -        | "notarray"    |       |
    // 8  | Frank   | true  | active   | c        | -             |       |            (mixed array)
    // 9  | Grace   | 30    | ACTIVE   | a        | [x]           | 10    |
    // 10 | Heidi   | 22    | active   | b        | [y,y]         |       | [{c,1}]
    public static readonly string[] FixtureJson =
    [
        """{"id":"1","name":"Alice","age":30,"status":"active","category":"a","tags":["x","y"],"address":{"city":"Seattle","zip":"98101"},"price":1.10,"big":12345678901234567890,"first name":"Al","_rid":"r1","_ts":100}""",
        """{"id":"2","name":"Bob","age":25,"status":"inactive","category":"b","tags":["y"],"address":{"city":"Portland"},"price":2.5,"_rid":"r2","_ts":200}""",
        """{"id":"3","name":"Carol","age":35,"status":"active","category":"a","tags":[],"address":{"city":"Seattle"},"price":3,"_rid":"r3","_ts":300}""",
        """{"id":"4","age":"40","status":"active","category":"c","tags":["x"],"_rid":"r4","_ts":400}""",
        """{"id":"5","name":null,"age":null,"status":"pending","category":"b","tags":null,"_rid":"r5","_ts":500}""",
        """{"id":"6","name":"dave","age":28.5,"status":"active","category":"a","tags":["z","x","y"],"items":[{"sku":"a","qty":2},{"sku":"b","qty":3}],"_rid":"r6","_ts":600}""",
        """{"id":"7","name":"Eve","age":45,"status":"inactive","tags":"notarray","_rid":"r7","_ts":700}""",
        """{"id":"8","name":"Frank","age":true,"status":"active","category":"c","mixed":[1,"a",null,true,{"k":1},[1]],"_rid":"r8","_ts":800}""",
        """{"id":"9","name":"Grace","age":30,"status":"ACTIVE","category":"a","tags":["x"],"price":10,"_rid":"r9","_ts":900}""",
        """{"id":"10","name":"Heidi","age":22,"status":"active","category":"b","tags":["y","y"],"items":[{"sku":"c","qty":1}],"_rid":"r10","_ts":1000}""",
    ];

    public static readonly JsonElement[] Fixture = FixtureJson.Select(ParseJson).ToArray();

    public static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Converts single quotes to double quotes, to write expected JSON compactly.</summary>
    public static string J(string s) => s.Replace('\'', '"');

    /// <summary>Runs a query over the fixture documents.</summary>
    public static string Run(string sql, string? parametersJson = null) => RunOn(Fixture, sql, parametersJson);

    public static string RunOn(IEnumerable<JsonElement> documents, string sql, string? parametersJson = null) =>
        Format(SqlQuery.Parse(sql).Execute(documents, Parameters(parametersJson)));

    public static string RunDocs(string[] documentsJson, string sql, string? parametersJson = null) =>
        RunOn(documentsJson.Select(ParseJson).ToArray(), sql, parametersJson);

    public static string Format(IEnumerable<QValue> values) => "[" + string.Join(",", values.Select(v => v.ToJsonString())) + "]";

    /// <summary>Evaluates <c>SELECT VALUE expr</c> (no FROM); returns the compact JSON or "undefined".</summary>
    public static string Eval(string expression, string? parametersJson = null)
    {
        var results = SqlQuery.Parse("SELECT VALUE " + expression).Execute([], Parameters(parametersJson)).ToList();
        Assert.True(results.Count <= 1);
        return results.Count == 0 ? "undefined" : results[0].ToJsonString();
    }

    public static IReadOnlyDictionary<string, JsonElement>? Parameters(string? json) =>
        json is null ? null : ParseJson(json).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    /// <summary>Asserts that parsing or executing (over the fixture) throws a <see cref="SqlQueryException"/>.</summary>
    public static SqlQueryException Error(string sql, string? parametersJson = null) =>
        Assert.Throws<SqlQueryException>(() => SqlQuery.Parse(sql).Execute(Fixture, Parameters(parametersJson)).ToList());
}
