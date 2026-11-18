using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Viaduct.Signals;

namespace Viaduct.Tests
{
    // ─── A contract, as an application declares one in a shared project ───

    [Signals("test.chat")]
    public interface ITestChatSignals
    {
        [SignalTopic("chat.room/{roomId}")]
        ValueTask MessagesArrivedAsync(Guid roomId, TestMessages summary, CancellationToken cancellationToken = default);

        [SignalTopic("chat.room/{roomId}"), Ephemeral]
        ValueTask TypingAsync(Guid roomId, TestPerson who, CancellationToken cancellationToken = default);

        [SignalTopic("chat.room/{roomId}"), SignalDelivery(SignalDeliveryKind.Debounce, Milliseconds = 500)]
        ValueTask RenamedAsync(Guid roomId, TestRoomName name, CancellationToken cancellationToken = default);

        [SignalTopic("user/{user}/inbox"), SignalName("Inbox")]
        ValueTask InboxChangedAsync(string user, CancellationToken cancellationToken = default);
    }

    public sealed record TestMessages(int Count, string? From) : ISignalMergeable<TestMessages>
    {
        public TestMessages Merge(TestMessages newer) => newer with { Count = Count + newer.Count };
    }

    public sealed record TestPerson(string Name);

    public sealed record TestRoomName(string Name);

    /// <summary>An app-wide handler: records what it was called with.</summary>
    public sealed class RecordingChatHandler : ITestChatSignals, ISignalResettable
    {
        public List<string> Calls { get; } = [];
        public List<SignalInfo?> Infos { get; } = [];
        public int Resets { get; private set; }

        ValueTask Record(string call)
        {
            Calls.Add(call);
            Infos.Add(SignalInfo.Current);
            return default;
        }

        public ValueTask MessagesArrivedAsync(Guid roomId, TestMessages summary, CancellationToken cancellationToken = default)
            => Record($"messages {roomId:N} {summary.Count} {summary.From}");

        public ValueTask TypingAsync(Guid roomId, TestPerson who, CancellationToken cancellationToken = default)
            => Record($"typing {roomId:N} {who.Name}");

        public ValueTask RenamedAsync(Guid roomId, TestRoomName name, CancellationToken cancellationToken = default)
            => Record($"renamed {roomId:N} {name.Name}");

        public ValueTask InboxChangedAsync(string user, CancellationToken cancellationToken = default)
            => Record($"inbox {user}");

        public ValueTask ResetAsync(CancellationToken cancellationToken = default)
        {
            Resets++;
            return default;
        }
    }

    // ─── The contract the generator wrote ───

    public class SignalContractTests
    {
        [Test]
        public async Task TheContract_DescribesEveryMethod()
        {
            var contract = SignalContracts.Get<ITestChatSignals>();

            await Assert.That(contract).IsSameReferenceAs(TestChatSignalsContract.Instance);
            await Assert.That(contract.Prefix).IsEqualTo("test.chat");
            await Assert.That(contract.Types.Select(static t => t.Key))
                .IsEquivalentTo(["test.chat.MessagesArrived", "test.chat.Typing", "test.chat.Renamed", "test.chat.Inbox"]);

            var messages = TestChatSignalsContract.Instance.MessagesArrived;
            await Assert.That(messages.Topic.Template).IsEqualTo("chat.room/{roomId}");
            await Assert.That(messages.PayloadType).IsEqualTo(typeof(TestMessages));
            await Assert.That(messages.Ephemeral).IsFalse();
            await Assert.That(TestChatSignalsContract.Instance.Typing.Ephemeral).IsTrue();
            await Assert.That(TestChatSignalsContract.Instance.Renamed.Delivery).IsEqualTo(SignalDelivery.Debounce(TimeSpan.FromMilliseconds(500)));
            await Assert.That(TestChatSignalsContract.Instance.Inbox.PayloadType).IsNull();
            await Assert.That(SignalContracts.FindType("test.chat.Inbox")).IsSameReferenceAs(TestChatSignalsContract.Instance.Inbox);
        }

