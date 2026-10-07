using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DbEmulator.Storage;

/// <summary>
/// Effective partition key (EPK) hashing, version 2 (Hash and MultiHash): the hex strings the SDK uses for
/// x-ms-start-epk / x-ms-end-epk (feed ranges, prefix queries on hierarchical partition keys).
/// MurmurHash3 x64 128-bit over the binary encoding of each component, like the Cosmos SDK.
/// </summary>
internal static class EffectivePartitionKey
{
    // Microsoft.Azure.Documents.Routing.PartitionKeyComponentType
    private const byte Undefined = 0x00;
    private const byte Null = 0x01;
    private const byte False = 0x02;
    private const byte True = 0x03;
    private const byte Number = 0x05;
    private const byte String = 0x08;

    /// <summary>The EPK of a document, or null when it cannot be computed (version 1 / legacy containers).</summary>
    public static string? Compute(PartitionKeyDefinition definition, JsonElement document)
    {
        if (definition.IsNone || definition.Version < 2)
        {
            return null;
        }

        var multiHash = string.Equals(definition.Kind, "MultiHash", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder(multiHash ? 32 * definition.Paths.Count : 32);
        if (multiHash)
        {
            foreach (var path in definition.Paths)
            {
                using var buffer = new MemoryStream();
                WriteComponent(buffer, PartitionKeyDefinition.TryNavigate(document, path, out var value) ? value : default);
                AppendHash(sb, buffer);
            }
        }
        else
        {
            using var buffer = new MemoryStream();
            foreach (var path in definition.Paths)
            {
                WriteComponent(buffer, PartitionKeyDefinition.TryNavigate(document, path, out var value) ? value : default);
            }

            AppendHash(sb, buffer);
        }

        return sb.ToString();
    }

    private static void WriteComponent(Stream buffer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                buffer.WriteByte(String);
                var utf8 = Encoding.UTF8.GetBytes(value.GetString()!);
                buffer.Write(utf8);
                buffer.WriteByte(0xFF);
                break;
            case JsonValueKind.Number:
                buffer.WriteByte(Number);
                Span<byte> number = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(number, value.GetDouble());
                buffer.Write(number);
                break;
            case JsonValueKind.True:
                buffer.WriteByte(True);
                break;
            case JsonValueKind.False:
                buffer.WriteByte(False);
                break;
            case JsonValueKind.Null:
                buffer.WriteByte(Null);
                break;
            default:
                buffer.WriteByte(Undefined);
                break;
        }
    }

    private static void AppendHash(StringBuilder sb, MemoryStream buffer)
    {
        var (low, high) = MurmurHash3X64128(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        Span<byte> hash = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(hash, low);
        BinaryPrimitives.WriteUInt64LittleEndian(hash[8..], high);
        hash.Reverse();
        hash[0] &= 0x3F; // the space is [00.., FF): clear the top bits
        sb.Append(Convert.ToHexString(hash));
    }

    /// <summary>MurmurHash3 x64 128-bit, seed 0.</summary>
    internal static (ulong Low, ulong High) MurmurHash3X64128(ReadOnlySpan<byte> data)
    {
        const ulong c1 = 0x87c37b91114253d5UL;
        const ulong c2 = 0x4cf5ad432745937fUL;
        ulong h1 = 0, h2 = 0;
        var blocks = data.Length / 16;
        for (var i = 0; i < blocks; i++)
        {
            var k1 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i * 16, 8));
            var k2 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i * 16 + 8, 8));
            k1 *= c1;
            k1 = ulong.RotateLeft(k1, 31);
            k1 *= c2;
            h1 ^= k1;
            h1 = ulong.RotateLeft(h1, 27);
            h1 += h2;
            h1 = h1 * 5 + 0x52dce729;
            k2 *= c2;
            k2 = ulong.RotateLeft(k2, 33);
            k2 *= c1;
            h2 ^= k2;
            h2 = ulong.RotateLeft(h2, 31);
            h2 += h1;
            h2 = h2 * 5 + 0x38495ab5;
        }

        var tail = data[(blocks * 16)..];
        ulong t1 = 0, t2 = 0;
        for (var i = tail.Length - 1; i >= 8; i--)
        {
            t2 = (t2 << 8) | tail[i];
        }

        for (var i = Math.Min(tail.Length, 8) - 1; i >= 0; i--)
        {
            t1 = (t1 << 8) | tail[i];
        }

        if (tail.Length > 8)
        {
            t2 *= c2;
            t2 = ulong.RotateLeft(t2, 33);
            t2 *= c1;
            h2 ^= t2;
        }

        if (tail.Length > 0)
        {
            t1 *= c1;
            t1 = ulong.RotateLeft(t1, 31);
            t1 *= c2;
            h1 ^= t1;
        }

        h1 ^= (ulong)data.Length;
        h2 ^= (ulong)data.Length;
        h1 += h2;
        h2 += h1;
        h1 = FMix(h1);
        h2 = FMix(h2);
        h1 += h2;
        h2 += h1;
        return (h1, h2);
    }

    private static ulong FMix(ulong k)
    {
        k ^= k >> 33;
        k *= 0xff51afd7ed558ccdUL;
        k ^= k >> 33;
        k *= 0xc4ceb9fe1a85ec53UL;
        k ^= k >> 33;
        return k;
    }
}
