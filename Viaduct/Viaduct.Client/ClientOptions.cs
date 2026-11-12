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
        /// For keeping a general store of type resolvers, use <see cref="Subro.Json.AddContextToDefault(IJsonTypeInfoResolver)"/> instead
        /// </summary>
        public IJsonTypeInfoResolver? JsonTypeInfoResolver { get; set; }

        /// <summary>
        /// Supplies the access token for each request, when <see cref="ViaductOptions.RequiresAuthorization"/>
        /// is set. Returning <c>null</c> means there is none — nobody has signed in yet — and the request goes
        /// out unauthenticated for the server to refuse.
        /// </summary>
        /// <remarks>
        /// Asked per request, so a refreshed token is used as soon as it exists. Where getting the token needs
        /// services of its own, register an <see cref="IViaductAccessTokenProvider"/> instead: that is resolved
        /// from the application's own container.
        /// <para>
        /// An <c>Authorization</c> header already on a request is never replaced.
        /// </para>
        /// </remarks>
        public Func<System.Net.Http.HttpRequestMessage, System.Threading.CancellationToken, ValueTask<string?>>? GetAccessToken { get; set; }

        /// <summary>
        /// The scheme the token is sent under. <c>Bearer</c> unless something says otherwise.
        /// </summary>
        public string AuthorizationScheme { get; set; } = "Bearer";

        /// <summary>
        /// Default options for the client. Any Viaduct client action that uses options makes a copy of this Default.
        /// This means that changing properties on this Default instance will affect all future calls that use it, but not calls that have already made a copy of it. This allows for dynamic configuration of default options at runtime, while still allowing individual calls to override options without affecting others.
        /// </summary>
        public static readonly ViaductClientOptions Default = new();
    }

    /// <summary>
    /// Helpers shared by the generated clients: JSON contracts, url building, and the extensions on
    /// <see cref="ViaductClientOptions"/>.
    /// </summary>
    partial class ViaductClientFunctions
    {
        /// <summary>The serializer options a client's own resolver is read through, one per resolver.</summary>
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IJsonTypeInfoResolver, JsonSerializerOptions> ResolverOptions = new();

        /// <summary>Read-only from the start: a serializer options instance keeps the metadata it built only once it is.</summary>
        static JsonSerializerOptions ReadThrough(IJsonTypeInfoResolver resolver)
        {
            var options = new JsonSerializerOptions(Subro.Json.Options) { TypeInfoResolver = resolver };
            options.MakeReadOnly();
            return options;
        }


        extension(ViaductClientOptions options)
        {
            /// <summary>
            /// The JSON contract for <typeparamref name="T"/>, never null: the resolver set on these options first, then
            /// Subro.Json's (<see cref="Subro.Json.TypeInfo{T}"/>), the one source of JSON type information the server's
            /// endpoints read and write with as well. A type no context has is read by reflection with the same settings
            /// and converters, or, where there is no reflection (a trimmed or AOT app), is an error that names it.
            /// </summary>
            public JsonTypeInfo<T> GetJsonTypeInfo<T>()
            {
                // option specific types first, through serializer options made once per resolver: asked directly, a
                // resolver builds new metadata on every call, while JsonSerializerOptions keeps what it built.
                if (options?.JsonTypeInfoResolver is { } resolver
                    && ResolverOptions.GetValue(resolver, ReadThrough)
                        .TryGetTypeInfo(typeof(T), out var info)
                    && info is JsonTypeInfo<T> typed)
                {
                    return typed;
                }
                return Subro.Json.TypeInfo<T>();
            }

            /// <summary>Parses JSON with the contract these options give, as a client's calls do.</summary>
            public T? Parse<T>(string json) => Subro.Json.Parse(json, options.GetJsonTypeInfo<T>());

            /// <summary>Writes JSON with the contract these options give, as a client's calls do.</summary>
            public string Stringify<T>(T Object) => Subro.Json.Stringify(Object, options.GetJsonTypeInfo<T>());
            
        }
    }
}
