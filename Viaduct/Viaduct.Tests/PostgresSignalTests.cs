using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Viaduct.Signals;

namespace Viaduct.Tests
{
    /// <summary>
    /// The PostgreSQL store and bus, against a database of their own on the local server (<c>postgres</c>/<c>postgres</c>
    /// on localhost, as Subro.Base's suites use; <c>VIADUCT_TEST_POSTGRES</c> for another). Skipped without one.
    /// </summary>
    /// <remarks>
    /// One at a time: transaction ids are the server's, not the database's, so the test that holds a transaction open holds
    /// back the signals of every database on the server meanwhile.
    /// </remarks>
    [NotInParallel(nameof(PostgresSignalTests))]
    public class PostgresSignalTests
    {
        static readonly Guid Room = Guid.Parse("0198d3c0-1111-7222-8333-944445555666");
        static string RoomTopic => $"chat.room/{Room:D}";

        static string Server => Environment.GetEnvironmentVariable("VIADUCT_TEST_POSTGRES")
            ?? "Host=localhost;Username=postgres;Password=postgres;Timeout=3";

        /// <summary>A database made for one test and dropped after it.</summary>
        sealed class TestDatabase : IAsyncDisposable
        {
            public required string Name { get; init; }
            public required string ConnectionString { get; init; }
            public List<IAsyncDisposable> Owned { get; } = [];

            public static async Task<TestDatabase> CreateAsync()
            {
                var name = "viaduct_signals_test_" + Guid.NewGuid().ToString("N")[..12];
                try
                {
                    await using var maintenance = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Server) { Database = "postgres" }.ConnectionString);
                    await maintenance.OpenAsync();
                    await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", maintenance);
                    await create.ExecuteNonQueryAsync();
                }
                catch (Exception exception) when (exception is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
                {
                    Skip.Test("No PostgreSQL to test against: " + exception.Message);
                }
                return new TestDatabase { Name = name, ConnectionString = new NpgsqlConnectionStringBuilder(Server) { Database = name }.ConnectionString };
            }

            public PostgresSignalDatabase Database(Action<PostgresSignalOptions>? configure = null)
            {
                var options = new PostgresSignalOptions { SafetyPollInterval = TimeSpan.FromMilliseconds(200), ReconnectDelay = TimeSpan.FromMilliseconds(200) };
                configure?.Invoke(options);
                var database = new PostgresSignalDatabase(NpgsqlDataSource.Create(ConnectionString), options, owns: true);
                Owned.Add(database);
                return database;
            }

            public T Own<T>(T disposable) where T : IAsyncDisposable
            {
                Owned.Add(disposable);
                return disposable;
            }

