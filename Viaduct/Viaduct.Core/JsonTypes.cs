using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Viaduct.JsonTypes;

namespace Viaduct
{
    namespace JsonTypes
    {
        /// <summary>
        /// Contains <see cref="JsonTypeInfo"/> for the most common types used in endpoints
        /// </summary>
        [JsonSerializable(typeof(string))]
        [JsonSerializable(typeof(int))]
        [JsonSerializable(typeof(int?))]
        [JsonSerializable(typeof(long))]
        [JsonSerializable(typeof(long?))]
        [JsonSerializable(typeof(bool))]
        [JsonSerializable(typeof(double))]
        [JsonSerializable(typeof(double?))]
        [JsonSerializable(typeof(decimal))]
        [JsonSerializable(typeof(Guid))]
        [JsonSerializable(typeof(Guid?))]
        [JsonSerializable(typeof(DateTime))]
        [JsonSerializable(typeof(DateTime?))]
        [JsonSerializable(typeof(DateTimeOffset))]
        [JsonSerializable(typeof(DateTimeOffset?))]
        [JsonSerializable(typeof(DateOnly))]
        [JsonSerializable(typeof(DateOnly?))]
        [JsonSerializable(typeof(TimeOnly))]
        [JsonSerializable(typeof(TimeOnly?))]
        [JsonSerializable(typeof(byte[]))]
        [JsonSerializable(typeof(Dictionary<string, string>))]
        [JsonSerializable(typeof(List<string>))]
        [JsonSerializable(typeof(string[]))]
        [JsonSerializable(typeof(int[]))]
        [JsonSerializable(typeof(Memory<byte>))]
        public partial class CommonTypes : JsonSerializerContext { }
    }

    /// <summary>
    /// Viaduct json functions
    /// </summary>
    public static class Json
    {
        /// <summary>
        /// A wrapper for IJsonTypeInfoResolver, to always use the current type resolver chain of the default <see cref="Options"/>
        /// </summary>
        public class ViaductGlobalJsonTypeInfoForwarder : IJsonTypeInfoResolver
        {
            /// <summary>
            /// Gets the JsonTypeInfo for the specified type.
            /// </summary>
            /// <remarks>Delegates to Options.TypeInfoResolver?.GetTypeInfo(type, options) when a
            /// resolver is configured.</remarks>
            /// <param name="type">The type to get metadata for.</param>
            /// <param name="options">The JsonSerializerOptions to use when resolving the type information.</param>
            /// <returns>A JsonTypeInfo describing the specified type, or null if no resolver is configured or the type cannot be
            /// resolved.</returns>
            public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) 
                => Options.TypeInfoResolver?.GetTypeInfo(type, options);
        }

        /// <summary>
        /// Default JsonSerializerOptions configured with CommonTypes.Default as the TypeInfoResolver and camel-case
        /// property naming.
        /// </summary>
        /// <remarks>Shared for reuse. Do not modify this instance after it is used concurrently; create a
        /// new JsonSerializerOptions for customizations.</remarks>
        public static readonly JsonSerializerOptions Options = new()
        {
            TypeInfoResolver = CommonTypes.Default,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Adds the type resolver to the top of the type info resolver chain.
        /// </summary>
        public static void AddContextToDefault(this IJsonTypeInfoResolver context)
        {
            // Insert, so newest contexts are checked first
            Options.TypeInfoResolverChain.Insert(0, context);
        }

        /// <summary>
        /// Gets the JsonTypeInfo for a type from the current type resolver chain of the default <see cref="Options"/>, if it exists. This is used internally to get the json type info for parameters and return types, but can also be used as an extra to get the same json type info for use in custom serialization scenarios. If no json type info is found for the type, returns null (and you can fall back to non AOT safe serialization if needed)
        /// </summary>
        public static JsonTypeInfo<T>? GetTypeInfo<T>()
        {
            if (Options.TryGetTypeInfo(typeof(T), out var builtin) && builtin is JsonTypeInfo<T> builtinTyped)
                return builtinTyped;
            //TODO: aot warnings

            return null;
        }

        /// <summary>
        /// Not used directly in the Viaduct server/client functionality, but can be used as an extra to deserialize json with the same (Aot safe) json serializers registered in Viaduct
        /// </summary>
        public static T? Parse<T>(string json) => Parse<T>(json, GetTypeInfo<T>());

        /// <summary>
        /// Not used directly in the Viaduct server/client functionality, but can be used as an extra to deserialize json with the same (Aot safe) json serializers registered in Viaduct
        /// </summary>
        public static T? Parse<T>(string json, JsonTypeInfo<T>? info)
        {
            if (info == null)
#pragma warning disable IL2026, IL3050 //Checks for AOT environment are in GetJsonTypeInfo
                return JsonSerializer.Deserialize<T>(json);
#pragma warning restore IL2026, IL3050
            else
                return JsonSerializer.Deserialize<T>(json, info);
        }

        /// <summary>
        /// Not used directly in the Viaduct server/client functionality, but can be used as an extra to serialize an object to json with the same (Aot safe) json serializers registered in Viaduct
        /// </summary>
        public static string Stringify<T>(this T Object) => Stringify(Object, GetTypeInfo<T>());

        /// <summary>
        /// Not used directly in the Viaduct server/client functionality, but can be used as an extra to serialize an object to json with the same (Aot safe) json serializers registered in Viaduct
        /// </summary>
        public static string Stringify<T>(this T Object, JsonTypeInfo<T>? info)
        {
            if (info == null)
#pragma warning disable IL2026, IL3050 //Checks for AOT environment are in GetJsonTypeInfo
                return JsonSerializer.Serialize(Object);
#pragma warning restore IL2026, IL3050
            else
                return JsonSerializer.Serialize(Object, info);
            throw new NotImplementedException();
        }

        
    }
}
