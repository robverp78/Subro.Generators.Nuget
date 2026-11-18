using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Viaduct.Signals
{
    /// <summary>Registering signals on a client.</summary>
    public static class SignalClientRegistration
    {
        /// <summary>
        /// Adds the client's side of signals: the <see cref="SignalDispatcher"/> (scoped: one per app, or per circuit on a
        /// Blazor server), which is also the <see cref="ISignalSubscriptions"/> pages use, and the delivery options.
        /// </summary>
        public static IServiceCollection AddSignalClient(this IServiceCollection services, Action<SignalClientOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var options = services.AddOptions<SignalClientOptions>();
            if (configure is not null)
                options.Configure(configure);

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddScoped<SignalClientEnvironment>();
            services.TryAddScoped<ISignalClientEnvironment>(static provider => provider.GetRequiredService<SignalClientEnvironment>());
            services.TryAddSingleton<ISignalDeliveryPolicy, ConfiguredSignalDeliveryPolicy>();
            services.TryAddScoped<SignalDispatcher>();
            services.TryAddScoped<ISignalSubscriptions>(static provider => provider.GetRequiredService<SignalDispatcher>());
            return services;
        }

        /// <summary>The name of the <see cref="System.Net.Http.HttpClient"/> the connections use.</summary>
        public const string HttpClientName = "Viaduct.Signals";

        /// <summary>
        /// Adds the connection to the server's signals: the <see cref="SignalClient"/> (scoped, like the dispatcher; call
        /// <see cref="ISignalClient.Start"/> once the app runs) over server-sent events and polling, with the client's side
        /// (<see cref="AddSignalClient"/>). Returns the <see cref="System.Net.Http.HttpClient"/>'s builder, for handlers such as
        /// <c>AddViaductAuthorization</c>; its timeout is off, as a stream stays open.
        /// </summary>
        public static IHttpClientBuilder AddSignalConnection(this IServiceCollection services, Action<System.Net.Http.HttpClient> configureClient,
            Action<SignalClientOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configureClient);

            services.AddSignalClient(configure);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ISignalConnection, ServerSentEventsSignalConnection>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ISignalConnection, PollingSignalConnection>());
            services.TryAddScoped<SignalClient>();
            services.TryAddScoped<ISignalClient>(static provider => provider.GetRequiredService<SignalClient>());

            return services.AddHttpClient(HttpClientName, client =>
            {
                client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                configureClient(client);
            });
        }

        /// <summary>
        /// Adds <typeparamref name="THandler"/> as the app-wide handler of the contract <typeparamref name="TContract"/>:
        /// each signal of it calls the matching method. Registered scoped unless registered already.
        /// </summary>
        /// <exception cref="InvalidOperationException"><typeparamref name="TContract"/> is not marked <see cref="SignalsAttribute"/>.</exception>
        public static IServiceCollection AddSignalHandler<TContract, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
            this IServiceCollection services)
            where TContract : class
            where THandler : class, TContract
        {
            ArgumentNullException.ThrowIfNull(services);
            var contract = SignalContracts.Get<TContract>();
            services.TryAddScoped<THandler>();
            services.AddSingleton(new SignalHandlerRegistration(contract, static provider => provider.GetRequiredService<THandler>()));
            return services;
        }
    }
}
