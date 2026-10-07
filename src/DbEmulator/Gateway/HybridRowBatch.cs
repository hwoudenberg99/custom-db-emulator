using System.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Serialization.HybridRow;
using Microsoft.Azure.Cosmos.Serialization.HybridRow.IO;
using Microsoft.Azure.Cosmos.Serialization.HybridRow.Layouts;
using Microsoft.Azure.Cosmos.Serialization.HybridRow.RecordIO;

namespace DbEmulator.Gateway;

internal sealed record BatchOperationRequest(
    int OperationType,
    string? PartitionKeyJson,
    string? Id,
    byte[]? ResourceBody,
    string? IfMatch,
    string? IfNoneMatch,
    bool MinimalReturn);

internal sealed record BatchOperationResult(int StatusCode, int SubStatusCode = 0, string? ETag = null, byte[]? ResourceBody = null, double RequestCharge = 0)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>
/// The .NET SDK sends transactional batches and bulk requests as RecordIO streams of HybridRow records (also in
/// Gateway mode). This codec uses the batch schema embedded in the Cosmos SDK itself, so the wire format always
/// matches the SDK version the emulator ships with.
/// </summary>
internal static class HybridRowBatch
{
    public const int Create = 0;
    public const int Patch = 1;
    public const int Read = 2;
    public const int Delete = 4;
    public const int Replace = 5;
    public const int Upsert = 20;

    private static readonly Lazy<(LayoutResolver Resolver, Layout ResultLayout)> Schema = new(() =>
    {
        var provider = typeof(CosmosClient).Assembly.GetType("Microsoft.Azure.Cosmos.BatchSchemaProvider", throwOnError: true)!;
        object Get(string name) => provider.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
        return ((LayoutResolver)Get("BatchLayoutResolver"), (Layout)Get("BatchResultLayout"));
    });

    public static async Task<List<BatchOperationRequest>> ReadAsync(byte[] body)
    {
        var operations = new List<BatchOperationRequest>();
        using var stream = new MemoryStream(body, writable: false);
        var result = await stream.ReadRecordIOAsync(
            (ReadOnlyMemory<byte> record) => ReadOperation(record, operations),
            (ReadOnlyMemory<byte> segment) => Result.Success,
            new MemorySpanResizer<byte>(Math.Max(1024, body.Length)));
        if (result != Result.Success)
        {
            throw GatewayException.BadRequest($"The batch request could not be read ({result}).");
        }

        return operations;
    }

    public static async Task<byte[]> WriteAsync(IReadOnlyList<BatchOperationResult> results)
    {
        var records = results.Select(SerializeResult).ToList();
        using var stream = new MemoryStream();
        var result = await stream.WriteRecordIOAsync(default, (long index, out ReadOnlyMemory<byte> buffer) =>
        {
            buffer = index < records.Count ? records[(int)index] : ReadOnlyMemory<byte>.Empty;
            return Result.Success;
        });
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"Writing the batch response failed ({result}).");
        }

        return stream.ToArray();
    }

    private static Result ReadOperation(ReadOnlyMemory<byte> record, List<BatchOperationRequest> operations)
    {
        var reader = new RowReader(record, HybridRowVersion.V1, Schema.Value.Resolver);
        int operationType = -1;
        string? partitionKey = null, id = null, ifMatch = null, ifNoneMatch = null;
        byte[]? resourceBody = null;
        var minimal = false;
        while (reader.Read())
        {
            var r = reader.Path.ToString() switch
            {
                "operationType" => reader.ReadInt32(out operationType),
                "partitionKey" => reader.ReadString(out partitionKey),
                "id" => reader.ReadString(out id),
                "resourceBody" => reader.ReadBinary(out resourceBody),
                "ifMatch" => reader.ReadString(out ifMatch),
                "ifNoneMatch" => reader.ReadString(out ifNoneMatch),
                "minimalReturnPreference" => reader.ReadBool(out minimal),
                _ => Result.Success, // resourceType, effectivePartitionKey, binaryId, indexingDirective, timeToLiveInSeconds
            };
            if (r != Result.Success)
            {
                return r;
            }
        }

        operations.Add(new BatchOperationRequest(operationType, partitionKey, id, resourceBody, ifMatch, ifNoneMatch, minimal));
        return Result.Success;
    }

    private static ReadOnlyMemory<byte> SerializeResult(BatchOperationResult result)
    {
        var resizer = new MemorySpanResizer<byte>(Math.Max(256, (result.ResourceBody?.Length ?? 0) + 256));
        var row = new RowBuffer(resizer.Memory.Length, resizer);
        row.InitLayout(HybridRowVersion.V1, Schema.Value.ResultLayout, Schema.Value.Resolver);
        var r = RowWriter.WriteBuffer(ref row, result, WriteResult);
        if (r != Result.Success)
        {
            throw new InvalidOperationException($"Writing a batch result failed ({r}).");
        }

        return resizer.Memory[..row.Length].ToArray();
    }

    private static Result WriteResult(ref RowWriter writer, TypeArgument typeArgument, BatchOperationResult result)
    {
        var r = writer.WriteInt32("statusCode", result.StatusCode);
        if (r == Result.Success)
        {
            r = writer.WriteInt32("subStatusCode", result.SubStatusCode);
        }

        if (r == Result.Success && result.ETag is not null)
        {
            r = writer.WriteString("eTag", result.ETag);
        }

        if (r == Result.Success && result.ResourceBody is not null)
        {
            r = writer.WriteBinary("resourceBody", result.ResourceBody);
        }

        if (r == Result.Success)
        {
            r = writer.WriteFloat64("requestCharge", result.RequestCharge);
        }

        return r;
    }
}
