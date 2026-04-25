using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Subro.JsonTypes;

namespace Subro
{


    /// <summary>
    /// json related functions
    /// </summary>
    public static partial class Json
    {
        /// <summary>
        /// A wrapper for IJsonTypeInfoResolver, to always use the current type resolver chain of the default <see cref="Options"/>
        /// </summary>
        public class GlobalJsonTypeInfoForwarder : IJsonTypeInfoResolver
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


    }
}
