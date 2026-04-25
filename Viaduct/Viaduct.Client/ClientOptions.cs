using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace Viaduct.Client
{
    /// <summary>
    /// Based on the general <see cref="ViaductOptions"/>, this class represents the configuration options 
    /// specific to the client side, such as the base URL for HTTP requests.
    /// </summary>
    public partial record ViaductClientOptions : ViaductOptions
    {
        /// <summary>
        /// an optional type info resolver for the next (set of) clients being registered.
        /// For keeping a general store of type resolvers, use <see cref="Viaduct.Json.AddContextToDefault(JsonSerializerContext)"/> instead
        /// </summary>
        public IJsonTypeInfoResolver? JsonTypeInfoResolver { get; set; }


        /// <summary>
        /// Default options for the client. Any Viaduct client action that uses options makes a copy of this Default.
        /// This means that changing properties on this Default instance will affect all future calls that use it, but not calls that have already made a copy of it. This allows for dynamic configuration of default options at runtime, while still allowing individual calls to override options without affecting others.
        /// </summary>
        public static readonly ViaductClientOptions Default = new();
    }

    partial class ViaductClientFunctions
    {


        extension(ViaductClientOptions options)
        {
            public JsonTypeInfo<T>? GetJsonTypeInfo<T>()
            {

                // option specific types first
                if (options?.JsonTypeInfoResolver is { } resolver)
                {
                    var info = resolver.GetTypeInfo(typeof(T), Json.Options);
                    if (info is JsonTypeInfo<T> typed)
                        return typed;

                }
                return Json.GetTypeInfo<T>();
            }

            /// <summary>
            /// Not used directly in the Viaduct server/client functionality, but can be used as an extra to deserialize json with the same (Aot safe) json serializers registered in Viaduct
            /// </summary>
            public T? Parse<T>(string json) => Json.Parse<T>(json, options.GetJsonTypeInfo<T>());


            /// <summary>
            /// Not used directly in the Viaduct server/client functionality, but can be used as an extra to serialize an object to json with the same (Aot safe) json serializers registered in Viaduct
            /// </summary>
            public string Stringify<T>(T Object) => Json.Stringify(Object, options.GetJsonTypeInfo<T>());
            
        }
    }
}
