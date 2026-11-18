using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using static Viaduct.Server.PendingMappers;

namespace Viaduct.Server
{
    /// <summary>
    /// Options for mapping endpoints to interfaces. This is used in the mapping functions to configure the endpoints that are created.
    /// </summary>
    /// <remarks>
    /// Changing the <see cref="Default"/> options will only affect server mapping registrations and mappings that are
    /// made after that change. Registered options are cloned so later changes do not affect
    /// those registered options. This is by design so global settings can be chained.
    /// </remarks>
    public record ViaductServerOptions : ViaductOptions
    {
        /// <summary>
        /// The default options used for mapping endpoints to interfaces. This can be changed to affect all mappings made after the change, but does not affect mappings that were already registered since options are cloned during registration. This allows for chaining of global settings.
        /// </summary>
        public static readonly ViaductServerOptions Default = new();

        /// <summary>
        /// Whether generated endpoints carry their documentation: the tag they are grouped under, and the
        /// summary and description taken from the interface. Default <c>true</c>.
        /// </summary>
        /// <remarks>
        /// This is what fills an OpenAPI document and everything generated from it. Turn it off only to keep
        /// the document bare.
        /// </remarks>
        public bool IncludeEndpointMetadata { get; set; } = true;

        /// <summary>
        /// Whether generated endpoints get a name, which is also their OpenAPI <c>operationId</c>. Default
        /// <c>true</c>.
        /// </summary>
        /// <remarks>
        /// Names are generated as <c>{Interface}_{Method}</c> — <c>UserService_GetUser</c> — and a method can
        /// set its own with <see cref="ViaductEndpointNameAttribute"/>. Overloads get none: which one a name
        /// refers to would be a guess.
        /// <para>
        /// ASP.NET Core requires endpoint names to be unique across the whole application and throws at startup
        /// when two collide. Viaduct sees one interface at a time, so two interfaces sharing a method name is
        /// the case to watch: name one of them explicitly, or turn this off and lose the operationIds.
        /// </para>
        /// </remarks>
        public bool GenerateEndpointNames { get; set; } = true;

        /// <summary>
        /// Whether a route parameter is mapped with its parameter's type as a constraint, such as <c>{userId:guid}</c>.
        /// Default <c>true</c>.
        /// </summary>
        /// <remarks>
        /// Without one, ASP.NET Core ranks <c>/users/{userId}</c> and <c>/users/{search}</c> alike, and answers every
        /// request to either with a 500 for matching both. With it, a Guid goes to the first and other text to the second.
        /// A value that is not of the type answers 404 instead of 400, as no endpoint matches it.
        /// <para>
        /// Only parameters of a type whose constraint reads a value as its binding does: <c>Guid</c>, <c>int</c>,
        /// <c>long</c>, <c>bool</c>, <c>decimal</c>, <c>double</c>, <c>float</c>, or one of those nullable. A route that
        /// gives a parameter a constraint of its own keeps it. Only the server maps constraints; the client's paths do
        /// not change.
        /// </para>
        /// </remarks>
        public bool UseRouteTypeConstraints { get; set; } = true;
    }

