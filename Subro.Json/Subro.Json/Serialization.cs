using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace Subro
{
    partial class Json
    {

        /// <summary>
        /// Parses <paramref name="json"/> into a <typeparamref name="T"/>, with the contract <see cref="TypeInfo{T}"/> gives.
        /// </summary>
        public static T? Parse<T>(string json) => JsonSerializer.Deserialize(json, TypeInfo<T>());

        /// <summary>
        /// Parses <paramref name="json"/> into a <typeparamref name="T"/> with <paramref name="info"/>; without one, with the
        /// contract <see cref="TypeInfo{T}"/> gives.
        /// </summary>
        public static T? Parse<T>(string json, JsonTypeInfo<T>? info) => JsonSerializer.Deserialize(json, info ?? TypeInfo<T>());

        /// <summary>
        /// Writes <paramref name="value"/> as JSON, with the contract <see cref="TypeInfo{T}"/> gives.
        /// </summary>
        public static string Stringify<T>(this T value) => JsonSerializer.Serialize(value, TypeInfo<T>());

        /// <summary>
        /// Writes <paramref name="value"/> as JSON with <paramref name="info"/>; without one, with the contract
        /// <see cref="TypeInfo{T}"/> gives.
        /// </summary>
        public static string Stringify<T>(this T value, JsonTypeInfo<T>? info) => JsonSerializer.Serialize(value, info ?? TypeInfo<T>());

    }
}
