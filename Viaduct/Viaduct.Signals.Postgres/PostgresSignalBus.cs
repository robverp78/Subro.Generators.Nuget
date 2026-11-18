using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// Tells every server of an application over PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c>: an empty notification for stored
    /// signals, the signal itself for an ephemeral one. Each server keeps one connection listening, reads the store every
    /// <see cref="PostgresSignalOptions.SafetyPollInterval"/> as well, and after listening again.
    /// </summary>
    public sealed class PostgresSignalBus : ISignalBus, IAsyncDisposable
    {
        /// <summary>The most a notification may carry, in UTF-8 bytes; PostgreSQL allows just under 8,000.</summary>
        public const int MaxNotificationBytes = 7_999;

        readonly PostgresSignalDatabase database;
        readonly ILogger logger;
        readonly object gate = new();
        ISignalBusSubscriber[] subscribers = [];
        CancellationTokenSource? stopping;
        Task? listening;

        /// <summary>The bus over <paramref name="database"/>.</summary>
        public PostgresSignalBus(PostgresSignalDatabase database, ILogger<PostgresSignalBus>? logger = null)
        {
            this.database = database;
            this.logger = logger ?? NullLogger<PostgresSignalBus>.Instance;
        }

        /// <summary>Whether the listening connection is open now.</summary>
        public bool IsListening { get; private set; }

        /// <inheritdoc/>
        public ValueTask AnnounceAsync(CancellationToken cancellationToken = default) => NotifyAsync("", cancellationToken);

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">The signal is larger than <see cref="MaxNotificationBytes"/>.</exception>
        public ValueTask SendEphemeralAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(signal);
            var payload = SignalWire.ToJson(signal);
            if (Encoding.UTF8.GetByteCount(payload) > MaxNotificationBytes)
                throw new ArgumentException($"Ephemeral signal '{signal.Type}' is larger than a notification can carry ({MaxNotificationBytes} bytes).", nameof(signal));
            return NotifyAsync(payload, cancellationToken);
        }

        async ValueTask NotifyAsync(string payload, CancellationToken cancellationToken)
        {
            await using var command = database.DataSource.CreateCommand("SELECT pg_notify(@channel, @payload)");
            command.Parameters.AddWithValue("channel", database.Options.Channel);
            command.Parameters.AddWithValue("payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public IDisposable Subscribe(ISignalBusSubscriber subscriber)
        {
            ArgumentNullException.ThrowIfNull(subscriber);
            lock (gate)
            {
                subscribers = [.. subscribers, subscriber];
                if (listening is null)
                {
                    stopping = new CancellationTokenSource();
                    var token = stopping.Token;
                    listening = Task.Run(() => ListenAsync(token));
                }
            }
            return new Unsubscriber(this, subscriber);
        }

        async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var arrived = new List<string>();
                    connection.Notification += (_, notification) =>
                    {
                        lock (arrived)
                            arrived.Add(notification.Payload);
                    };

                    await using (var listen = new NpgsqlCommand($"LISTEN \"{database.Options.Channel}\"", connection))
                        await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    IsListening = true;

                    // Whatever was announced while not listening.
                    await StoredAsync(cancellationToken).ConfigureAwait(false);

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var notified = await connection.WaitAsync(database.Options.SafetyPollInterval, cancellationToken).ConfigureAwait(false);

                        string[] payloads;
                        lock (arrived)
                        {
                            payloads = [.. arrived];
                            arrived.Clear();
                        }

                        if (!notified || Array.IndexOf(payloads, "") >= 0)
                            await StoredAsync(cancellationToken).ConfigureAwait(false);
                        foreach (var payload in payloads)
                            if (payload.Length > 0)
                                await EphemeralAsync(payload, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    IsListening = false;
                    logger.LogWarning(exception, "Listening for signals on '{Channel}' failed; listening again in {Delay}.",
                        database.Options.Channel, database.Options.ReconnectDelay);
                    try
                    {
                        await Task.Delay(database.Options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            IsListening = false;
        }

        async Task StoredAsync(CancellationToken cancellationToken)
        {
            foreach (var subscriber in Volatile.Read(ref subscribers))
                await subscriber.OnStoredAsync(cancellationToken).ConfigureAwait(false);
        }

        async Task EphemeralAsync(string payload, CancellationToken cancellationToken)
        {
            SignalEnvelope signal;
            try
            {
                signal = SignalWire.FromJson(payload);
            }
            catch (FormatException exception)
            {
                logger.LogWarning(exception, "Ignored a notification on '{Channel}' that is no signal.", database.Options.Channel);
                return;
            }

            foreach (var subscriber in Volatile.Read(ref subscribers))
                await subscriber.OnEphemeralAsync(signal, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Stops listening.</summary>
        public async ValueTask DisposeAsync()
        {
            Task? running;
            lock (gate)
            {
                stopping?.Cancel();
                running = listening;
                listening = null;
            }
            if (running is not null)
            {
                try
                {
                    await running.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            stopping?.Dispose();
        }

        sealed class Unsubscriber(PostgresSignalBus bus, ISignalBusSubscriber subscriber) : IDisposable
        {
            public void Dispose()
            {
                lock (bus.gate)
                    bus.subscribers = Array.FindAll(bus.subscribers, s => !ReferenceEquals(s, subscriber));
            }
        }
    }
}
