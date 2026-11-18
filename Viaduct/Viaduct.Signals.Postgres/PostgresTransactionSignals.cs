using Npgsql;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// Publishes in a transaction of the application's own: the signal is stored, and every server told, only when that
    /// transaction commits, and not at all when it rolls back. For a signal about a change saved in the same transaction,
    /// which then can neither go out before the change is there nor be lost after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Made with <see cref="PostgresSignalDatabase.InTransaction"/> for an open connection to the signals' database and its
    /// transaction (an EF Core context's: <c>Database.GetDbConnection()</c> and <c>CurrentTransaction.GetDbTransaction()</c>),
    /// and handed to a contract: <c>ChatSignalsContract.Instance.CreatePublisher(signals).MessagesArrivedAsync(…)</c>.
    /// </para>
    /// <para>
    /// The tables must exist already (<see cref="PostgresSignalOptions.CreateSchema"/> creates them at the store's first use,
    /// outside any transaction).
    /// </para>
    /// </remarks>
    public sealed class PostgresTransactionSignals : ISignalPublisher
    {
        readonly PostgresSignalDatabase database;
        readonly NpgsqlConnection connection;
        readonly NpgsqlTransaction? transaction;
        readonly SignalOptions options;
        readonly TimeProvider time;

        internal PostgresTransactionSignals(PostgresSignalDatabase database, NpgsqlConnection connection, NpgsqlTransaction? transaction,
            SignalOptions options, TimeProvider time)
        {
            this.database = database;
            this.connection = connection;
            this.transaction = transaction;
            this.options = options;
            this.time = time;
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">As <see cref="SignalPublisher.Prepare"/>, or an ephemeral signal too large for a notification.</exception>
        public async ValueTask PublishAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
        {
            var stamped = SignalPublisher.Prepare(signal, options, time);

            string payload = "";
            if (stamped.Ephemeral)
            {
                payload = SignalWire.ToJson(stamped);
                if (Encoding.UTF8.GetByteCount(payload) > PostgresSignalBus.MaxNotificationBytes)
                    throw new ArgumentException($"Ephemeral signal '{stamped.Type}' is larger than a notification can carry.", nameof(signal));
            }
            else
            {
                await using var append = new NpgsqlCommand(null, connection, transaction);
                await PostgresSignalStore.AppendAsync(database, append, stamped, cancellationToken).ConfigureAwait(false);
            }

            // Sent by PostgreSQL when the transaction commits, and dropped when it rolls back.
            await using var notify = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection, transaction);
            notify.Parameters.AddWithValue("channel", database.Options.Channel);
            notify.Parameters.AddWithValue("payload", payload);
            await notify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
