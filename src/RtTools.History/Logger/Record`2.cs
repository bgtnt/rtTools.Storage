using System;
using System.Collections.Generic;
using System.IO;

namespace RtTools.Logger
{
    /// <summary>
    /// Default DataRecord implementatin for Record {Key , Source} , where 
    /// Key - record key for indexed search (log.FindAll)
    /// Source - data Object - serialized/deserialized by the RtTools .NET 10 serializer
    /// </summary>
    /// <typeparam name="T">Data object type.</typeparam>
    /// <typeparam name="TKey">Record Key Type</typeparam>
    public sealed class Record<T, TKey> : DataRecord<TKey>
        where TKey : Key, new()
    {

        /// <summary>
        /// to convert byte[] to object and vice versa
        /// </summary>
        public static IDataSerializer Serializer { get; } = DataContractDataSerializer.Default;

        /// <summary>
        /// Source Key
        /// </summary>
        public override TKey Key { get; set; } = new();

        /// <summary>
        /// Serialized data(Source)
        /// </summary>
        public override byte[] Data
        {
            get {
                object? obj = this.Source;

                if (obj == null) return [];

                using (MemoryStream ms = new())
                { // serialize into byte[]
                    Serializer.Serialize(ms, obj);
                    return ms.ToArray();
                }

            }
            set {
                if (value == null || value.Length == 0)
                {
                    this.Source = default;
                }
                else
                {
                    using MemoryStream ms = new(value); // deserialize from byte[]
                    object? source = Serializer.Deserialize(ms);
                    if (source is null)
                        this.Source = default;
                    else if (source is T typedSource)
                        this.Source = typedSource;
                    else
                        throw new InvalidDataException($"Serialized value is '{source.GetType().FullName}', expected '{typeof(T).FullName}'.");
                }
            }
        }

        /// <summary>
        /// Non Serialized Record data
        /// </summary>
        public T? Source { get; set; }

        /// <summary>
        /// overridden
        /// </summary>
        /// <returns></returns>
        public override string ToString() => this.Key.ToString() + ": " + Convert.ToString(this.Source);
    }
}
