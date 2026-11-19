using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Viaduct.Signals;

namespace Viaduct.Tests
{
    /// <summary>The client's connection to a test server: server-sent events, falling back to polling, reconnecting, pausing.</summary>
    public class SignalConnectionTests
    {
        static readonly Guid Room = Guid.Parse("0198d3c0-1111-7222-8333-944445555666");

        /// <summary>What the test server does with a request, set by the test.</summary>
        sealed class Behaviour
        {
            public volatile string Mode = "normal";
            public int Requests;
        }

        sealed class InboxResolver : ISignalTopicResolver
        {
            public ValueTask<IReadOnlyCollection<string>> GetTopicsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
                => new(user.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } id ? [$"user/{SignalTopicValues.Format(id)}/inbox"] : []);
        }

        /// <summary>Gives every request a token that expires 15 minutes after the server's now.</summary>
        sealed class ExpiryHandler(ManualTimeProvider serverTime) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Headers.Add("X-Test-Exp", serverTime.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds().ToString());
                return base.SendAsync(request, cancellationToken);
            }
        }

        sealed class Setup : IAsyncDisposable
        {
            public required WebApplication Server { get; init; }
            public required ManualTimeProvider ServerTime { get; init; }
            public required ManualTimeProvider ClientTime { get; init; }
            public required ServiceProvider ClientServices { get; init; }
            public required AsyncServiceScope ClientScope { get; init; }
            public List<SignalClientState> States { get; } = [];

            public Behaviour Behaviour => Server.Services.GetRequiredService<Behaviour>();
            public ITestChatSignals Chat => Server.Services.GetRequiredService<ITestChatSignals>();
            public SignalFanOut FanOut => Server.Services.GetRequiredService<SignalFanOut>();
            public SignalClient Client => ClientScope.ServiceProvider.GetRequiredService<SignalClient>();
            public SignalDispatcher Dispatcher => ClientScope.ServiceProvider.GetRequiredService<SignalDispatcher>();
            public RecordingChatHandler Handler => ClientScope.ServiceProvider.GetRequiredService<RecordingChatHandler>();
            public SignalClientEnvironment Environment => ClientScope.ServiceProvider.GetRequiredService<SignalClientEnvironment>();

            public int TimesConnected
            {
                get { lock (States) return States.Count(static s => s == SignalClientState.Connected); }
            }

            public async ValueTask DisposeAsync()
            {
                await ClientScope.DisposeAsync();
                await ClientServices.DisposeAsync();
                await Server.DisposeAsync();
            }
        }

        static async Task<Setup> StartAsync(Action<SignalClientOptions>? client = null, bool expiringTokens = false, string mode = "normal")
        {
            var serverTime = new ManualTimeProvider();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<TimeProvider>(serverTime);
            builder.Services.AddSingleton(new Behaviour { Mode = mode });
            builder.Services.AddSignals(signals => signals
                .UseInMemory()
                .Configure(o => o.RequireAuthorization = false)
                .AddTopicResolver<InboxResolver>()
                .AllowTopic(TestChatSignalsContract.Instance.MessagesArrived.Topic, (user, _, _) => new(user.Identity?.IsAuthenticated == true)));
            builder.Services.AddSignalPublisher<ITestChatSignals>();

            var server = builder.Build();
            server.Use(async (context, next) =>
            {
                var behaviour = context.RequestServices.GetRequiredService<Behaviour>();
                Interlocked.Increment(ref behaviour.Requests);
                var stream = context.Request.Headers.Accept.ToString().Contains(SignalWire.EventStreamType);

                switch (behaviour.Mode)
                {
                    case "deny":
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    case "refuse-streams" when stream:
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    case "error-streams" when stream:
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        return;
                    case "unreachable-streams" when stream:
                        context.Abort();
                        return;
                    case "silent-streams" when stream:
                        context.Response.ContentType = SignalWire.EventStreamType;
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        try
                        {
                            await Task.Delay(Timeout.Infinite, context.RequestAborted);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        return;
                }

                List<Claim> claims = [new(ClaimTypes.NameIdentifier, "u1")];
                if (context.Request.Headers["X-Test-Exp"].ToString() is { Length: > 0 } exp)
                    claims.Add(new("exp", exp));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
                await next(context);
            });
            server.MapSignals();
            await server.StartAsync();

            var clientTime = new ManualTimeProvider();
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(clientTime);
            var http = services.AddSignalConnection(c => c.BaseAddress = new Uri("http://localhost/"), o =>
            {
                o.BackoffJitter = 0;
                client?.Invoke(o);
            }).ConfigurePrimaryHttpMessageHandler(() => server.GetTestServer().CreateHandler());
            if (expiringTokens)
                http.AddHttpMessageHandler(() => new ExpiryHandler(serverTime));
            services.AddSignalHandler<ITestChatSignals, RecordingChatHandler>();

            var provider = services.BuildServiceProvider();
            var setup = new Setup
            {
                Server = server,
                ServerTime = serverTime,
                ClientTime = clientTime,
                ClientServices = provider,
                ClientScope = provider.CreateAsyncScope(),
            };
            var signals = setup.Client;
            signals.StateChanged += () =>
            {
                lock (setup.States)
                    setup.States.Add(signals.State);
            };
            return setup;
        }

        static async Task WaitFor(Func<bool> condition, string what)
        {
            for (var i = 0; i < 1500 && !condition(); i++)
                await Task.Delay(10);
            if (!condition())
                throw new TimeoutException("Waited in vain for: " + what);
        }

        static Task Connected(Setup setup, string connection, int times = 1)
            => WaitFor(() => setup.TimesConnected >= times && setup.Client.State == SignalClientState.Connected && setup.Client.ConnectionName == connection,
                $"connected over {connection} ({times}x)");

        [Test]
        public async Task TheClient_ConnectsOverServerSentEvents_AndDeliversToItsHandlers()
        {
            await using var setup = await StartAsync();
            setup.Client.Start();
            await Connected(setup, SignalConnectionNames.ServerSentEvents);

            await setup.Chat.InboxChangedAsync("u1");
            await setup.Chat.InboxChangedAsync("u2");

            await WaitFor(() => setup.Handler.Calls.Count == 1, "the inbox signal");
            await Assert.That(setup.Handler.Calls.Single()).IsEqualTo("inbox u1");
            await Assert.That(setup.Dispatcher.Cursor).IsGreaterThan(0);
        }

        [Test]
        public async Task StreamsAnsweredWithErrors_MoveOnToPolling_AfterTwoTries()
        {
            await using var setup = await StartAsync(mode: "error-streams");
            setup.Client.Start();

            await WaitFor(() => setup.Client.LastError is SignalConnectionException { StatusCode: 500 }, "the first error");
            await WaitFor(() => setup.ClientTime.PendingTimers > 0, "the wait before trying again");
            setup.ClientTime.Advance(TimeSpan.FromSeconds(1));
            await Connected(setup, SignalConnectionNames.Polling);
        }

        [Test]
        public async Task AServerOutOfReach_IsWaitedOut_WithoutMovingOn()
        {
            await using var setup = await StartAsync(mode: "unreachable-streams");
            setup.Client.Start();

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await WaitFor(() => setup.Behaviour.Requests >= attempt && setup.Client.State == SignalClientState.Reconnecting && setup.ClientTime.PendingTimers > 0, $"attempt {attempt} failed and its wait");
                setup.ClientTime.Advance(TimeSpan.FromSeconds(4));
            }

            await Assert.That(setup.Client.ConnectionName).IsEqualTo(SignalConnectionNames.ServerSentEvents);
            await Assert.That(setup.Client.State).IsNotEqualTo(SignalClientState.Connected);

            // The fourth attempt was let go by the last advance: wait for it to fail and set its timer, or advancing the clock
            // below could come before that timer exists and move nothing.
            await WaitFor(() => setup.Behaviour.Requests >= 4 && setup.Client.State == SignalClientState.Reconnecting && setup.ClientTime.PendingTimers > 0, "attempt 4 failed and its wait");
            setup.Behaviour.Mode = "normal";
            setup.ClientTime.Advance(TimeSpan.FromSeconds(10));
            await Connected(setup, SignalConnectionNames.ServerSentEvents);
        }

        [Test]
        public async Task APageSubscription_ReconnectsWithItsTopic()
        {
            await using var setup = await StartAsync();
            setup.Client.Start();
            await Connected(setup, SignalConnectionNames.ServerSentEvents);

            var heard = new List<int>();
            using var subscription = setup.Dispatcher.TestChatSignals().OnMessagesArrived(Room, (summary, _) =>
            {
                lock (heard)
                    heard.Add(summary.Count);
                return default;
            });
            setup.ClientTime.Advance(TimeSpan.FromMilliseconds(200));
            await Connected(setup, SignalConnectionNames.ServerSentEvents, times: 2);
            await setup.Chat.MessagesArrivedAsync(Room, new TestMessages(3, "Anna"));

            await WaitFor(() => { lock (heard) return heard.Count == 1; }, "the room message");
            await Assert.That(heard.Single()).IsEqualTo(3);
        }

        [Test]
        public async Task WhenStreamsAreRefused_ItPolls()
        {
            await using var setup = await StartAsync(mode: "refuse-streams");
            setup.Client.Start();
            await Connected(setup, SignalConnectionNames.Polling);

            await setup.Chat.InboxChangedAsync("u1");

            await WaitFor(() => setup.Handler.Calls.Count == 1, "the inbox signal by polling");
            await Assert.That(setup.Client.LastError).IsNull();
        }

        [Test]
        public async Task WhenAStreamSaysNothing_ItPolls()
        {
            await using var setup = await StartAsync(mode: "silent-streams");
            setup.Client.Start();

            await WaitFor(() => setup.ClientTime.PendingTimers > 0 && setup.Behaviour.Requests > 0, "the stream request");
            setup.ClientTime.Advance(TimeSpan.FromSeconds(10));
            await Connected(setup, SignalConnectionNames.Polling);
        }

        [Test]
        public async Task ARefusedCaller_IsRetried_WithoutMovingOn()
        {
            await using var setup = await StartAsync(mode: "deny");
            setup.Client.Start();

            await WaitFor(() => setup.Client.LastError is SignalConnectionException { StatusCode: 401 }, "the first refusal");
            await WaitFor(() => setup.ClientTime.PendingTimers > 0, "the wait before trying again");
            setup.ClientTime.Advance(TimeSpan.FromSeconds(1));
            await WaitFor(() => setup.Behaviour.Requests >= 2, "the second attempt");
            await WaitFor(() => setup.Behaviour.Requests >= 2 && setup.Client.State == SignalClientState.Reconnecting && setup.ClientTime.PendingTimers > 0, "the wait after the second refusal");

            await Assert.That(setup.Client.ConnectionName).IsEqualTo(SignalConnectionNames.ServerSentEvents);
            await Assert.That(setup.Client.State).IsEqualTo(SignalClientState.Reconnecting);

            setup.Behaviour.Mode = "normal";
            setup.ClientTime.Advance(TimeSpan.FromSeconds(2));
            await Connected(setup, SignalConnectionNames.ServerSentEvents);
        }

        [Test]
        public async Task WhenTheServerEndsTheStream_ItReconnects_FromItsCursor()
        {
            await using var setup = await StartAsync(expiringTokens: true);
            setup.Client.Start();
            await Connected(setup, SignalConnectionNames.ServerSentEvents);
            await setup.Chat.InboxChangedAsync("u1");
            await WaitFor(() => setup.Handler.Calls.Count == 1, "the first signal");

            setup.ClientTime.Advance(TimeSpan.FromSeconds(3));
            setup.ServerTime.Advance(TimeSpan.FromMinutes(15));
            await Connected(setup, SignalConnectionNames.ServerSentEvents, times: 2);
            await setup.Chat.InboxChangedAsync("u1");

            await WaitFor(() => setup.Handler.Calls.Count == 2, "the second signal");
            await Task.Delay(50);
            await Assert.That(setup.Handler.Calls.Count).IsEqualTo(2);
        }

        [Test]
        public async Task APausedClient_Disconnects_AndCatchesUpWhenResumed()
        {
            await using var setup = await StartAsync();
            setup.Client.Start();
            await Connected(setup, SignalConnectionNames.ServerSentEvents);

            setup.Client.Pause();
            await WaitFor(() => setup.Client.State == SignalClientState.Paused && setup.FanOut.ListenerCount == 0, "paused and gone from the server");
            await setup.Chat.InboxChangedAsync("u1");
            await Task.Delay(50);
            await Assert.That(setup.Handler.Calls.Count).IsEqualTo(0);

            setup.Client.Resume();
            await WaitFor(() => setup.Handler.Calls.Count == 1, "the signal sent while paused");
        }

        [Test]
        public async Task AReconnect_DoesNotCancelAHandlerHalfway()
        {
            await using var setup = await StartAsync();
            setup.Client.Start();
            await Connected(setup, SignalConnectionNames.ServerSentEvents);

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool? cancelled = null;
            using var slow = setup.Dispatcher.TestChatSignals().OnMessagesArrived(Room, async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task;
                cancelled = cancellationToken.IsCancellationRequested;
            });
            setup.ClientTime.Advance(TimeSpan.FromMilliseconds(200));
            await Connected(setup, SignalConnectionNames.ServerSentEvents, times: 2);

            await setup.Chat.MessagesArrivedAsync(Room, new TestMessages(1, "slow"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // A page opens another room while the handler is busy: the connection starts again with the new topics.
            using var other = setup.Dispatcher.TestChatSignals().OnMessagesArrived(Guid.NewGuid(), (_, _) => default);
            setup.ClientTime.Advance(TimeSpan.FromMilliseconds(200));
            release.SetResult();

            await Connected(setup, SignalConnectionNames.ServerSentEvents, times: 3);
            await Assert.That(cancelled).IsEqualTo(false);
        }

        [Test]
        public async Task WithPauseWhenHidden_ItFollowsVisibility()
        {
            await using var setup = await StartAsync(o => o.PauseWhenHidden = true);
            setup.Client.Start();
            await Connected(setup, SignalConnectionNames.ServerSentEvents);

            setup.Environment.SetVisible(false);
            await WaitFor(() => setup.Client.State == SignalClientState.Paused, "paused while hidden");
            setup.Environment.SetVisible(true);
            await Connected(setup, SignalConnectionNames.ServerSentEvents, times: 2);
        }

        [Test]
        public async Task StoppingTheClient_EndsItsConnection()
        {
            await using var setup = await StartAsync();
            setup.Client.Start();
            await Connected(setup, SignalConnectionNames.ServerSentEvents);

            await setup.Client.StopAsync();

            await Assert.That(setup.Client.State).IsEqualTo(SignalClientState.Stopped);
            await WaitFor(() => setup.FanOut.ListenerCount == 0, "the listener gone from the server");
        }
    }
}