            public async ValueTask DisposeAsync()
            {
                for (var i = Owned.Count - 1; i >= 0; i--)
                    await Owned[i].DisposeAsync();
                NpgsqlConnection.ClearAllPools();
                await using var maintenance = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Server) { Database = "postgres" }.ConnectionString);
                await maintenance.OpenAsync();
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", maintenance);
                await drop.ExecuteNonQueryAsync();
            }
        }

        static SignalEnvelope Signal(string topic, string? data = null, DateTimeOffset? at = null)
            => new("test.chat.MessagesArrived", topic, data) { At = at ?? DateTimeOffset.UtcNow };

        static HashSet<string> Topics(params string[] topics) => new(topics, StringComparer.Ordinal);

        static async Task WaitFor(Func<bool> condition, string what)
        {
            for (var i = 0; i < 500 && !condition(); i++)
                await Task.Delay(10);
            if (!condition())
                throw new TimeoutException("Waited in vain for: " + what);
        }

        [Test]
        public async Task TheStore_ReadsWhatCameAfterACursor_OnTheTopicsAskedFor()
        {
            await using var test = await TestDatabase.CreateAsync();
            var store = new PostgresSignalStore(test.Database());

            var start = await store.GetCursorAsync();
            await store.AppendAsync(Signal(RoomTopic, """{"count":1}"""));
            await store.AppendAsync(Signal("user/u1/inbox"));
            await store.AppendAsync(Signal(RoomTopic, """{"count":2}"""));

            var all = await store.ReadAsync(start, null, 10);
            var room = await store.ReadAsync(start, Topics(RoomTopic), 10);
            var paged = await store.ReadAsync(start, null, 2);
            var rest = await store.ReadAsync(paged.Cursor, null, 2);
            var nothingNew = await store.ReadAsync(all.Cursor, null, 10);

            await Assert.That(all.Signals.Select(static s => s.Topic)).IsEquivalentTo([RoomTopic, "user/u1/inbox", RoomTopic]);
            await Assert.That(all.Signals.Select(static s => s.Id)).IsOrderedBy(static id => id);
            await Assert.That(room.Signals.Select(static s => s.Data)).IsEquivalentTo(["""{"count":1}""", """{"count":2}"""]);
            await Assert.That(paged.More).IsTrue();
            await Assert.That(rest.Signals.Single().Data).IsEqualTo("""{"count":2}""");
            await Assert.That(nothingNew.Signals.Count).IsEqualTo(0);
            await Assert.That(nothingNew.Cursor).IsGreaterThanOrEqualTo(all.Cursor);
        }

        [Test]
        public async Task ASignalCommittedEarly_WaitsForAnOlderTransaction_SoNoneIsSkipped()
        {
            await using var test = await TestDatabase.CreateAsync();
            var database = test.Database();
            var store = new PostgresSignalStore(database);
            var start = await store.GetCursorAsync();

            // An older transaction writes a signal and stays open; a newer one commits its signal first.
            await using var older = await database.DataSource.OpenConnectionAsync();
            await using var transaction = await older.BeginTransactionAsync();
            await using (var insert = new NpgsqlCommand($"INSERT INTO {database.Table} (type, topic, at) VALUES ('test.chat.MessagesArrived', 'older', now())", older, transaction))
                await insert.ExecuteNonQueryAsync();
            await store.AppendAsync(Signal("newer"));

            var whileOpen = await store.ReadAsync(start, null, 10);
            await transaction.CommitAsync();
            var afterCommit = await store.ReadAsync(whileOpen.Cursor, null, 10);

            await Assert.That(whileOpen.Signals.Count).IsEqualTo(0);
            await Assert.That(afterCommit.Signals.Select(static s => s.Topic)).IsEquivalentTo(["older", "newer"]);
        }

        [Test]
        public async Task SignalsOfOneTransaction_KeepTheirOrder_AndAreReadTogether()
        {
            await using var test = await TestDatabase.CreateAsync();
            var database = test.Database();
            var store = new PostgresSignalStore(database);
            await store.GetCursorAsync();
            var start = await store.GetCursorAsync();

            await using (var connection = await database.DataSource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                foreach (var topic in new[] { "a", "b", "c" })
                {
                    await using var insert = new NpgsqlCommand($"INSERT INTO {database.Table} (type, topic, at) VALUES ('t.T', @topic, now())", connection, transaction);
                    insert.Parameters.AddWithValue("topic", topic);
                    await insert.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
            }

            var first = await store.ReadAsync(start, null, 2);
            var second = await store.ReadAsync(first.Cursor, null, 2);

            await Assert.That(first.Signals.Select(static s => s.Topic)).IsEquivalentTo(["a", "b"]);
            await Assert.That(second.Signals.Select(static s => s.Topic)).IsEquivalentTo(["c"]);
        }

        [Test]
        public async Task Removing_OldSignals_LeavesAResetForACursorBeforeThem()
        {
            await using var test = await TestDatabase.CreateAsync();
            var store = new PostgresSignalStore(test.Database());
            var start = await store.GetCursorAsync();
            var old = DateTimeOffset.UtcNow.AddHours(-30);

            await store.AppendAsync(Signal(RoomTopic, at: old));
            await store.AppendAsync(Signal(RoomTopic, at: old));
            await store.AppendAsync(Signal(RoomTopic));
            var removed = await store.RemoveBeforeAsync(DateTimeOffset.UtcNow.AddHours(-24));

            var fromBefore = await store.ReadAsync(start, null, 10);
            var fromNow = await store.ReadAsync(fromBefore.Cursor, null, 10);
            var foreign = await store.ReadAsync(long.MaxValue / 2, null, 10);

            await Assert.That(removed).IsEqualTo(2);
            await Assert.That(fromBefore.Reset).IsTrue();
            await Assert.That(fromNow.Reset).IsFalse();
            await Assert.That(foreign.Reset).IsTrue();
        }

        [Test]
        public async Task TheBus_TellsAnotherServer_OfStoredAndEphemeralSignals()
        {
            await using var test = await TestDatabase.CreateAsync();
            var one = test.Own(new PostgresSignalBus(test.Database()));
            var two = test.Own(new PostgresSignalBus(test.Database()));
            var heard = new Recorder();
            using var subscription = two.Subscribe(heard);
            await WaitFor(() => two.IsListening, "the second server listening");
            var storedBefore = heard.Stored;

            await one.AnnounceAsync();
            await one.SendEphemeralAsync(new SignalEnvelope("test.chat.Typing", RoomTopic, """{"name":"Bo"}""") { Ephemeral = true });

            await WaitFor(() => heard.Ephemeral.Count == 1 && heard.Stored > storedBefore, "both notifications");
            await Assert.That(heard.Ephemeral.Single().Data).IsEqualTo("""{"name":"Bo"}""");
            await Assert.That(() => one.SendEphemeralAsync(new SignalEnvelope("t.T", "x", $"\"{new string('x', 9000)}\"") { Ephemeral = true }).AsTask())
                .Throws<ArgumentException>();
        }

        [Test]
        public async Task TwoServers_OnOneDatabase_HearEachOthersSignals()
        {
            await using var test = await TestDatabase.CreateAsync();
            ServiceProvider Server()
            {
                var services = new ServiceCollection();
                services.AddSignals(signals => signals.UsePostgres(test.ConnectionString, o => o.SafetyPollInterval = TimeSpan.FromMilliseconds(200)));
                services.AddSignalPublisher<ITestChatSignals>();
                return test.Own(services.BuildServiceProvider());
            }
            var publishing = Server();
            var listening = Server();

            var fanOut = listening.GetRequiredService<SignalFanOut>();
            using var listener = await fanOut.ListenAsync(null, Topics(RoomTopic));
            await WaitFor(() => ((PostgresSignalBus)listening.GetRequiredService<ISignalBus>()).IsListening, "the second server listening");

            var chat = publishing.GetRequiredService<ITestChatSignals>();
            await chat.MessagesArrivedAsync(Room, new TestMessages(2, "Anna"));
            await chat.TypingAsync(Room, new TestPerson("Bo"));

            var got = new List<SignalEnvelope>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var signal in listener.ReadAllAsync(timeout.Token))
            {
                got.Add(signal);
                if (got.Count == 2)
                    break;
            }

            await Assert.That(got.Select(static s => s.Type)).IsEquivalentTo(["test.chat.MessagesArrived", "test.chat.Typing"], TUnit.Assertions.Enums.CollectionOrdering.Any);
            await Assert.That(got.Single(static s => !s.Ephemeral).Data).IsEqualTo("""{"count":2,"from":"Anna"}""");
        }

        sealed class Recorder : ISignalBusSubscriber
        {
            int stored;
            public int Stored => Volatile.Read(ref stored);
            public List<SignalEnvelope> Ephemeral { get; } = [];

            public ValueTask OnStoredAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref stored);
                return default;
            }

            public ValueTask OnEphemeralAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
            {
                lock (Ephemeral)
                    Ephemeral.Add(signal);
                return default;
            }
        }
    }
}
