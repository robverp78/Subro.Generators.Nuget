using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using System;

namespace Viaduct.Signals
{
    /// <summary>Choosing PostgreSQL for signals.</summary>
    public static class PostgresSignalRegistration
    {
        /// <summary>
        /// Keeps signals in PostgreSQL and tells every server over <c>LISTEN</c>/<c>NOTIFY</c>, for several servers. The
        /// store and the bus are registered separately, so either can be replaced afterwards.
        /// </summary>
        public static SignalServerBuilder UsePostgres(this SignalServerBuilder builder, string connectionString, Action<PostgresSignalOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(connectionString);
            return builder.UsePostgres(_ => (NpgsqlDataSource.Create(connectionString), true), configure);
        }

        /// <summary>As <see cref="UsePostgres(SignalServerBuilder, string, Action{PostgresSignalOptions}?)"/>, on the application's own data source.</summary>
        public static SignalServerBuilder UsePostgres(this SignalServerBuilder builder, NpgsqlDataSource dataSource, Action<PostgresSignalOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(dataSource);
            return builder.UsePostgres(_ => (dataSource, false), configure);
        }

        static SignalServerBuilder UsePostgres(this SignalServerBuilder builder, Func<IServiceProvider, (NpgsqlDataSource Source, bool Owned)> source,
            Action<PostgresSignalOptions>? configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var options = new PostgresSignalOptions();
            configure?.Invoke(options);

            builder.Services.TryAddSingleton(provider =>
            {
                var (dataSource, owned) = source(provider);
                return new PostgresSignalDatabase(dataSource, options, owned);
            });
            builder.UseStore<PostgresSignalStore>();
            builder.UseBus<PostgresSignalBus>();
            return builder;
        }
    }
}
