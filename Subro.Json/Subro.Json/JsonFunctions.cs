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

        static readonly ResolverRegistry Resolvers = new(CommonTypes.Default);

        public static readonly JsonSerializerOptions Options = new()
        {
            TypeInfoResolver = Resolvers,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { ConverterRegistry.Instance }
        };

        /// <summary>
        /// Adds the type resolver to the top of the type info resolver chain.
        /// Can be called after <see cref="Options"/> has been used.
        /// </summary>
        public static void AddContextToDefault(this IJsonTypeInfoResolver context) => Resolvers.Insert(context);

        /// <summary>
        /// <see cref="Options"/> plus reflection-based type info, for types that none of the registered contexts contain.
        /// </summary>
#pragma warning disable IL2026, IL3050 //only used by the reflection fallbacks, which carry the same pragma
        static JsonSerializerOptions ReflectionOptions => field ??= CreateReflectionOptions();
#pragma warning restore IL2026, IL3050

        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Reflection-based serialization")]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection-based serialization")]
        static JsonSerializerOptions CreateReflectionOptions() => new(Options)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(Resolvers, new DefaultJsonTypeInfoResolver())
        };


        public static JsonTypeInfo<T>? GetTypeInfo<T>()
        {
            if (Options.TryGetTypeInfo(typeof(T), out var builtin) && builtin is JsonTypeInfo<T> builtinTyped)
                return builtinTyped;
            //TODO: aot warnings

            return null;
        }


    }
}
