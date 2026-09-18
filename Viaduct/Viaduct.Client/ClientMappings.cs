using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Viaduct.Client
{


    /// <summary>
    /// Main entry point for registering client side proxies for interfaces.
    /// </summary>
    public static class ClientMappings
    {
        /// <summary>
        /// Registers an <see cref="HttpClient"/>-backed proxy for <typeparamref name="TInterface"/>, generated
        /// from the interface, and registers it as that interface. Inject the interface and call its methods;
        /// the proxy turns each call into the HTTP request the server's matching endpoint expects.
        /// </summary>
        /// <remarks>
        /// The call is replaced at compile time by generated code, so the interface must be one the generator
        /// can see. Reaching this body at runtime means generation or interception did not happen — see
        /// <see cref="ViaductMethodNotInterceptedException"/>.
        /// </remarks>
        /// <typeparam name="TInterface">The service interface, shared with the server.</typeparam>
        /// <param name="services">The service collection to register the client on.</param>
        /// <param name="configure">
        /// Options for this client: the base url to call, the paths to build, and whether calls carry an access
        /// token. Omitted, <see cref="ViaductClientOptions.Default"/> is copied.
        /// </param>
        /// <returns>The <see cref="IHttpClientBuilder"/>, so handlers and policies can be added as usual.</returns>
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.None)]
        public static IHttpClientBuilder CreateAndAddHttpClient<TInterface>(this IServiceCollection services, Action<ViaductClientOptions>? configure = null)
            where TInterface: class
        {
            throw new ViaductMethodNotInterceptedException();          
        }

        /// <summary>
        /// The options a <paramref name="configure"/> delegate describes: a copy of
        /// <see cref="ViaductClientOptions.Default"/> with the delegate applied. A copy, so later changes to the
        /// defaults leave an already-registered client alone.
        /// </summary>
        public static ViaductClientOptions GetOptionsInstance(this Action<ViaductClientOptions>? configure)
            => configure.GetOptionsInstance(ViaductClientOptions.Default);
    }
}
