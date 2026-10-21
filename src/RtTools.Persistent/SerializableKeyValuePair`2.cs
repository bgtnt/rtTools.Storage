using System;
using System.Collections.Generic;
using System.Text;

namespace RtTools
{
    /// <summary>
    /// Serializable key/value pair structure for persistence APIs that need settable properties.
    /// </summary>
    public struct SerializableKeyValuePair<TKey, TValue>
    {
        public TKey Key { get; set; }

        public TValue Value { get; set; }

        public static implicit operator KeyValuePair<TKey, TValue>(SerializableKeyValuePair<TKey, TValue> item) => new(item.Key, item.Value);

        public static implicit operator SerializableKeyValuePair<TKey, TValue>(KeyValuePair<TKey, TValue> item) => new() { Key = item.Key, Value = item.Value };

        public override readonly string ToString()
        {
            StringBuilder builder = new();

            builder.Append('[');

            if (this.Key != null)
                builder.Append(this.Key.ToString());

            builder.Append(',');

            if (this.Value != null)
                builder.Append(this.Value.ToString());

            builder.Append(']');

            return builder.ToString();
        }
    }
}
