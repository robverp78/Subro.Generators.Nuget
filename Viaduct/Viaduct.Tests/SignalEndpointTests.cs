using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Viaduct.Signals;

namespace Viaduct.Tests
{
    /// <summary>How signals travel: the same writer and reader on both sides.</summary>
    public class SignalWireTests
    {
        [Test]
        public async Task ASignal_RoundTrips()
        {
            var signal = new SignalEnvelope("chat.MessagesArrived", "chat.room/1", """{"count":2}""")
            {
                Id = 7,
                At = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero),
            };
            var ephemeral = new SignalEnvelope("chat.Typing", "chat.room/1") { Ephemeral = true };

            await Assert.That(SignalWire.FromJson(SignalWire.ToJson(signal))).IsEqualTo(signal);
            await Assert.That(SignalWire.FromJson(SignalWire.ToJson(ephemeral))).IsEqualTo(ephemeral);
            await Assert.That(() => SignalWire.FromJson("""{"id":1}""")).Throws<FormatException>();
        }

        [Test]
        public async Task APoll_AndAStart_RoundTrip()
        {
            var poll = new SignalPoll(9, false, true, [new SignalEnvelope("a.B", "t") { Id = 9 }], ["refused/1"]);

            var read = SignalWire.PollFromJson(SignalWire.ToJson(poll));
            var connected = SignalWire.ConnectedFromJson(SignalWire.ToJson(new SignalConnected(5, [])));

            await Assert.That(read.Cursor).IsEqualTo(9);
            await Assert.That(read.More).IsTrue();
            await Assert.That(read.Reset).IsFalse();
            await Assert.That(read.Signals.Single()).IsEqualTo(poll.Signals[0]);
            await Assert.That(read.Refused).IsEquivalentTo(["refused/1"]);
            await Assert.That(connected.Cursor).IsEqualTo(5);
        }