        [Test]
        public async Task ThePublisher_FillsTheTopic_AndWritesThePayload()
        {
            var sink = new CollectingPublisher();
            var signals = TestChatSignalsContract.Instance.CreatePublisher(sink);
            var room = Guid.NewGuid();

            await signals.MessagesArrivedAsync(room, new TestMessages(2, "Anna"));
            await signals.InboxChangedAsync("a b/c");
            await signals.TypingAsync(room, new TestPerson("Bo"));

            await Assert.That(sink.Signals[0].Type).IsEqualTo("test.chat.MessagesArrived");
            await Assert.That(sink.Signals[0].Topic).IsEqualTo($"chat.room/{room:D}");
            await Assert.That(sink.Signals[0].Data).IsEqualTo("""{"count":2,"from":"Anna"}""");
            await Assert.That(sink.Signals[1].Topic).IsEqualTo("user/a%20b%2Fc/inbox");
            await Assert.That(sink.Signals[1].Data).IsNull();
            await Assert.That(sink.Signals[2].Ephemeral).IsTrue();
        }

        [Test]
        public async Task InvokingAHandler_ReadsTheTopicBack()
        {
            var contract = TestChatSignalsContract.Instance;
            var handler = new RecordingChatHandler();
            var room = Guid.NewGuid();

            await contract.InvokeAsync(handler, contract.Inbox, "user/a%20b%2Fc/inbox", null);
            await contract.InvokeAsync(handler, contract.MessagesArrived, $"chat.room/{room:D}", contract.Decode(contract.MessagesArrived, """{"count":3}"""));

            await Assert.That(handler.Calls).IsEquivalentTo(["inbox a b/c", $"messages {room:N} 3 "]);
            await Assert.That(() => contract.InvokeAsync(handler, contract.Inbox, "somewhere/else", null).AsTask()).Throws<FormatException>();
        }

        [Test]
        public async Task AMergeablePayload_MergesItself_OthersTakeTheNewer()
        {
            var contract = TestChatSignalsContract.Instance;

            var merged = contract.Merge(contract.MessagesArrived, new TestMessages(2, "Anna"), new TestMessages(1, "Bo"));
            var latest = contract.Merge(contract.Renamed, new TestRoomName("Old"), new TestRoomName("New"));

            await Assert.That(merged).IsEqualTo(new TestMessages(3, "Bo"));
            await Assert.That(latest).IsEqualTo(new TestRoomName("New"));
        }

        [Test]
        public async Task TopicTemplates_FillAndMatch()
        {
            var template = SignalTopicTemplate.Parse("club/{club}/group/{group}");

            var topic = template.Fill("1", "a%2Fb");
            var matched = template.TryMatch(topic, out var values);

            await Assert.That(topic).IsEqualTo("club/1/group/a%2Fb");
            await Assert.That(matched).IsTrue();
            await Assert.That(values).IsEquivalentTo(["1", "a%2Fb"]);
            await Assert.That(template.TryMatch("club/1/group/", out _)).IsFalse();
            await Assert.That(template.TryMatch("club/1/other/2", out _)).IsFalse();
            await Assert.That(() => SignalTopicTemplate.Parse("club/{club")).Throws<FormatException>();
        }

        sealed class CollectingPublisher : ISignalPublisher
        {
            public List<SignalEnvelope> Signals { get; } = [];

            public ValueTask PublishAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
            {
                Signals.Add(signal);
                return default;
            }
        }
    }

    // ─── The server's engine: publisher, store, fan-out, retention ───

    public class SignalServerTests
    {
        static readonly Guid Room = Guid.Parse("0198d3c0-1111-7222-8333-944445555666");
        static string RoomTopic => $"chat.room/{Room:D}";

        static ServiceProvider Server(TimeProvider? time = null, Action<SignalOptions>? options = null)
        {
            var services = new ServiceCollection();
            if (time is not null)
                services.AddSingleton(time);
            services.AddSignals(signals => signals.UseInMemory().Configure(options ?? (_ => { })));
            services.AddSignalPublisher<ITestChatSignals>();
            return services.BuildServiceProvider();
        }

        static HashSet<string> Topics(params string[] topics) => new(topics, StringComparer.Ordinal);

