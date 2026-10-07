using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DbEmulator.Storage;

/// <summary>
/// Generates Cosmos DB resource ids (_rid) in the binary layout the SDK parses:
/// database = 4 bytes; collection = database + 4 bytes with the high bit of byte[4] set;
/// collection children (documents, pkranges, scripts) = collection + 8-byte little-endian id whose
/// top nibble (byte[15] >> 4) is the child type. The string form is base64 with '/' replaced by '-'.
/// </summary>
internal static class ResourceIds
{
    public const byte DocumentType = 0x0;
    public const byte PartitionKeyRangeType = 0x5;
    public const byte UserDefinedFunctionType = 0x6;
    public const byte TriggerType = 0x7;
    public const byte StoredProcedureType = 0x8;

    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).Replace('/', '-');

    public static bool TryDecode(string? rid, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(rid))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(rid.Replace('-', '/'));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A database rid derived from the database id, so re-creating a database by name reuses its rid.</summary>
    public static string DatabaseRid(string databaseId, int salt = 0)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"db|{salt}|{databaseId}"), hash);
        Span<byte> rid = stackalloc byte[4];
        hash[..3].CopyTo(rid);
        rid[3] = 0;
        if (rid[0] == 0 && rid[1] == 0 && rid[2] == 0)
        {
            rid[0] = 1;
        }

        return Encode(rid);
    }

    /// <summary>A collection rid derived from the database rid and the collection id.</summary>
    public static string CollectionRid(string databaseRid, string collectionId, int salt = 0)
    {
        if (!TryDecode(databaseRid, out var db) || db.Length != 4)
        {
            throw new ArgumentException($"Invalid database rid '{databaseRid}'.", nameof(databaseRid));
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"coll|{salt}|{collectionId}"), hash);
        Span<byte> rid = stackalloc byte[8];
        db.CopyTo(rid);
        hash[..4].CopyTo(rid[4..]);
        rid[4] |= 0x80; // marks the 8-byte id as a collection (not a user)
        return Encode(rid);
    }

    /// <summary>A rid for a child resource of a collection (document, pkrange, script).</summary>
    public static string ChildRid(string collectionRid, ulong sequence, byte type)
    {
        if (!TryDecode(collectionRid, out var coll) || coll.Length != 8)
        {
            throw new ArgumentException($"Invalid collection rid '{collectionRid}'.", nameof(collectionRid));
        }

        if (sequence == 0 || sequence >= 1UL << 60)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        Span<byte> rid = stackalloc byte[16];
        coll.CopyTo(rid);
        BinaryPrimitives.WriteUInt64LittleEndian(rid[8..], sequence);
        rid[15] = (byte)((rid[15] & 0x0F) | (type << 4));
        return Encode(rid);
    }

    public static bool IsDatabaseRid(string? value) => TryDecode(value, out var b) && b.Length == 4;

    public static bool IsCollectionRid(string? value) => TryDecode(value, out var b) && b.Length == 8 && (b[4] & 0x80) != 0;

    /// <summary>True when <paramref name="childRid"/> is a valid 16-byte rid that belongs to <paramref name="collectionRid"/>.</summary>
    public static bool BelongsTo(string? childRid, string collectionRid)
    {
        return TryDecode(childRid, out var child) && child.Length == 16
            && TryDecode(collectionRid, out var coll) && coll.Length == 8
            && child.AsSpan(0, 8).SequenceEqual(coll);
    }

    /// <summary>A short random etag in the Cosmos format (a quoted GUID).</summary>
    public static string NewETag() => $"\"{Guid.NewGuid()}\"";

    public static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
