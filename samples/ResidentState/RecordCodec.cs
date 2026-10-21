using RtTools;

namespace ResidentState;

public sealed class StateRecord
{
    public string Name { get; set; } = "";
    public int Revision { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? TransientMessage { get; set; }
}

public static class RecordCodec
{
    public static byte[] Write(KeyValuePair<string, StateRecord> item)
    {
        var buffer = new BufferBuilder(64);
        buffer.Append((byte)1);
        buffer.Append(item.Key, TypeCode.Object);
        buffer.Append(item.Value.Name, TypeCode.Object);
        buffer.Append(item.Value.Revision);
        buffer.Append(item.Value.CreatedUtc);
        // TransientMessage belongs to this process and is deliberately omitted.
        return buffer.ToArray();
    }

    public static KeyValuePair<string, StateRecord> Read(byte[] bytes)
    {
        var buffer = new BufferBuilder(bytes);
        if (buffer.ReadByte() != 1) throw new InvalidDataException("Unsupported record version.");
        string key = buffer.ReadString(TypeCode.Object) ?? throw new InvalidDataException("Null key.");
        string name = buffer.ReadString(TypeCode.Object) ?? throw new InvalidDataException("Null name.");
        var record = new StateRecord { Name = name, Revision = buffer.ReadInt32(), CreatedUtc = buffer.ReadDateTime() };
        if (buffer.Left != 0) throw new InvalidDataException("Trailing record bytes.");
        return new(key, record);
    }
}
