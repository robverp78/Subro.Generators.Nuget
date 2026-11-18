using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>Registering signals on a server.</summary>
    public static class SignalServerRegistration
    {
        /// <summary>
        /// Adds the signal engine: publisher, fan-out and retention. The store and bus are in-memory and in-process unless
        /// <paramref name="configure"/> chooses others, such as Postgres for several servers.
        /// </summary>
        public static IServiceCollection AddSignals(this IServiceCollection services, Action<SignalServerBuilder>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddOptions<SignalOptions>();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<ISignalPublisher, SignalPublisher>();
            services.TryAddSingleton<SignalFanOut>();
            services.TryAddSingleton<SignalEndpointHandler>();
            services.AddHostedService<SignalRetentionService>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<ISignalTopicResolver, UserSignalTopicResolver>());
            services.AddSignalPublisher<IViaductSignals>();

            configure?.Invoke(new SignalServerBuilder(services));

            services.TryAddSingleton<ISignalStore, InMemorySignalStore>();
            services.TryAddSingleton<ISignalBus, InProcessSignalBus>();
            return services;
        }

        /// <summary>
        /// Registers the publishing implementation of the contract <typeparamref name="T"/>: code injects
        /// <typeparamref name="T"/> and calls it after saving what changed.
        /// </summary>
        /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not marked <see cref="SignalsAttribute"/>.</exception>
        public static IServiceCollection AddSignalPublisher<T>(this IServiceCollection services) where T : class
        {
            ArgumentNullException.ThrowIfNull(services);
            var contract = SignalContracts.Get<T>();
            services.TryAddSingleton(provider => contract.CreatePublisher(provider.GetRequiredService<ISignalPublisher>()));
            return services;
        }
    }

    /// <summary>Chooses the parts of the signal engine on a server.</summary>
    public sealed class SignalServerBuilder(IServiceCollection services)
    {
        /// <summary>The services being registered.</summary>
        public IServiceCollection Services { get; } = services;

        /// <summary>Keeps signals in this process: development, tests, a single server.</summary>
        public SignalServerBuilder UseInMemory()
        {
            Services.Replace(ServiceDescriptor.Singleton<ISignalStore, InMemorySignalStore>());
            Services.Replace(ServiceDescriptor.Singleton<ISignalBus, InProcessSignalBus>());
            return this;
        }

        /// <summary>Keeps signals in <typeparamref name="T"/>.</summary>
        public SignalServerBuilder UseStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, ISignalStore
        {
            Services.Replace(ServiceDescriptor.Singleton<ISignalStore, T>());
            return this;
        }

        /// <summary>Carries signals between servers with <typeparamref name="T"/>.</summary>
        public SignalServerBuilder UseBus<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, ISignalBus
        {
            Services.Replace(ServiceDescriptor.Singleton<ISignalBus, T>());
            return this;
        }

        /// <summary>Sets the options in code; configuration binding works as well.</summary>
        public SignalServerBuilder Configure(Action<SignalOptions> configure)
        {
            Services.Configure(configure);
            return this;
        }

        /// <summary>Adds base topics from <typeparamref name="T"/>, scoped, besides the user's own topic.</summary>
        public SignalServerBuilder AddTopicResolver<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
            where T : class, ISignalTopicResolver
        {
            Services.TryAddEnumerable(ServiceDescriptor.Scoped<ISignalTopicResolver, T>());
            return this;
        }

        /// <summary>Adds an access rule for page topics, scoped.</summary>
        public SignalServerBuilder AddTopicAccess<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
            where T : class, ISignalTopicAccess
        {
            Services.TryAddEnumerable(ServiceDescriptor.Scoped<ISignalTopicAccess, T>());
            return this;
        }

        /// <summary>
        /// Lets a page ask for topics of <paramref name="template"/> when <paramref name="canListen"/> says so, such as
        /// <c>ChatSignalsContract.Instance.MessagesArrived.Topic</c>.
        /// </summary>
        public SignalServerBuilder AllowTopic(SignalTopicTemplate template, Func<ClaimsPrincipal, string, CancellationToken, ValueTask<bool>> canListen)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(canListen);
            Services.AddSingleton<ISignalTopicAccess>(new DelegateSignalTopicAccess(template, canListen));
            return this;
        }

        /// <summary>Lets a page ask for topics of <paramref name="template"/> when <paramref name="canListen"/> says so.</summary>
        public SignalServerBuilder AllowTopic(string template, Func<ClaimsPrincipal, string, CancellationToken, ValueTask<bool>> canListen)
            => AllowTopic(SignalTopicTemplate.Parse(template), canListen);

        /// <summary>
        /// Has a stream work out its user's topics again, and check its page topics again, after <paramref name="type"/>
        /// reaches it, as after <see cref="IViaductSignals.TopicsChangedAsync"/>: for an application's own signal that what
        /// someone may hear changed, such as their roles. See <see cref="SignalOptions.ResolveTopicsAgainOn"/>.
        /// </summary>
        public SignalServerBuilder ResolveTopicsAgainOn(SignalTypeInfo type)
        {
            ArgumentNullException.ThrowIfNull(type);
            Services.Configure<SignalOptions>(options => options.ResolveTopicsAgainOn.Add(type.Key));
            return this;
        }
    }

    /// <summary>
    /// Removes signals older than <see cref="SignalOptions.Retention"/>, which is read at every run, so a change applies
    /// without a restart. Runs every 24th of the retention, at least every minute and at most every hour.
    /// </summary>
    public sealed class SignalRetentionService(ISignalStore store, IOptionsMonitor<SignalOptions> options, TimeProvider time,
        ILogger<SignalRetentionService>? logger = null) : BackgroundService
    {
        readonly ILogger logger = logger ?? NullLogger<SignalRetentionService>.Instance;

        /// <summary>Removes what is older than the retention now; returns how many.</summary>
        public ValueTask<int> CleanAsync(CancellationToken cancellationToken = default)
            => store.RemoveBeforeAsync(time.GetUtcNow() - options.CurrentValue.Retention, cancellationToken);

        /// <summary>How long between runs for a retention.</summary>
        public static TimeSpan IntervalFor(TimeSpan retention)
        {
            var interval = TimeSpan.FromTicks(retention.Ticks / 24);
            return interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1)
                : interval > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1)
                : interval;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var removed = await CleanAsync(stoppingToken).ConfigureAwait(false);
                    if (removed > 0)
                        logger.LogDebug("Removed {Count} signals older than the retention.", removed);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Could not remove old signals.");
                }

                try
                {
                    await Task.Delay(IntervalFor(options.CurrentValue.Retention), time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