        static async Task<List<SignalEnvelope>> Take(SignalListener listener, int count)
        {
            var taken = new List<SignalEnvelope>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var signal in listener.ReadAllAsync(timeout.Token))
            {
                taken.Add(signal);
                if (taken.Count == count)
                    break;
            }
            return taken;
        }

        [Test]
        public async Task PublishedSignals_ReachTheListenersOnTheirTopic_Only()
        {
            using var server = Server();
            var signals = server.GetRequiredService<ITestChatSignals>();
            var fanOut = server.GetRequiredService<SignalFanOut>();

            using var inRoom = await fanOut.ListenAsync(null, Topics(RoomTopic));
            using var elsewhere = await fanOut.ListenAsync(null, Topics("user/x/inbox"));

            await signals.MessagesArrivedAsync(Room, new TestMessages(1, "Anna"));
            await signals.TypingAsync(Room, new TestPerson("Bo"));
            await signals.InboxChangedAsync("x");

            var room = await Take(inRoom, 2);
            var inbox = await Take(elsewhere, 1);

            await Assert.That(room.Select(static s => s.Type)).IsEquivalentTo(["test.chat.MessagesArrived", "test.chat.Typing"]);
            await Assert.That(room[0].Id).IsGreaterThan(0);
            await Assert.That(room[1].Id).IsEqualTo(0);
            await Assert.That(inbox.Single().Type).IsEqualTo("test.chat.Inbox");
        }

        [Test]
        public async Task EphemeralSignals_AreNotStored()
        {
            using var server = Server();
            var signals = server.GetRequiredService<ITestChatSignals>();
            var store = server.GetRequiredService<ISignalStore>();
            var start = await store.GetCursorAsync();

            await signals.TypingAsync(Room, new TestPerson("Bo"));
            await signals.MessagesArrivedAsync(Room, new TestMessages(1, null));

            var batch = await store.ReadAsync(start, null, 10);
            await Assert.That(batch.Signals.Select(static s => s.Type)).IsEquivalentTo(["test.chat.MessagesArrived"]);
        }

        [Test]
        public async Task AListenerWithACursor_CatchesUp_ThenGoesLive_EachOnce()
        {
            using var server = Server();
            var signals = server.GetRequiredService<ITestChatSignals>();
            var store = server.GetRequiredService<ISignalStore>();
            var fanOut = server.GetRequiredService<SignalFanOut>();

            await signals.MessagesArrivedAsync(Room, new TestMessages(1, "a"));
            var cursor = await store.GetCursorAsync();
            await signals.MessagesArrivedAsync(Room, new TestMessages(2, "b"));
            await signals.InboxChangedAsync("someone-else");
            await signals.MessagesArrivedAsync(Room, new TestMessages(3, "c"));

            using var listener = await fanOut.ListenAsync(cursor, Topics(RoomTopic));
            await signals.MessagesArrivedAsync(Room, new TestMessages(4, "d"));

            var got = await Take(listener, 3);
            await Assert.That(got.Select(static s => s.Data!)).IsEquivalentTo([
                """{"count":2,"from":"b"}""", """{"count":3,"from":"c"}""", """{"count":4,"from":"d"}"""]);
            await Assert.That(listener.Cursor).IsEqualTo(got[^1].Id);
        }

        [Test]
        public async Task ACursorPastTheRetention_GetsAReset()
        {
            var time = new ManualTimeProvider();
            using var server = Server(time);
            var signals = server.GetRequiredService<ITestChatSignals>();
            var store = server.GetRequiredService<ISignalStore>();
            var fanOut = server.GetRequiredService<SignalFanOut>();
            var retention = server.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<SignalRetentionService>().Single();

            await signals.MessagesArrivedAsync(Room, new TestMessages(1, "old"));
            var cursor = await store.GetCursorAsync();
            await signals.MessagesArrivedAsync(Room, new TestMessages(2, "old too"));
            time.Advance(TimeSpan.FromHours(25));
            await signals.MessagesArrivedAsync(Room, new TestMessages(3, "new"));

            var removed = await retention.CleanAsync();
            using var listener = await fanOut.ListenAsync(cursor, Topics(RoomTopic));
            var got = await Take(listener, 1);

            await Assert.That(removed).IsEqualTo(2);
            await Assert.That(got.Single().IsReset).IsTrue();
            await Assert.That(got.Single().Id).IsEqualTo(await store.GetCursorAsync());
        }

