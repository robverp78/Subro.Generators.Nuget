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
            public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) 
                => Options.TypeInfoResolver?.GetTypeInfo(type, options);
        }

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
