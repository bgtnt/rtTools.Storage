using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace RtTools
{
    public static partial class Statics
    {
        public static bool TryChangeType<T>(object? value, [MaybeNullWhen(false)] out T result, IFormatProvider? provider = null)
        {
            if (value is T typedValue)
            {
                result = typedValue;
                return true;
            }

            if (value == null)
            {
                result = default;
                return !typeof(T).IsValueType;
            }

            if (TryChangeType(value, typeof(T), out object? converted, provider) && converted is T convertedValue)
            {
                result = convertedValue;
                return true;
            }

            result = default;
            return false;
        }

        public static bool TryChangeType(object? value, Type type, out object? result, IFormatProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(type);

            if (value == null)
            {
                result = null;
                return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
            }

            Type targetType = Nullable.GetUnderlyingType(type) ?? type;
            if (targetType.IsInstanceOfType(value))
            {
                result = value;
                return true;
            }

            if (targetType.IsEnum)
            {
                if (value is string text)
                {
                    bool parsed = Enum.TryParse(targetType, text, ignoreCase: true, out object? enumValue);
                    result = enumValue;
                    return parsed;
                }

                if (TryChangeType(value, Enum.GetUnderlyingType(targetType), out object? underlying, provider) && underlying != null)
                {
                    result = Enum.ToObject(targetType, underlying);
                    return true;
                }

                result = null;
                return false;
            }

            try
            {
                result = Convert.ChangeType(value, targetType, provider);
                return true;
            }
            catch (InvalidCastException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
            catch (ArgumentException) { }

            result = null;
            return false;
        }

        internal static readonly IDataSerializer DefaultSerializer = DataContractDataSerializer.Default;

        /// <summary>
        /// Serializes an object to data using the default serializer.
        /// </summary>
        /// <param name="obj">object to serialize</param>
        /// <returns>serialized data</returns>
        public static byte[] ToBytes(object? obj)
        {
            if (obj == null) return [];

            using (MemoryStream ms = new())
            { // serialize into byte[]
                DefaultSerializer.Serialize(ms, obj);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Deserializes data into an object using the default serializer.
        /// </summary>
        /// <param name="data">data to deserialize</param>
        /// <returns>deserialized object</returns>
        public static object? FromBytes(byte[]? data) => FromBytes(data, 0, data?.Length ?? 0);

        /// <summary>
        /// Deserializes data into an object using the default serializer.
        /// </summary>
        /// <param name="data">data to deserialize</param>
        /// <param name="start"></param>
        /// <param name="len"></param>
        /// <returns>deserialized object</returns>
        public static object? FromBytes(byte[]? data, int start, int len)
        {
            if (len == 0 || data == null || data.Length == 0) return null;

            using (MemoryStream ms = new(data, start, len)) // deserialize from byte[]
                return DefaultSerializer.Deserialize(ms);
        }
    }
}
