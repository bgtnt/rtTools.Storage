using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Serialization;
using System.Text;

namespace RtTools
{
    /// <summary>
    /// Stream-based object serializer used by the .NET 10 storage layer.
    /// </summary>
    public interface IDataSerializer
    {
        void Serialize(Stream serializationStream, object? graph);
        object? Deserialize(Stream serializationStream);
    }

    /// <summary>
    /// DataContractSerializer wrapper with a small type header. This is the default
    /// .NET 10 persistence serializer for object graphs.
    /// </summary>
    public sealed class DataContractDataSerializer : IDataSerializer
    {
        private const string Magic = "RTDS1";
        private static readonly ConcurrentDictionary<Type, DataContractSerializer> Serializers = new();

        public static DataContractDataSerializer Default { get; } = new();

        public void Serialize(Stream serializationStream, object? graph)
        {
            using BinaryWriter writer = new(serializationStream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);

            if (graph == null)
            {
                writer.Write(string.Empty);
                writer.Flush();
                return;
            }

            Type type = graph.GetType();
            writer.Write(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
            writer.Flush();

            CreateSerializer(type).WriteObject(serializationStream, graph);
        }

        public object? Deserialize(Stream serializationStream)
        {
            using BinaryReader reader = new(serializationStream, Encoding.UTF8, leaveOpen: true);
            string magic = reader.ReadString();
            if (!string.Equals(magic, Magic, StringComparison.Ordinal))
                throw new SerializationException("Unsupported RtTools serializer format.");

            string typeName = reader.ReadString();
            if (string.IsNullOrEmpty(typeName))
                return null;

            Type type = Type.GetType(typeName, throwOnError: false) ??
                throw new SerializationException("Cannot resolve serialized type: " + typeName);

            return CreateSerializer(type).ReadObject(serializationStream);
        }

        public byte[] SerializeToArray(object? graph)
        {
            if (graph == null)
                return [];

            using MemoryStream stream = new();
            this.Serialize(stream, graph);
            return stream.ToArray();
        }

        public object? Deserialize(byte[]? data) => this.Deserialize(data, 0, data?.Length ?? 0);

        public object? Deserialize(byte[]? data, int start, int length)
        {
            if (data == null || length == 0)
                return null;

            using MemoryStream stream = new(data, start, length);
            return this.Deserialize(stream);
        }

        private static DataContractSerializer CreateSerializer(Type type) =>
            Serializers.GetOrAdd(type, static t => new DataContractSerializer(t, new DataContractSerializerSettings
            {
                PreserveObjectReferences = true,
                MaxItemsInObjectGraph = int.MaxValue,
                IgnoreExtensionDataObject = false
            }));
    }

}