    /// <summary>
    /// Main entry point to add minimal api server side mappings from interfaces
    /// </summary>
    public static partial class ServerMappings
    {
        /// <summary>
        /// Contains methods to register services and map endpoints for interfaces in a single step. 
        /// These methods ensure that the specified implementation is registered as a service for the given interface and that the corresponding endpoints are registered for mapping to the app/route builder. 
        /// NB: the endpoints are only created after <see cref="AddRegisteredEndpoints"/>  on the app/route builder. 
        /// </summary>
        /// <typeparam name="TInterface"></typeparam>
        /// <param name="services">The service collection to register on</param>
        extension<TInterface>(IServiceCollection services)
            where TInterface : class
        {
            /// <summary>
            /// Registers a singleton service of the specified implementation type and registers the endpoints for mapping at the same time.          
            /// NB: the endpoints are only created after <see cref="AddRegisteredEndpoints"/>  on the app/route builder. 
            /// </summary>
            /// <remarks>Use this method to ensure that a single instance of the service is used
            /// throughout the application's lifetime. The configuration delegate allows customization of service
            /// behavior during initialization.</remarks>
            /// <typeparam name="TImplementation">The type of the implementation to register as a singleton service. Must be a class that implements the specified interface.</typeparam>
            /// <param name="configure">optional configuration for the endpoints, such as base path and authorization requirements.</param>             
            /// <returns>The <see cref="IServiceCollection"/> instance, allowing for method chaining.</returns>
            public IServiceCollection AddSingletonAndMap<TImplementation>(Action<ViaductServerOptions>? configure = null)
                where TImplementation : class, TInterface
            {
                throw new ViaductMethodNotInterceptedException();
            }

            /// <summary>
            /// Registers a scoped service of the specified implementation type and registers the endpoints for mapping at the same time.
            /// NB: the endpoints are only created after <see cref="AddRegisteredEndpoints"/> on the app/route builder.
            /// </summary>
            /// <remarks>Use this method to ensure that a new instance of the service is created
            /// for each scope (typically per HTTP request). The configuration delegate allows customization of service
            /// behavior during initialization.</remarks>
            /// <typeparam name="TImplementation">The type of the implementation to register as a scoped service. Must be a class that implements the specified interface.</typeparam>
            /// <param name="configure">optional configuration for the endpoints, such as base path and authorization requirements.</param>
            /// <returns>The <see cref="IServiceCollection"/> instance, allowing for method chaining.</returns>
            public IServiceCollection AddScopedAndMap<TImplementation>(Action<ViaductServerOptions>? configure = null)
                where TImplementation : class, TInterface
            {
                throw new ViaductMethodNotInterceptedException();
            }

            /// <summary>
            /// Registers a transient service of the specified implementation type and registers the endpoints for mapping at the same time.
            /// NB: the endpoints are only created after <see cref="AddRegisteredEndpoints"/> on the app/route builder.
            /// </summary>
            /// <remarks>Use this method to ensure that a new instance of the service is created
            /// each time it is requested. The configuration delegate allows customization of service
            /// behavior during initialization.</remarks>
            /// <typeparam name="TImplementation">The type of the implementation to register as a transient service. Must be a class that implements the specified interface.</typeparam>
            /// <param name="configure">optional configuration for the endpoints, such as base path and authorization requirements.</param>
            /// <returns>The <see cref="IServiceCollection"/> instance, allowing for method chaining.</returns>
            public IServiceCollection AddTransientAndMap<TImplementation>(Action<ViaductServerOptions>? configure = null)
                where TImplementation : class, TInterface
            {
                throw new ViaductMethodNotInterceptedException();
            }

            /// <summary>
            /// An optional way to Register the endpoints during service registration. 
            /// Normally either the endpoints would be added directly to the app with <see cref="AddEndpoints"/> or
            /// registered with one of the Add...AndMap functions, but this allows registation for later mapping while chaining.
            /// NB: the endpoints are only created after <see cref="AddRegisteredEndpoints"/>  on the app/route builder. 
            /// </summary>            
            /// <param name="configure">optional configuration for the endpoints, such as base path and authorization requirements.</param>
            /// <returns>the services passed to the function, for chaining</returns>
            public IServiceCollection RegisterEndpoints(Action<ViaductServerOptions>? configure = null)
            {
                throw new ViaductMethodNotInterceptedException();
            }

            /// <summary>
            /// A helper method to change the <see cref="ViaductServerOptions.Default"/> <see cref="ViaductServerOptions"/> for all mapping registrations
            /// made after this point. It does the same as altering the Default directly, but allows for chaining.
            /// </summary>            
            public IServiceCollection ChangeViaductOptions(Action<ViaductServerOptions> configure)
            {
                if (configure is not null)
                    configure(ViaductServerOptions.Default);
                return services;                
            }


        }