        [Test]
        public async Task ACursorFromAnotherStore_GetsAReset()
        {
            using var server = Server();
            var fanOut = server.GetRequiredService<SignalFanOut>();

            using var listener = await fanOut.ListenAsync(42, Topics(RoomTopic));
            var got = await Take(listener, 1);

            await Assert.That(got.Single().IsReset).IsTrue();
        }

        [Test]
        public async Task AfterTopicsChanged_WhatFollowsIsReadWithTheNewTopics_AlsoWhileCatchingUp()
        {
            using var server = Server();
            var signals = server.GetRequiredService<ITestChatSignals>();
            var viaduct = server.GetRequiredService<IViaductSignals>();
            var store = server.GetRequiredService<ISignalStore>();
            var fanOut = server.GetRequiredService<SignalFanOut>();
            var user = SignalTopics.ForUser("u1");

            var cursor = await store.GetCursorAsync();
            await viaduct.TopicsChangedAsync("u1");
            await signals.MessagesArrivedAsync(Room, new TestMessages(1, "after joining"));

            using var listener = await fanOut.ListenAsync(cursor, Topics(user), Topics(ViaductSignalsContract.Instance.TopicsChanged.Key));
            var got = new List<SignalEnvelope>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var signal in listener.ReadAllAsync(timeout.Token))
            {
                got.Add(signal);
                if (signal.Type == ViaductSignalsContract.Instance.TopicsChanged.Key)
                    listener.SetTopics(Topics(user, RoomTopic));
                if (got.Count == 2)
                    break;
            }

