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
#if NET9_0_OR_GREATER
            //a type discriminator may come back anywhere in the object: Postgres jsonb, for one, does not keep property order
            //(net8 has no such option, so there the discriminator must still come first)
            AllowOutOfOrderMetadataProperties = true,
#endif
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



        /// <summary>
        /// The registered contract for <typeparamref name="T"/>: from a context added with <see cref="AddContextToDefault"/>
        /// (source-generated, so safe under trimming and AOT) or a built-in one; null when none has it.
        /// </summary>
        /// <remarks>
        /// Most code wants <see cref="TypeInfo{T}"/>, which falls back to reflection rather than to null.
        /// <para>
        /// <see cref="Options"/> is made read-only on the first lookup: only then does it keep the contracts it built, where
        /// before it built a new one for every lookup (2.8 µs and 1.3 KB each, then set up again on use). Converters and
        /// contexts are still added after that, through <see cref="RegisterConverter"/> and <see cref="AddContextToDefault"/>.
        /// </para>
        /// </remarks>
        public static JsonTypeInfo<T>? GetTypeInfo<T>()
        {
            if (!Options.IsReadOnly)
                Options.MakeReadOnly();
            return Options.TryGetTypeInfo(typeof(T), out var registered) && registered is JsonTypeInfo<T> typed ? typed : null;
        }

        /// <summary>
        /// The contract for <typeparamref name="T"/>, never null: the registered one where there is one, else one built by
        /// reflection with the settings and converters of <see cref="Options"/>, so the JSON is the same either way. Every
        /// reader and writer of JSON (Parse, Stringify, Viaduct's clients) goes through this, which is what keeps them alike.
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// Reflection is not available (a trimmed or AOT app, such as on a phone) and no registered context has the type:
        /// it has to be added to a <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> registered with
        /// <see cref="AddContextToDefault"/>. Said here, by name, rather than as whatever System.Text.Json fails with later.
        /// </exception>
        public static JsonTypeInfo<T> TypeInfo<T>()
        {
            if (GetTypeInfo<T>() is { } registered)
                return registered;

            if (!JsonSerializer.IsReflectionEnabledByDefault)
                throw new NotSupportedException(
                    $"There is no JSON type information for {typeof(T).FullName}, and reflection is not available here (a trimmed or AOT app). "
                    + $"Add it to a JsonSerializerContext ([JsonSerializable(typeof({typeof(T).Name}))]) registered with Subro.Json.AddContextToDefault.");

#pragma warning disable IL2026, IL3050 //only reached where reflection is enabled, checked above
            return (JsonTypeInfo<T>)ReflectionOptions.GetTypeInfo(typeof(T));
#pragma warning restore IL2026, IL3050
        }


    }
}