        /// <summary>
        /// Adds the Viaduct type resolver chain to the DI 
        /// </summary>
        public static IServiceCollection AddJsonTypeResolvers(this IServiceCollection services)
        {
            if (services.Any(d => d.ServiceType == typeof(ViaductJsonTypesMarker)))
                return services;

            services.AddSingleton<ViaductJsonTypesMarker>();
            services.ConfigureHttpJsonOptions(options =>
            {
                // Subro.Json's converters, and its registered (source-generated) contexts ahead of reflection: the clients
                // read and write with the same, so a type is written and read alike on both sides.
                Subro.Json.AddRegisteredConverters(options.SerializerOptions);
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, new Subro.Json.GlobalJsonTypeInfoForwarder());
            });
            return services;
        }
        /// <summary>
        /// Dummy class to check if the json services have been added to the specific services collection before
        /// </summary>
        sealed class ViaductJsonTypesMarker { }

        // A RegisterEndpoints<TInterface>() without a service collection used to stand here. It could not be intercepted (the
        // generated interceptor takes the services, CS9144), and registrations now live in the service collection, so
        // services.RegisterEndpoints<TInterface>() is the one.


        extension(IEndpointRouteBuilder app)
        {
            /// <summary>
            /// Directly adds the endpoints of the interface to the app/route builder. This assumes the service implementation has been registered
            /// before and only the endpoints themselves need to be added.
            /// </summary>
            /// <typeparam name="TInterface">the interface to add the minimal api mappings for</typeparam>
            /// <param name="configure">optional configuration for the endpoints, such as base path and authorization requirements.</param>
            /// <returns>the app that was used, for chaining purposes</returns>
            public IEndpointRouteBuilder AddEndpoints<TInterface>(Action<ViaductServerOptions>? configure = null)
            {
                throw new ViaductMethodNotInterceptedException();
            }