            await Assert.That(got[1].Topic).IsEqualTo(RoomTopic);
        }

        [Test]
        public async Task ATooLargePayload_IsRefused()
        {
            using var server = Server(options: o => o.MaxPayloadBytes = 16);
            var signals = server.GetRequiredService<ITestChatSignals>();

            await Assert.That(() => signals.MessagesArrivedAsync(Room, new TestMessages(1, new string('x', 100))).AsTask())
                .Throws<ArgumentException>();
        }

        [Test]
        public async Task ASlowListener_Overflows_RatherThanLosingSignals()
        {
            using var server = Server(options: o => o.ListenerCapacity = 2);
            var signals = server.GetRequiredService<ITestChatSignals>();
            var fanOut = server.GetRequiredService<SignalFanOut>();

            using var listener = await fanOut.ListenAsync(null, Topics(RoomTopic));
            for (var i = 0; i < 5; i++)
                await signals.MessagesArrivedAsync(Room, new TestMessages(i, null));

            var got = new List<SignalEnvelope>();
            await foreach (var signal in listener.ReadAllAsync())
                got.Add(signal);

            await Assert.That(listener.Overflowed).IsTrue();
            await Assert.That(got.Count).IsEqualTo(2);
            await Assert.That(listener.Cursor).IsEqualTo(got[^1].Id);
        }

        [Test]
        public async Task RetentionRunsEvery24thOfIt_BetweenAMinuteAndAnHour()
        {
            await Assert.That(SignalRetentionService.IntervalFor(TimeSpan.FromHours(24))).IsEqualTo(TimeSpan.FromHours(1));
            await Assert.That(SignalRetentionService.IntervalFor(TimeSpan.FromHours(6))).IsEqualTo(TimeSpan.FromMinutes(15));
            await Assert.That(SignalRetentionService.IntervalFor(TimeSpan.FromMinutes(5))).IsEqualTo(TimeSpan.FromMinutes(1));
        }

        [Test]
        public async Task AFanOutWhoseStoreWasOutOfReach_StartsWhenItIsBack()
        {
            var options = new OptionsMonitor(new SignalOptions());
            var store = new FlakyStore(new InMemorySignalStore(options));
            var bus = new InProcessSignalBus();
            using var fanOut = new SignalFanOut(store, bus, options);

            await Assert.That(() => fanOut.ListenAsync(null, Topics(RoomTopic)).AsTask()).Throws<InvalidOperationException>();
            store.Reachable = true;
            using var listener = await fanOut.ListenAsync(null, Topics(RoomTopic));

            var publisher = new SignalPublisher(store, bus, options);
            await TestChatSignalsContract.Instance.CreatePublisher(publisher).MessagesArrivedAsync(Room, new TestMessages(1, "back"));
            var got = await Take(listener, 1);

            await Assert.That(got.Single().Data).IsEqualTo("""{"count":1,"from":"back"}""");
        }

        /// <summary>A store that cannot be reached until the test says so.</summary>
        sealed class FlakyStore(ISignalStore inner) : ISignalStore
        {
            public bool Reachable { get; set; }

            T Check<T>(T value) => Reachable ? value : throw new InvalidOperationException("The store is out of reach.");

            public ValueTask<SignalEnvelope> AppendAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
                => Check(inner).AppendAsync(signal, cancellationToken);

            public ValueTask<SignalBatch> ReadAsync(long after, IReadOnlySet<string>? topics, int max, CancellationToken cancellationToken = default)
                => Check(inner).ReadAsync(after, topics, max, cancellationToken);

            public ValueTask<long> GetCursorAsync(CancellationToken cancellationToken = default)
                => Check(inner).GetCursorAsync(cancellationToken);

            public ValueTask<int> RemoveBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
                => Check(inner).RemoveBeforeAsync(before, cancellationToken);
        }

        sealed class OptionsMonitor(SignalOptions value) : IOptionsMonitor<SignalOptions>
        {
            public SignalOptions CurrentValue => value;
            public SignalOptions Get(string? name) => value;
            public IDisposable? OnChange(Action<SignalOptions, string?> listener) => null;
        }
    }

    // ─── The client: cursor, delivery, merging, subscriptions, reset ───

    public class SignalClientTests
    {
        static readonly Guid Room = Guid.Parse("0198d3c0-1111-7222-8333-944445555666");
        static readonly Guid OtherRoom = Guid.Parse("0198d3c0-2222-7222-8333-944445555666");

        sealed class Client : IDisposable
        {
            readonly ServiceProvider provider;
            readonly IServiceScope scope;
            long id = 100;

            public Client(Action<SignalClientOptions>? configure = null, ManualTimeProvider? time = null)
            {
                var services = new ServiceCollection();
                services.AddSingleton<TimeProvider>(Time = time ?? new ManualTimeProvider());
                services.AddSignalClient(configure);
                services.AddSignalHandler<ITestChatSignals, RecordingChatHandler>();
                provider = services.BuildServiceProvider();
                scope = provider.CreateScope();
            }

            public ManualTimeProvider Time { get; }
            public SignalDispatcher Dispatcher => scope.ServiceProvider.GetRequiredService<SignalDispatcher>();
            public RecordingChatHandler Handler => scope.ServiceProvider.GetRequiredService<RecordingChatHandler>();
            public SignalClientEnvironment Environment => scope.ServiceProvider.GetRequiredService<SignalClientEnvironment>();

            /// <summary>A signal as the server would send it: published through the contract, stamped and numbered.</summary>
            public async Task Receive(Func<ITestChatSignals, ValueTask> publish, bool numbered = true)
            {
                var sink = new Sink();
                await publish(TestChatSignalsContract.Instance.CreatePublisher(sink));
                var signal = sink.Signal! with { At = Time.GetUtcNow() };
                if (numbered && !signal.Ephemeral)
                    signal = signal with { Id = ++id };
                await Dispatcher.ReceiveAsync(signal);
            }

            public void Dispose()
            {
                scope.Dispose();
                provider.Dispose();
            }

            sealed class Sink : ISignalPublisher
            {
                public SignalEnvelope? Signal { get; private set; }

                public ValueTask PublishAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
                {
                    Signal = signal;
                    return default;
                }
            }
        }

        [Test]
        public async Task ASignal_CallsTheHandler_WithItsTopicAndPayload_AndItsInfo()
        {
            using var client = new Client();

            await client.Receive(s => s.MessagesArrivedAsync(Room, new TestMessages(2, "Anna")));

            await Assert.That(client.Handler.Calls).IsEquivalentTo([$"messages {Room:N} 2 Anna"]);
            var info = client.Handler.Infos.Single()!;
            await Assert.That(info.Type).IsSameReferenceAs(TestChatSignalsContract.Instance.MessagesArrived);
            await Assert.That(info.Id).IsEqualTo(client.Dispatcher.Cursor);
            await Assert.That(info.Count).IsEqualTo(1);
            await Assert.That(SignalInfo.Current).IsNull();
        }

        [Test]
        public async Task ASignalAlreadyHad_IsDropped_AndAnUnknownOne_Ignored()
        {
            using var client = new Client();
            await client.Receive(s => s.InboxChangedAsync("me"));
            var seen = client.Dispatcher.Cursor;

            await client.Dispatcher.ReceiveAsync(new SignalEnvelope("test.chat.Inbox", "user/me/inbox") { Id = seen });
            await client.Dispatcher.ReceiveAsync(new SignalEnvelope("someone.else.Thing", "x") { Id = seen + 1 });

            await Assert.That(client.Handler.Calls.Count).IsEqualTo(1);
            await Assert.That(client.Dispatcher.Cursor).IsEqualTo(seen + 1);
        }

        [Test]
        public async Task HeldSignals_MergeIntoOneCall()
        {
            using var client = new Client(o => o.Deliver<ITestChatSignals>(SignalDelivery.Manual));

            await client.Receive(s => s.MessagesArrivedAsync(Room, new TestMessages(2, "Anna")));
            await client.Receive(s => s.MessagesArrivedAsync(Room, new TestMessages(1, "Bo")));
            await client.Receive(s => s.MessagesArrivedAsync(OtherRoom, new TestMessages(5, "Cas")));
            await Assert.That(client.Handler.Calls.Count).IsEqualTo(0);
            await Assert.That(client.Dispatcher.HeldCount).IsEqualTo(2);

            await client.Dispatcher.FlushAsync(topic: $"chat.room/{Room:D}");

            await Assert.That(client.Handler.Calls).IsEquivalentTo([$"messages {Room:N} 3 Bo"]);
            await Assert.That(client.Handler.Infos.Single()!.Count).IsEqualTo(2);
            await Assert.That(client.Dispatcher.HeldCount).IsEqualTo(1);
        }

        [Test]
        public async Task ADebounce_WaitsForAPause()
        {
            using var client = new Client();
            var renamed = client.Handler;

            await client.Receive(s => s.RenamedAsync(Room, new TestRoomName("A")));
            client.Time.Advance(TimeSpan.FromMilliseconds(400));
            await client.Receive(s => s.RenamedAsync(Room, new TestRoomName("B")));
            client.Time.Advance(TimeSpan.FromMilliseconds(400));
            await Assert.That(renamed.Calls.Count).IsEqualTo(0);

            client.Time.Advance(TimeSpan.FromMilliseconds(100));
            await WaitFor(() => renamed.Calls.Count == 1);

            await Assert.That(renamed.Calls).IsEquivalentTo([$"renamed {Room:N} B"]);
            await Assert.That(renamed.Infos.Single()!.Count).IsEqualTo(2);
        }

        [Test]
        public async Task ABatch_DeliversAFixedTimeAfterTheFirst()
        {
            using var client = new Client(o => o.Deliver(TestChatSignalsContract.Instance.MessagesArrived, SignalDelivery.Batch(TimeSpan.FromSeconds(1))));

            await client.Receive(s => s.MessagesArrivedAsync(Room, new TestMessages(1, "a")));
            client.Time.Advance(TimeSpan.FromMilliseconds(600));
            await client.Receive(s => s.MessagesArrivedAsync(Room, new TestMessages(1, "b")));
            client.Time.Advance(TimeSpan.FromMilliseconds(400));
            await WaitFor(() => client.Handler.Calls.Count == 1);

            await Assert.That(client.Handler.Calls).IsEquivalentTo([$"messages {Room:N} 2 b"]);
        }

        [Test]
        public async Task WhenVisible_HoldsWhileHidden()
        {
            using var client = new Client(o => o.DefaultDelivery = SignalDelivery.WhenVisible);
            client.Environment.SetVisible(false);

            await client.Receive(s => s.InboxChangedAsync("me"));
            await Assert.That(client.Handler.Calls.Count).IsEqualTo(0);

            client.Environment.SetVisible(true);
            await WaitFor(() => client.Handler.Calls.Count == 1);
            await client.Receive(s => s.InboxChangedAsync("me"));

            await Assert.That(client.Handler.Calls).IsEquivalentTo(["inbox me", "inbox me"]);
        }

        [Test]
        public async Task ASubscription_HearsItsTopic_UntilDisposed()
        {
            using var client = new Client();
            var heard = new List<TestMessages>();
            var topicsChanged = 0;
            client.Dispatcher.TopicsChanged += () => topicsChanged++;

            var subscription = client.Dispatcher.TestChatSignals().OnMessagesArrived(Room, (summary, _) =>
            {
                heard.Add(summary);
                return default;
            });
            await Assert.That(client.Dispatcher.SubscribedTopics).IsEquivalentTo([$"chat.room/{Room:D}"]);

            await client.Receive(s => s.MessagesArrivedAsync(Room, new TestMessages(1, "a")));
            await client.Receive(s => s.MessagesArrivedAsync(OtherRoom, new TestMessages(1, "b")));
            subscription.Dispose();
            await client.Receive(s => s.MessagesArrivedAsync(Room, new TestMessages(1, "c")));

            await Assert.That(heard).IsEquivalentTo([new TestMessages(1, "a")]);
            await Assert.That(topicsChanged).IsEqualTo(2);
            await Assert.That(client.Dispatcher.SubscribedTopics).IsEmpty();
        }

        [Test]
        public async Task AReset_MovesTheCursor_DropsWhatIsHeld_AndTellsEveryone()
        {
            using var client = new Client(o => o.DefaultDelivery = SignalDelivery.Manual);
            var reloaded = 0;
            using var subscription = client.Dispatcher.TestChatSignals().OnInbox("me", _ => default, _ =>
            {
                reloaded++;
                return default;
            });
            await client.Receive(s => s.InboxChangedAsync("me"));

            await client.Dispatcher.ReceiveAsync(SignalEnvelope.Reset(5000));

            await Assert.That(client.Dispatcher.Cursor).IsEqualTo(5000);
            await Assert.That(client.Dispatcher.HeldCount).IsEqualTo(0);
            await Assert.That(client.Handler.Resets).IsEqualTo(1);
            await Assert.That(reloaded).IsEqualTo(1);
        }

        [Test]
        public async Task AFailingHandler_DoesNotStopTheOthers()
        {
            using var client = new Client();
            var heard = 0;
            using var failing = client.Dispatcher.TestChatSignals().OnInbox("me", _ => throw new InvalidOperationException("boom"));
            using var working = client.Dispatcher.TestChatSignals().OnInbox("me", _ =>
            {
                heard++;
                return default;
            });

            await client.Receive(s => s.InboxChangedAsync("me"));

            await Assert.That(heard).IsEqualTo(1);
            await Assert.That(client.Handler.Calls.Count).IsEqualTo(1);
        }

        static async Task WaitFor(Func<bool> condition)
        {
            for (var i = 0; i < 200 && !condition(); i++)
                await Task.Delay(10);
            await Assert.That(condition()).IsTrue();
        }
    }

    /// <summary>A clock the test moves: timers fire when it passes their time.</summary>
    public sealed class ManualTimeProvider : TimeProvider
    {
        readonly object gate = new();
        readonly List<ManualTimer> timers = [];
        DateTimeOffset now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
                return now;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (gate)
            {
                now += by;
                due = timers.Where(t => t.Due is { } d && d <= now).OrderBy(static t => t.Due).ToList();
                foreach (var timer in due)
                    timer.Due = timer.Period is { } period ? now + period : null;
            }
            foreach (var timer in due)
                timer.Fire();
        }

        /// <summary>How many timers are waiting to fire.</summary>
        public int PendingTimers
        {
            get { lock (gate) return timers.Count(static t => t.Due is not null); }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (gate)
                timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }
            public TimeSpan? Period { get; private set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                    Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
                }
                return true;
            }

            public void Dispose()
            {
                lock (clock.gate)
                {
                    Due = null;
                    clock.timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