        [Test]
        public async Task AnEvent_HasItsIdOnlyWhenStored_AndOneDataLinePerLine()
        {
            var stored = SignalWire.FormatEvent(new SignalEnvelope("a.B", "t") { Id = 3 });
            var ephemeral = SignalWire.FormatEvent(new SignalEnvelope("a.C", "t") { Ephemeral = true });
            var lines = SignalWire.FormatEvent("x", "one\ntwo");

            await Assert.That(stored).StartsWith("id: 3\nevent: a.B\ndata: {");
            await Assert.That(ephemeral).StartsWith("event: a.C\n");
            await Assert.That(lines).IsEqualTo("event: x\ndata: one\ndata: two\n\n");
        }
    }

    /// <summary>The signal endpoint over a test server: polling, long polling and server-sent events.</summary>
    public class SignalEndpointTests
    {
        static readonly Guid Room = Guid.Parse("0198d3c0-1111-7222-8333-944445555666");
        static readonly Guid OtherRoom = Guid.Parse("0198d3c0-2222-7222-8333-944445555666");
        static string RoomTopic(Guid room) => $"chat.room/{room:D}";

        /// <summary>Rooms per user, as an application's membership would say: the base topics of <see cref="RoomsResolver"/>.</summary>
        sealed class Rooms
        {
            public ConcurrentDictionary<string, Guid[]> ByUser { get; } = new();
        }

        sealed class RoomsResolver(Rooms rooms) : ISignalTopicResolver
        {
            public ValueTask<IReadOnlyCollection<string>> GetTopicsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
            {
                var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                IReadOnlyCollection<string> topics = id is null ? [] : [
                    $"user/{SignalTopicValues.Format(id)}/inbox",
                    .. rooms.ByUser.GetValueOrDefault(id, []).Select(RoomTopic)];
                return new(topics);
            }
        }

        sealed class Server : IAsyncDisposable
        {
            public required WebApplication App { get; init; }
            public required ManualTimeProvider Time { get; init; }
            public HttpClient Client => App.GetTestClient();
            public Rooms Rooms => App.Services.GetRequiredService<Rooms>();
            public ITestChatSignals Chat => App.Services.GetRequiredService<ITestChatSignals>();
            public IViaductSignals Viaduct => App.Services.GetRequiredService<IViaductSignals>();
            public ValueTask DisposeAsync() => App.DisposeAsync();
        }

        /// <summary>
        /// A server whose callers are whoever the <c>X-Test-User</c> header says (with <c>X-Test-Exp</c> as their token's
        /// expiry), who may ask for any room but <see cref="OtherRoom"/>.
        /// </summary>
        static async Task<Server> StartAsync(Action<SignalOptions>? options = null)
        {
            var time = new ManualTimeProvider();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<TimeProvider>(time);
            builder.Services.AddSingleton<Rooms>();
            builder.Services.AddSignals(signals => signals
                .UseInMemory()
                .Configure(o =>
                {
                    o.RequireAuthorization = false;
                    options?.Invoke(o);
                })
                .AddTopicResolver<RoomsResolver>()
                .AllowTopic(TestChatSignalsContract.Instance.MessagesArrived.Topic,
                    (user, topic, _) => new(user.Identity?.IsAuthenticated == true && topic != RoomTopic(OtherRoom))));
            builder.Services.AddSignalPublisher<ITestChatSignals>();

            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers["X-Test-User"].ToString() is { Length: > 0 } user)
                {
                    List<Claim> claims = [new(ClaimTypes.NameIdentifier, user)];
                    if (context.Request.Headers["X-Test-Exp"].ToString() is { Length: > 0 } exp)
                        claims.Add(new("exp", exp));
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
                }
                await next(context);
            });
            app.MapSignals();
            await app.StartAsync();
            return new Server { App = app, Time = time };
        }

        static HttpRequestMessage Request(string url, string? user = "u1", bool stream = false, long? lastEventId = null, long? exp = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (user is not null)
                request.Headers.Add("X-Test-User", user);
            if (exp is long seconds)
                request.Headers.Add("X-Test-Exp", seconds.ToString());
            if (stream)
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SignalWire.EventStreamType));
            if (lastEventId is long id)
                request.Headers.Add(SignalWire.LastEventIdHeader, id.ToString());
            return request;
        }

        static async Task<SignalPoll> Poll(Server server, string url, string? user = "u1")
        {
            using var response = await server.Client.SendAsync(Request(url, user));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            return SignalWire.PollFromJson(await response.Content.ReadAsStringAsync());
        }

        /// <summary>One server-sent event, or a comment such as a heartbeat (<see cref="Comment"/> set, no event).</summary>
        sealed record Event(string? Id, string? Type, string? Data, string? Comment);

        sealed class EventStream : IDisposable
        {
            readonly HttpResponseMessage response;
            readonly StreamReader reader;

            EventStream(HttpResponseMessage response, Stream stream)
            {
                this.response = response;
                reader = new StreamReader(stream);
            }

            public static async Task<EventStream> OpenAsync(Server server, string url, string user = "u1", long? lastEventId = null, long? exp = null)
            {
                var response = await server.Client.SendAsync(Request(url, user, stream: true, lastEventId, exp), HttpCompletionOption.ResponseHeadersRead);
                await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo(SignalWire.EventStreamType);
                return new EventStream(response, await response.Content.ReadAsStreamAsync());
            }

            /// <summary>The next event or comment; null when the stream ended.</summary>
            public async Task<Event?> NextAsync()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                string? id = null, type = null, comment = null;
                var data = new List<string>();
                while (true)
                {
                    var line = await reader.ReadLineAsync(timeout.Token);
                    if (line is null)
                        return null;
                    if (line.Length == 0)
                    {
                        if (type is null && comment is null && data.Count == 0)
                            continue;
                        return new Event(id, type, data.Count == 0 ? null : string.Join("\n", data), comment);
                    }
                    if (line.StartsWith(':'))
                        comment = line[1..].Trim();
                    else if (line.StartsWith("id: "))
                        id = line[4..];
                    else if (line.StartsWith("event: "))
                        type = line[7..];
                    else if (line.StartsWith("data: "))
                        data.Add(line[6..]);
                }
            }

            public async Task<SignalEnvelope> NextSignalAsync()
            {
                var next = await NextAsync();
                await Assert.That(next?.Data).IsNotNull();
                return SignalWire.FromJson(next!.Data!);
            }

            public void Dispose()
            {
                reader.Dispose();
                response.Dispose();
            }
        }

        static async Task WaitFor(Func<bool> condition)
        {
            for (var i = 0; i < 300 && !condition(); i++)
                await Task.Delay(10);
            await Assert.That(condition()).IsTrue();
        }

        [Test]
        public async Task APoll_WithoutACursor_GivesWhereToStart_ThenWhatCameAfter()
        {
            await using var server = await StartAsync();

            var start = await Poll(server, $"/signals?topic={RoomTopic(Room)}&topic={RoomTopic(OtherRoom)}");
            await server.Chat.MessagesArrivedAsync(Room, new TestMessages(1, "a"));
            await server.Chat.MessagesArrivedAsync(OtherRoom, new TestMessages(1, "b"));
            await server.Chat.InboxChangedAsync("u1");
            await server.Chat.InboxChangedAsync("u2");
            var next = await Poll(server, $"/signals?after={start.Cursor}&topic={RoomTopic(Room)}&topic={RoomTopic(OtherRoom)}");

            await Assert.That(start.Signals.Count).IsEqualTo(0);
            await Assert.That(start.Refused).IsEquivalentTo([RoomTopic(OtherRoom)]);
            await Assert.That(next.Signals.Select(static s => s.Topic)).IsEquivalentTo([RoomTopic(Room), "user/u1/inbox"]);
            await Assert.That(next.Cursor).IsGreaterThan(start.Cursor);
            await Assert.That(next.Refused).IsEquivalentTo([RoomTopic(OtherRoom)]);
        }

        [Test]
        public async Task ALongPoll_IsHeldUntilSomethingArrives()
        {
            await using var server = await StartAsync();
            var start = await Poll(server, "/signals");

            var waiting = Poll(server, $"/signals?after={start.Cursor}&wait=30");
            await Task.Delay(100);
            await Assert.That(waiting.IsCompleted).IsFalse();
            await server.Chat.InboxChangedAsync("u1");
            var answer = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(answer.Signals.Single().Topic).IsEqualTo("user/u1/inbox");
        }

        [Test]
        public async Task ALongPoll_AnswersEmptyAfterItsWait()
        {
            await using var server = await StartAsync();
            var start = await Poll(server, "/signals");
            var timers = server.Time.PendingTimers;

            var waiting = Poll(server, $"/signals?after={start.Cursor}&wait=5");
            await WaitFor(() => server.Time.PendingTimers > timers);
            server.Time.Advance(TimeSpan.FromSeconds(5));
            var answer = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(answer.Signals.Count).IsEqualTo(0);
            await Assert.That(answer.Cursor).IsEqualTo(start.Cursor);
        }

        [Test]
        public async Task AStream_StartsWithItsCursor_ThenCarriesWhatArrives()
        {
            await using var server = await StartAsync();
            using var stream = await EventStream.OpenAsync(server, $"/signals?topic={RoomTopic(Room)}&topic={RoomTopic(OtherRoom)}");

            var connected = await stream.NextAsync();
            await server.Chat.MessagesArrivedAsync(OtherRoom, new TestMessages(1, "not for u1"));
            await server.Chat.TypingAsync(Room, new TestPerson("Bo"));
            await server.Chat.MessagesArrivedAsync(Room, new TestMessages(2, "Anna"));
            var typing = await stream.NextAsync();
            var messages = await stream.NextAsync();

            await Assert.That(connected!.Type).IsEqualTo(SignalWire.ConnectedEvent);
            var start = SignalWire.ConnectedFromJson(connected.Data!);
            await Assert.That(start.Refused).IsEquivalentTo([RoomTopic(OtherRoom)]);
            await Assert.That(connected.Id).IsEqualTo(start.Cursor.ToString());

            await Assert.That(typing!.Type).IsEqualTo("test.chat.Typing");
            await Assert.That(typing.Id).IsNull();
            await Assert.That(messages!.Type).IsEqualTo("test.chat.MessagesArrived");
            var signal = SignalWire.FromJson(messages.Data!);
            await Assert.That(messages.Id).IsEqualTo(signal.Id.ToString());
            await Assert.That(signal.Data).IsEqualTo("""{"count":2,"from":"Anna"}""");
        }

        [Test]
        public async Task AStreamWithALastEventId_CatchesUpFirst()
        {
            await using var server = await StartAsync();
            var start = await Poll(server, "/signals");
            await server.Chat.InboxChangedAsync("u1");
            await server.Chat.InboxChangedAsync("u1");

            using var stream = await EventStream.OpenAsync(server, "/signals", lastEventId: start.Cursor);
            await stream.NextAsync();
            var first = await stream.NextSignalAsync();
            var second = await stream.NextSignalAsync();

            await Assert.That(first.Id).IsGreaterThan(start.Cursor);
            await Assert.That(second.Id).IsGreaterThan(first.Id);
        }

        [Test]
        public async Task AStreamFromAForgottenCursor_StartsWithAReset()
        {
            await using var server = await StartAsync();

            using var stream = await EventStream.OpenAsync(server, "/signals", lastEventId: 1);
            await stream.NextAsync();
            var reset = await stream.NextAsync();

            await Assert.That(reset!.Type).IsEqualTo(SignalEnvelope.ResetType);
            await Assert.That(SignalWire.FromJson(reset.Data!).IsReset).IsTrue();
        }

        [Test]
        public async Task TopicsChanged_WorksTheTopicsOutAgain_WithoutReconnecting()
        {
            await using var server = await StartAsync();
            using var stream = await EventStream.OpenAsync(server, "/signals");
            await stream.NextAsync();

            await server.Chat.MessagesArrivedAsync(Room, new TestMessages(1, "before joining"));
            server.Rooms.ByUser["u1"] = [Room];
            await server.Viaduct.TopicsChangedAsync("u1");
            await server.Chat.MessagesArrivedAsync(Room, new TestMessages(1, "after joining"));

            var changed = await stream.NextSignalAsync();
            var message = await stream.NextSignalAsync();

            await Assert.That(changed.Type).IsEqualTo(ViaductSignalsContract.Instance.TopicsChanged.Key);
            await Assert.That(changed.Topic).IsEqualTo(SignalTopics.ForUser("u1"));
            await Assert.That(message.Data).IsEqualTo("""{"count":1,"from":"after joining"}""");
        }

        [Test]
        public async Task AnApplicationsOwnSignal_CanHaveTheTopicsWorkedOutAgain()
        {
            // As an application whose own signal says someone's access changed: here, the inbox signal.
            await using var server = await StartAsync(o => o.ResolveTopicsAgainOn.Add(TestChatSignalsContract.Instance.Inbox.Key));
            using var stream = await EventStream.OpenAsync(server, "/signals");
            await stream.NextAsync();

            server.Rooms.ByUser["u1"] = [Room];
            await server.Chat.InboxChangedAsync("u1");
            await server.Chat.MessagesArrivedAsync(Room, new TestMessages(1, "after joining"));

            var changed = await stream.NextSignalAsync();
            var message = await stream.NextSignalAsync();

            await Assert.That(changed.Type).IsEqualTo(TestChatSignalsContract.Instance.Inbox.Key);
            await Assert.That(message.Data).IsEqualTo("""{"count":1,"from":"after joining"}""");
        }

        [Test]
        public async Task AQuietStream_SendsAHeartbeat()
        {
            await using var server = await StartAsync();
            var timers = server.Time.PendingTimers;
            using var stream = await EventStream.OpenAsync(server, "/signals");
            await stream.NextAsync();

            await WaitFor(() => server.Time.PendingTimers > timers);
            server.Time.Advance(TimeSpan.FromSeconds(25));
            var heartbeat = await stream.NextAsync();

            await Assert.That(heartbeat!.Comment).IsEqualTo("ping");
        }

        [Test]
        public async Task AStream_EndsWhenItsTokenExpires()
        {
            await using var server = await StartAsync();
            var expires = server.Time.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds();
            var timers = server.Time.PendingTimers;
            using var stream = await EventStream.OpenAsync(server, "/signals", exp: expires);
            await stream.NextAsync();

            await WaitFor(() => server.Time.PendingTimers >= timers + 2);
            server.Time.Advance(TimeSpan.FromMinutes(15));
            Event? next;
            do
                next = await stream.NextAsync();
            while (next?.Comment is not null);

            await Assert.That(next).IsNull();
        }

        [Test]
        public async Task TooManyTopics_OrABadCursor_AreBadRequests()
        {
            await using var server = await StartAsync(o => o.MaxTopics = 1);

            using var topics = await server.Client.SendAsync(Request("/signals?topic=a&topic=b"));
            using var cursor = await server.Client.SendAsync(Request("/signals?after=abc"));

            await Assert.That(topics.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(cursor.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        [Test]
        public async Task TheEndpoint_RequiresAuthorization_ByDefault()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthorization();
            builder.Services.AddSignals();
            await using var app = builder.Build();
            app.MapSignals();

            var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static d => d.Endpoints).Single();

            await Assert.That(endpoint.Metadata.GetMetadata<IAuthorizeData>()).IsNotNull();
        }
    }
}