            /// <summary>
            /// Has to be called after registering endpoints during service registration phase. It creates the mappings for all registered endpoints.
            /// </summary>            
            public IEndpointRouteBuilder AddRegisteredEndpoints() => PendingMappers.AddRegisteredEndpoints(app);


        }
    }

    /// <summary>
    /// Helper functions for the generated code
    /// </summary>
    public static partial class ViaductServerHelperFunctions
    {
        /// <summary>
        /// Ensures that an options instance is created for the given configure action. If the action is null, it returns a clone of the default options. If the action is not null, it creates a clone of the default options and applies the configuration to it, returning the configured instance. This is used to ensure that each mapping registration gets its own options instance that can be configured without affecting other registrations.
        /// </summary>        
        public static ViaductServerOptions GetOptionsInstance(this Action<ViaductServerOptions>? configure)
            => configure.GetOptionsInstance(ViaductServerOptions.Default);


        [GeneratedRegex(ViaductFunctions.GetRouteParametersPattern)]
        private static partial Regex GetRouteParametersRegex();

        /// <summary>A route parameter, not an escaped brace: <c>{id}</c>, <c>{id?}</c>, <c>{id=5}</c>, <c>{id:int}</c>, <c>{*path}</c>.</summary>
        [GeneratedRegex(@"(?<!\{)\{(?<catchAll>\*{0,2})(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<rest>[^{}]*)\}(?!\})")]
        private static partial Regex RouteParameterRegex();

        /// <summary>
        /// <paramref name="template"/> with each parameter that <paramref name="constraints"/> names given that constraint:
        /// <c>{userId}</c> becomes <c>{userId:guid}</c>, <c>{age?}</c> <c>{age:int?}</c>. A parameter with a constraint of
        /// its own, and a catch-all, are left as they are. See <see cref="ViaductServerOptions.UseRouteTypeConstraints"/>.
        /// </summary>
        /// <param name="constraints">
        /// The constraint per parameter name, as the dictionary's comparer matches names; the generated mappings ignore case,
        /// as their route parameter names do.
        /// </param>
        public static string WithTypeConstraints([StringSyntax(ViaductFunctions.RouteSyntax)] string template, IReadOnlyDictionary<string, string> constraints)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(constraints);

            if (constraints.Count == 0 || template.IndexOf('{') < 0)
                return template;

            return RouteParameterRegex().Replace(template, match =>
            {
                var rest = match.Groups["rest"].Value;
                if (match.Groups["catchAll"].Length > 0 || rest.StartsWith(':')
                    || !constraints.TryGetValue(match.Groups["name"].Value, out var constraint))
                    return match.Value;

                return "{" + match.Groups["name"].Value + ":" + constraint + rest + "}";
            });
        }

        /// <summary>
        /// Gets the names of any predefined route parameters
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static IEnumerable<string> GetRouteParameters(string path)
        {
            var matches = GetRouteParametersRegex().Matches(path);
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                yield return m.Groups["name"].Value;
            }
        }
    }

    /// <summary>
    /// Stores endpoint mappers registered during service registration to be applied to an application's
    /// IEndpointRouteBuilder later.
    /// </summary>
    /// <remarks>
    /// Intended for use from generated code. <see cref="Push"/> queues a mapper in the service collection it registers on;
    /// AddRegisteredEndpoints maps that application's mappers, in the order they were registered, and each once.
    /// <para>
    /// Kept in the service collection rather than in a static, so that two applications in one process (test hosts, say)
    /// each map their own endpoints: with one queue for the process, the first to map took every application's endpoints
    /// and the rest got none.
    /// </para>
    /// </remarks>
    public static class PendingMappers
    {
        internal sealed record PendingMapper(ViaductEndpointMapper Mapper, ViaductServerOptions Options);

        /// <summary>The mappers one service collection registered, kept in it as a singleton.</summary>
        internal sealed class Registrations
        {
            // A queue: mapped in the order they were registered, and each taken once.
            readonly ConcurrentQueue<PendingMapper> pending = new();

            internal void Add(PendingMapper mapper) => pending.Enqueue(mapper);

            /// <summary>The next mapper not mapped yet, in the order they were registered; it is not handed out again.</summary>
            internal bool TryTake([NotNullWhen(true)] out PendingMapper? mapper) => pending.TryDequeue(out mapper);

            internal int Count => pending.Count;
        }

        /// <summary>
        /// Maps Viaduct server endpoints onto an IEndpointRouteBuilder.
        /// </summary>
        /// <remarks>Invoked during application startup when configuring routing for the Viaduct
        /// server.</remarks>
        /// <param name="builder">The endpoint route builder used to configure request endpoints.</param>
        /// <param name="options">ViaductServerOptions that influence how endpoints are mapped.</param>
        public delegate void ViaductEndpointMapper(IEndpointRouteBuilder builder, ViaductServerOptions options);

        /// <summary>
        /// Meant to be used from the generated code. Adds the mappings of an interface to <paramref name="services"/> during
        /// the service registration phase, to be added to the web app built from them later.
        /// </summary>
        public static void Push(IServiceCollection services, ViaductEndpointMapper mapper, Action<ViaductServerOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(mapper);
            RegistrationsOf(services).Add(new PendingMapper(mapper, configure.GetOptionsInstance()));
        }

        /// <summary>The number of mappers registered on <paramref name="services"/> and not mapped yet.</summary>
        public static int Count(IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            return Find(services)?.Count ?? 0;
        }

        static Registrations RegistrationsOf(IServiceCollection services)
        {
            if (Find(services) is { } existing)
                return existing;

            var created = new Registrations();
            services.AddSingleton(created);
            return created;
        }

        static Registrations? Find(IServiceCollection services)
        {
            foreach (var descriptor in services)
            {
                // A keyed descriptor throws when asked for its ImplementationInstance; ours is never keyed.
                if (descriptor.ServiceType == typeof(Registrations) && !descriptor.IsKeyedService
                    && descriptor.ImplementationInstance is Registrations registrations)
                    return registrations;
            }
            return null;
        }

        internal static IEndpointRouteBuilder AddRegisteredEndpoints(IEndpointRouteBuilder app)
        {
            if (app.ServiceProvider.GetService<Registrations>() is not { } registrations)
                return app;

            while (registrations.TryTake(out var mapping))
                mapping.Mapper(app, mapping.Options);
            return app;
        }
    }


}
