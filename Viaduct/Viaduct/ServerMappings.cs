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
                options.SerializerOptions.TypeInfoResolverChain.Add(new Json.ViaductGlobalJsonTypeInfoForwarder());
            });
            return services;
        }
        /// <summary>
        /// Dummy class to check if the json services have been added to the specific services collection before
        /// </summary>
        sealed class ViaductJsonTypesMarker { }       
        


        /// <summary>
        /// An optional way to Register the endpoints instead of adding them directly (<see cref="AddEndpoints"/>) or via the Add...AndMap functions. 
        /// This allows registation for later mapping. This is mainly meant for 
        /// NB: <see cref="AddRegisteredEndpoints"/> must be called on the app/route builder to actually create the mappings for endpoints registered this way.
        /// </summary>            
        /// <param name="configure">optional configuration for the endpoints, such as base path and authorization requirements.</param>        
        public static void RegisterEndpoints<TInterface>(Action<ViaductServerOptions>? configure = null)
        {
            throw new ViaductMethodNotInterceptedException();
        }




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
    /// <remarks>Intended for use from generated code. Push queues a ViaductEndpointMapper with optional
    /// ViaductServerOptions during service registration; AddRegisteredEndpoints applies queued mappers when building
    /// the web application. Thread-safe and uses a LIFO stack. The Count property exposes the current number of pending
    /// mappers.</remarks>
    public static class PendingMappers
    {
        internal record PendingMapper(ViaductEndpointMapper Mapper, ViaductServerOptions Options);

        static readonly ConcurrentStack<PendingMapper> stack = [];

        /// <summary>
        /// Maps Viaduct server endpoints onto an IEndpointRouteBuilder.
        /// </summary>
        /// <remarks>Invoked during application startup when configuring routing for the Viaduct
        /// server.</remarks>
        /// <param name="builder">The endpoint route builder used to configure request endpoints.</param>
        /// <param name="options">ViaductServerOptions that influence how endpoints are mapped.</param>
        public delegate void ViaductEndpointMapper(IEndpointRouteBuilder builder, ViaductServerOptions options);

        /// <summary>
        /// Meant to be used from the generated code. Add mappings of an interface during service registration phase, to be added to the web app later.
        /// </summary>        
        public static void Push(ViaductEndpointMapper mapper, Action<ViaductServerOptions>? configure = null)
        {
            stack.Push(new PendingMapper(mapper, configure.GetOptionsInstance()));
        }

        internal static IEndpointRouteBuilder AddRegisteredEndpoints(IEndpointRouteBuilder app)
        {
            while (stack.TryPop(out var mapping))
                mapping!.Mapper(app, mapping.Options);
            return app;
        }

        /// <summary>
        /// The number of pending mappers
        /// </summary>
        public static int Count => stack.Count;


    }


}
