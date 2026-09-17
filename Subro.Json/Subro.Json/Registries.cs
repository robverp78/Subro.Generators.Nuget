using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Subro
{
    partial class Json
    {
        /// <summary>
        /// Registers a converter for every <see cref="JsonSerializerOptions"/> that uses the registered converters
        /// (<see cref="Options"/>, and any options passed through <see cref="AddRegisteredConverters"/>).
        /// A <see cref="JsonConverterFactory"/> is asked for each type; any other converter applies to its exact <see cref="JsonConverter.Type"/>.
        /// </summary>
        /// <remarks>
        /// Safe to call after the options have been used (for example from a module initializer), because the
        /// options hold the registry rather than the converters themselves.
        /// A type whose converter was resolved before its registration keeps the converter it got.
        /// </remarks>
        public static void RegisterConverter(JsonConverter converter) => ConverterRegistry.Instance.Register(converter);

        /// <summary>
        /// Adds the registered converters (see <see cref="RegisterConverter"/>) to the options, for options instances
        /// other than <see cref="Options"/>. Must be called before the options are used.
        /// </summary>
        public static JsonSerializerOptions AddRegisteredConverters(this JsonSerializerOptions options)
        {
            if (!options.Converters.Contains(ConverterRegistry.Instance))
                options.Converters.Add(ConverterRegistry.Instance);
            return options;
        }

        /// <summary>
        /// Before looking up converters or type info for a type, make sure the module initializers of its assembly
        /// have run. They register converters and contexts, and would otherwise only run once code in that
        /// assembly executes, which can be after the serializer has already resolved the type.
        /// </summary>
        static void EnsureModuleInitialized(Type type)
            => RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);

        sealed class ConverterRegistry : JsonConverterFactory
        {
            public static readonly ConverterRegistry Instance = new();

            readonly ConcurrentDictionary<Type, JsonConverter> exact = new();
            ImmutableArray<JsonConverterFactory> factories = ImmutableArray<JsonConverterFactory>.Empty;

            public void Register(JsonConverter converter)
            {
                if (converter is JsonConverterFactory factory)
                    ImmutableInterlocked.Update(ref factories, static (list, f) => list.Add(f), factory);
                else
                    exact[converter.Type ?? throw new ArgumentException("The converter has no type", nameof(converter))] = converter;
            }

            public override bool CanConvert(Type typeToConvert) => Find(typeToConvert) is not null;

            public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
                => Find(typeToConvert) switch
                {
                    JsonConverterFactory factory => factory.CreateConverter(typeToConvert, options),
                    var converter => converter
                };

            JsonConverter? Find(Type type)
            {
                EnsureModuleInitialized(type);
                if (exact.TryGetValue(type, out var converter))
                    return converter;
                foreach (var factory in factories)
                    if (factory.CanConvert(type))
                        return factory;
                return null;
            }
        }

        /// <summary>
        /// Resolver chain that can still be extended after <see cref="Options"/> has been used
        /// (<see cref="JsonSerializerOptions.TypeInfoResolverChain"/> cannot).
        /// </summary>
        sealed class ResolverRegistry(IJsonTypeInfoResolver initial) : IJsonTypeInfoResolver
        {
            ImmutableArray<IJsonTypeInfoResolver> resolvers = [initial];

            /// <summary>Inserts at the start, so the newest resolvers are checked first</summary>
            public void Insert(IJsonTypeInfoResolver resolver)
                => ImmutableInterlocked.Update(ref resolvers, static (list, r) => list.Insert(0, r), resolver);

            public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
            {
                EnsureModuleInitialized(type);
                foreach (var resolver in resolvers)
                    if (resolver.GetTypeInfo(type, options) is { } info)
                        return info;
                return null;
            }
        }
    }
}
