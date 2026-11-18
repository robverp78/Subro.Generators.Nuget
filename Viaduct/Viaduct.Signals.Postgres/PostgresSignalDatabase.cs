using Npgsql;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>Where and how signals are kept in PostgreSQL.</summary>
    public sealed class PostgresSignalOptions
    {
        /// <summary>The schema of the tables. Default <c>public</c>.</summary>
        public string Schema { get; set; } = "public";

        /// <summary>The table of signals; the cursor's floor is kept in <c>{Table}_floor</c>. Default <c>viaduct_signals</c>.</summary>
        public string Table { get; set; } = "viaduct_signals";

        /// <summary>The <c>NOTIFY</c> channel the servers tell each other on. Default <c>viaduct_signals</c>.</summary>
        public string Channel { get; set; } = "viaduct_signals";

        /// <summary>
        /// Create the tables when they are missing, at first use. Default true; turn it off when a migration creates them
        /// (<see cref="PostgresSignalDatabase.CreateScript"/>).
        /// </summary>
        public bool CreateSchema { get; set; } = true;

        /// <summary>
        /// How often each server reads the store without being told, for signals held back by a transaction that was still
        /// open, and for notifications missed while its listening connection was down. Default 2 seconds.
        /// </summary>
        public TimeSpan SafetyPollInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>How long to wait before listening again after the connection was lost. Default 5 seconds.</summary>
        public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// The PostgreSQL side of signals: the data source, the names, and the tables. Shared by the store and the bus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A signal's cursor is not its row id: ids are taken when rows are written, but transactions commit in another order,
    /// so a reader moving past id 101 could later find 100 committed. The cursor is the writing transaction's id
    /// (<c>xid8</c>) and the row's place within that transaction; a reader only reads transactions older than the oldest
    /// one still running, which can never get rows before them any more. A transaction left open therefore holds back the
    /// signals of every transaction after it until it ends, and as transaction ids are the server's, that includes a
    /// transaction in another database on the same server: give signals a server of the application's own, and keep
    /// transactions short.
    /// </para>
    /// <para>
    /// Needs PostgreSQL 13 or later, and for <c>LISTEN</c> a direct connection, not one through a pooler in transaction
    /// mode. A transaction may write at most 1,048,575 signals.
    /// </para>
    /// </remarks>
    public sealed class PostgresSignalDatabase : IAsyncDisposable
    {
        static readonly Regex Identifier = new("^[a-z_][a-z0-9_]{0,50}$", RegexOptions.CultureInvariant);

        readonly bool ownsDataSource;
        readonly object gate = new();
        Task? created;

        /// <summary>The database on <paramref name="dataSource"/>; disposing this disposes it only when <paramref name="owns"/>.</summary>
        public PostgresSignalDatabase(NpgsqlDataSource dataSource, PostgresSignalOptions options, bool owns = false)
        {
            ArgumentNullException.ThrowIfNull(dataSource);
            ArgumentNullException.ThrowIfNull(options);
            foreach (var name in new[] { options.Schema, options.Table, options.Channel })
                if (!Identifier.IsMatch(name))
                    throw new ArgumentException($"'{name}' is not a plain lower-case PostgreSQL name.", nameof(options));

            DataSource = dataSource;
            Options = options;
            ownsDataSource = owns;
            Table = $"\"{options.Schema}\".\"{options.Table}\"";
            FloorTable = $"\"{options.Schema}\".\"{options.Table}_floor\"";
        }

        /// <summary>Where connections come from.</summary>
        public NpgsqlDataSource DataSource { get; }

        /// <summary>The settings.</summary>
        public PostgresSignalOptions Options { get; }

        /// <summary>The signal table, quoted.</summary>
        public string Table { get; }

        /// <summary>The table holding the cursor's floor, quoted.</summary>
        public string FloorTable { get; }

        /// <summary>The script that creates the tables when missing, for a migration of the application's own.</summary>
        public static string CreateScript(PostgresSignalOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var table = $"\"{options.Schema}\".\"{options.Table}\"";
            return $"""
                CREATE SCHEMA IF NOT EXISTS "{options.Schema}";
                CREATE TABLE IF NOT EXISTS {table} (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    xid bigint NOT NULL DEFAULT (pg_current_xact_id()::text::bigint),
                    type text NOT NULL,
                    topic text NOT NULL,
                    at timestamptz NOT NULL,
                    data json NULL
                );
                CREATE INDEX IF NOT EXISTS "{options.Table}_xid" ON {table} (xid, id);
                CREATE INDEX IF NOT EXISTS "{options.Table}_at" ON {table} (at);
                CREATE TABLE IF NOT EXISTS "{options.Schema}"."{options.Table}_floor" (
                    one boolean PRIMARY KEY DEFAULT true CHECK (one),
                    position bigint NOT NULL
                );
                INSERT INTO "{options.Schema}"."{options.Table}_floor" (one, position) VALUES (true, 0) ON CONFLICT DO NOTHING;
                """;
        }

        /// <summary>
        /// A publisher that writes in <paramref name="transaction"/> on <paramref name="connection"/>, an open connection to
        /// this database: what it publishes is stored and sent when that transaction commits. See
        /// <see cref="PostgresTransactionSignals"/>.
        /// </summary>
        public PostgresTransactionSignals InTransaction(NpgsqlConnection connection, NpgsqlTransaction? transaction, SignalOptions options,
            TimeProvider? time = null)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(options);
            return new PostgresTransactionSignals(this, connection, transaction, options, time ?? TimeProvider.System);
        }

        /// <summary>Creates the tables once when <see cref="PostgresSignalOptions.CreateSchema"/> is on; tried again after a failure.</summary>
        public Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
        {
            if (!Options.CreateSchema)
                return Task.CompletedTask;
            lock (gate)
            {
                if (created is null || created.IsFaulted || created.IsCanceled)
                    created = CreateAsync();
                return created.WaitAsync(cancellationToken);
            }
        }

        async Task CreateAsync()
        {
            await using var command = DataSource.CreateCommand(CreateScript(Options));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>Disposes the data source when it was made for this.</summary>
        public async ValueTask DisposeAsync()
        {
            if (ownsDataSource)
                await DataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A cursor in PostgreSQL: the writing transaction's id in the high bits, the row's place within it (from 1) in the low
    /// 20 bits, so cursors follow commit order rather than the order rows were written.
    /// </summary>
    static class SignalPosition
    {
        public const int OrdinalBits = 20;
        public const long MaxOrdinal = (1L << OrdinalBits) - 1;

        public static long Of(long xid, long ordinal) => (xid << OrdinalBits) | Math.Min(ordinal, MaxOrdinal);

        public static long Xid(long position) => position >> OrdinalBits;

        public static long Ordinal(long position) => position & MaxOrdinal;

        /// <summary>Past every transaction before <paramref name="xmin"/>, the oldest still running.</summary>
        public static long Before(long xmin) => Of(xmin - 1, MaxOrdinal);
    }
}
