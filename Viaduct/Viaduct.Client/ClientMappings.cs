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
        /// Starts client registrations that share <paramref name="configure"/> and <paramref name="client"/>: each
        /// <see cref="ViaductClientChain.Add{TInterface}"/> on the chain is given these, then its own.
        /// </summary>
        /// <remarks>
        /// The options are the chain's alone, where a change to <see cref="ViaductClientOptions.Default"/> would reach every
        /// client registered after it in the process.
        /// </remarks>
        /// <example>
        /// <code>
        /// services.ViaductClients(options => options.BaseUrl = apiUrl, client => client.AddHttpMessageHandler&lt;AppHeaderHandler&gt;())
        ///     .Add&lt;IGroupService&gt;()
        ///     .Add&lt;IMemberService&gt;();
        /// </code>
        /// </example>
        /// <param name="services">The service collection to register the clients on.</param>
        /// <param name="configure">The options the chain's clients share, such as the base url.</param>
        /// <param name="client">What each client's <see cref="IHttpClientBuilder"/> is given, such as message handlers.</param>
        public static ViaductClientChain ViaductClients(this IServiceCollection services, Action<ViaductClientOptions>? configure = null,
            Action<IHttpClientBuilder>? client = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            return new(services, configure, client);
        }

        /// <summary>
        /// The options a <paramref name="configure"/> delegate describes: a copy of
        /// <see cref="ViaductClientOptions.Default"/> with the delegate applied. A copy, so later changes to the
        /// defaults leave an already-registered client alone.
        /// </summary>
        public static ViaductClientOptions GetOptionsInstance(this Action<ViaductClientOptions>? configure)
            => configure.GetOptionsInstance(ViaductClientOptions.Default);
    }

    /// <summary>
    /// Client registrations that share options and what their <see cref="IHttpClientBuilder"/> is given, started by
    /// <see cref="ClientMappings.ViaductClients"/>.
    /// </summary>
    /// <remarks>
    /// A client on the chain starts from <see cref="ViaductClientOptions.Default"/>, then takes what the interface's
    /// <see cref="ViaductRoutesAttribute"/> says, then the chain's options, then its own <c>configure</c>. Its builder is
    /// given the chain's <c>client</c> first, then its own. As <see cref="ClientMappings.CreateAndAddHttpClient{TInterface}"/>,
    /// each <see cref="Add{TInterface}"/> is replaced at compile time by generated code.
    /// </remarks>
    public sealed class ViaductClientChain
    {
        readonly Action<ViaductClientOptions>? shared;
        readonly Action<IHttpClientBuilder>? sharedClient;

        internal ViaductClientChain(IServiceCollection services, Action<ViaductClientOptions>? shared, Action<IHttpClientBuilder>? sharedClient)
        {
            Services = services;
            this.shared = shared;
            this.sharedClient = sharedClient;
        }

        /// <summary>The service collection the chain registers on, to go on with other registrations.</summary>
        public IServiceCollection Services { get; }

        /// <summary>
        /// Registers an <see cref="HttpClient"/>-backed proxy for <typeparamref name="TInterface"/>, as
        /// <see cref="ClientMappings.CreateAndAddHttpClient{TInterface}"/> does, with the chain's options and builder setup.
        /// </summary>
        /// <param name="configure">This client's own options, after the chain's.</param>
        /// <param name="client">What this client's <see cref="IHttpClientBuilder"/> is given, after the chain's.</param>
        /// <returns>The chain, for the next client.</returns>
        public ViaductClientChain Add<TInterface>(Action<ViaductClientOptions>? configure = null, Action<IHttpClientBuilder>? client = null)
            where TInterface : class
            => throw new ViaductMethodNotInterceptedException();

        /// <summary>
        /// The chain's options, then <paramref name="then"/>: what a client on the chain is configured with. Used by the
        /// generated code.
        /// </summary>
        public Action<ViaductClientOptions>? With(Action<ViaductClientOptions>? then)
            => shared is null ? then
             : then is null ? shared
             : options => { shared(options); then(options); };

        /// <summary>
        /// Gives a client's <paramref name="builder"/> what the chain gives every client, then <paramref name="then"/>. Used by
        /// the generated code.
        /// </summary>
        public ViaductClientChain ConfigureClient(IHttpClientBuilder builder, Action<IHttpClientBuilder>? then)
        {
            ArgumentNullException.ThrowIfNull(builder);
            sharedClient?.Invoke(builder);
            then?.Invoke(builder);
            return this;
        }
    }
}
