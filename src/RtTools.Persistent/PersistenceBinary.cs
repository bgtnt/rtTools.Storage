using System;
using System.IO;

namespace RtTools;

internal static class PersistenceBinary
{
    public static bool Equals(byte[] first, byte[] second) =>
        ReferenceEquals(first, second) ||
        first is not null && second is not null && first.AsSpan().SequenceEqual(second);

    public static string ToHexString(byte[] data, int start, int length) =>
        Convert.ToHexString(data.AsSpan(start, length));

    public static byte[] ToBytes(object? value)
    {
        if (value is null)
            return [];

        using var stream = new MemoryStream();
        DataContractDataSerializer.Default.Serialize(stream, value);
        return stream.ToArray();
}
    public static object? FromBytes(byte[]? data)
    {
        if (data is not { Length: > 0 })
            return null;

        using var stream = new MemoryStream(data, writable: false);
        return DataContractDataSerializer.Default.Deserialize(stream);
    }
}
