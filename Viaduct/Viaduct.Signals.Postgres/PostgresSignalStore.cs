using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// Keeps signals in a PostgreSQL table, for several servers and across restarts. Cursors follow commit order; see
    /// <see cref="PostgresSignalDatabase"/>.
    /// </summary>
    public sealed class PostgresSignalStore(PostgresSignalDatabase database) : ISignalStore
    {
        /// <inheritdoc/>
        public async ValueTask<SignalEnvelope> AppendAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(signal);
            await database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            await using var command = database.DataSource.CreateCommand();
            return await AppendAsync(database, command, signal, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Stores a signal with <paramref name="command"/>: on its own connection, or in a transaction it was given.</summary>
        internal static async ValueTask<SignalEnvelope> AppendAsync(PostgresSignalDatabase database, NpgsqlCommand command, SignalEnvelope signal,
            CancellationToken cancellationToken)
        {
            // The row's place in its transaction: the rows this transaction wrote before, plus one.
            command.CommandText = $"""
                WITH added AS (
                    INSERT INTO {database.Table} (type, topic, at, data) VALUES (@type, @topic, @at, @data) RETURNING xid, id
                )
                SELECT added.xid, (SELECT count(*) FROM {database.Table} s WHERE s.xid = added.xid AND s.id < added.id) + 1 FROM added
                """;
            command.Parameters.AddWithValue("type", signal.Type);
            command.Parameters.AddWithValue("topic", signal.Topic);
            command.Parameters.AddWithValue("at", signal.At.ToUniversalTime());
            command.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Json) { Value = (object?)signal.Data ?? DBNull.Value });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return signal with { Id = SignalPosition.Of(reader.GetInt64(0), reader.GetInt64(1)), Ephemeral = false };
        }

        /// <inheritdoc/>
        public async ValueTask<SignalBatch> ReadAsync(long after, IReadOnlySet<string>? topics, int max, CancellationToken cancellationToken = default)
        {
            if (max <= 0)
                throw new ArgumentOutOfRangeException(nameof(max), max, "Read at least one signal.");
            await database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

            await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var (xmin, xmax, floor) = await BoundsAsync(connection, cancellationToken).ConfigureAwait(false);
            var current = SignalPosition.Before(xmin);

            // Gone after this cursor, or a cursor this database never gave.
            if (after < floor || SignalPosition.Xid(after) >= xmax)
                return new SignalBatch([], current, Reset: true);

            if (topics is { Count: 0 })
                return new SignalBatch([], Math.Max(after, current));

            await using var command = new NpgsqlCommand($"""
                SELECT xid, ordinal, type, topic, at, data FROM (
                    SELECT xid, id, type, topic, at, data::text AS data,
                           row_number() OVER (PARTITION BY xid ORDER BY id) AS ordinal
                    FROM {database.Table}
                    WHERE xid >= @afterXid AND xid < @xmin
                ) s
                WHERE (xid > @afterXid OR ordinal > @afterOrdinal) AND (@all OR topic = ANY(@topics))
                ORDER BY xid, id
                LIMIT @limit
                """, connection);
            command.Parameters.AddWithValue("afterXid", SignalPosition.Xid(after));
            command.Parameters.AddWithValue("afterOrdinal", SignalPosition.Ordinal(after));
            command.Parameters.AddWithValue("xmin", xmin);
            command.Parameters.AddWithValue("all", topics is null);
            command.Parameters.Add(new NpgsqlParameter("topics", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = topics?.ToArray() ?? [] });
            command.Parameters.AddWithValue("limit", max + 1);

            var signals = new List<SignalEnvelope>();
            var more = false;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (signals.Count == max)
                    {
                        more = true;
                        break;
                    }
                    signals.Add(new SignalEnvelope(reader.GetString(2), reader.GetString(3), reader.IsDBNull(5) ? null : reader.GetString(5))
                    {
                        Id = SignalPosition.Of(reader.GetInt64(0), reader.GetInt64(1)),
                        At = new DateTimeOffset(reader.GetFieldValue<DateTime>(4), TimeSpan.Zero),
                    });
                }
            }

            var cursor = more ? signals[^1].Id : Math.Max(after, current);
            return new SignalBatch(signals, cursor, More: more);
        }

        /// <inheritdoc/>
        public async ValueTask<long> GetCursorAsync(CancellationToken cancellationToken = default)
        {
            await database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var (xmin, _, _) = await BoundsAsync(connection, cancellationToken).ConfigureAwait(false);
            return SignalPosition.Before(xmin);
        }

        /// <inheritdoc/>
        /// <remarks>Removes whole transactions, so a cursor is never left inside one that is partly gone.</remarks>
        public async ValueTask<int> RemoveBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
        {
            await database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            await using var command = database.DataSource.CreateCommand($"""
                WITH gone AS (
                    DELETE FROM {database.Table}
                    WHERE xid IN (SELECT xid FROM {database.Table} GROUP BY xid HAVING max(at) < @before)
                      AND xid < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
                    RETURNING xid
                ),
                last AS (
                    SELECT xid, count(*) AS rows FROM gone GROUP BY xid ORDER BY xid DESC LIMIT 1
                ),
                floor AS (
                    UPDATE {database.FloorTable}
                    SET position = GREATEST(position, (SELECT (xid << {SignalPosition.OrdinalBits}) | rows FROM last))
                    WHERE EXISTS (SELECT 1 FROM last)
                    RETURNING position
                )
                SELECT count(*) FROM gone
                """);
            command.Parameters.AddWithValue("before", before.ToUniversalTime());
            var removed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt32(removed);
        }

        /// <summary>The oldest transaction still running, the next to start, and the highest cursor removed.</summary>
        async Task<(long Xmin, long Xmax, long Floor)> BoundsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand($"""
                SELECT pg_snapshot_xmin(pg_current_snapshot())::text::bigint,
                       pg_snapshot_xmax(pg_current_snapshot())::text::bigint,
                       coalesce((SELECT position FROM {database.FloorTable}), 0)
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }
    }
}
