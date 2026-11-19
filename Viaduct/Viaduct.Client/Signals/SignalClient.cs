using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>Where a client's connection stands.</summary>
    public enum SignalClientState
    {
        /// <summary>Not started, or stopped.</summary>
        Stopped,

        /// <summary>Connecting for the first time.</summary>
        Connecting,

        /// <summary>Hearing signals.</summary>
        Connected,

        /// <summary>The connection ended or failed; trying again.</summary>
        Reconnecting,

        /// <summary>Held off on purpose, such as an app in the background; catches up when resumed.</summary>
        Paused,
    }

    /// <summary>
    /// A client's connection to the server's signals: keeps one open, as the first way in
    /// <see cref="SignalClientOptions.Connections"/> that gets through, and hands what arrives to the
    /// <see cref="SignalDispatcher"/>. What a connection indicator shows.
    /// </summary>
    public interface ISignalClient
    {
        /// <summary>Where the connection stands.</summary>
        SignalClientState State { get; }

        /// <summary>The way it is connected or connecting, such as <c>sse</c>; null when stopped.</summary>
        string? ConnectionName { get; }

        /// <summary>Why the last attempt failed; null once connected.</summary>
        Exception? LastError { get; }

        /// <summary>Page topics the server refused on the current connection.</summary>
        IReadOnlyList<string> RefusedTopics { get; }

        /// <summary>Raised when <see cref="State"/>, <see cref="ConnectionName"/> or <see cref="RefusedTopics"/> changed.</summary>
        event Action? StateChanged;

        /// <summary>Starts connecting, in the background; again after <see cref="StopAsync"/>. Does nothing when started.</summary>
        void Start();

        /// <summary>Disconnects and stops.</summary>
        Task StopAsync(CancellationToken cancellationToken = default);

        /// <summary>Disconnects until <see cref="Resume"/>, then catches up from the cursor.</summary>
        void Pause();

        /// <summary>Connects again after <see cref="Pause"/>.</summary>
        void Resume();
    }

    /// <summary>
    /// The connection loop: connects, reconnects with a doubling wait after failures, moves on to the next way when one does
    /// not get through and tries the first again later, reconnects when the page topics change, and pauses while hidden
    /// when asked.
    /// </summary>
    public sealed class SignalClient : ISignalClient, IAsyncDisposable
    {
        /// <summary>A connection that ends sooner than this after opening counts as a failure, so a server ending at once is not hammered.</summary>
        static readonly TimeSpan ShortRun = TimeSpan.FromSeconds(2);

        readonly SignalDispatcher dispatcher;
        readonly ISignalConnection[] connections;
        readonly IOptionsMonitor<SignalClientOptions> options;
        readonly ISignalClientEnvironment environment;
        readonly TimeProvider time;
        readonly ILogger logger;
        readonly Lock gate = new();

        CancellationTokenSource? stopping;
        Task? loop;
        CancellationTokenSource? currentRun;
        HashSet<string> currentTopics = new(StringComparer.Ordinal);
        TaskCompletionSource? resumed;
        bool pausedByHand, pausedWhileHidden, restartFromFirst, metered;
        ITimer? topicsTimer;
        SignalClientState state;
        string? connectionName;
        IReadOnlyList<string> refused = [];

        /// <summary>A client for this dispatcher over the registered connections.</summary>
        public SignalClient(SignalDispatcher dispatcher, IEnumerable<ISignalConnection> connections, IOptionsMonitor<SignalClientOptions> options,
            ISignalClientEnvironment environment, TimeProvider time, ILogger<SignalClient>? logger = null)
        {
            this.dispatcher = dispatcher;
            this.connections = [.. connections];
            this.options = options;
            this.environment = environment;
            this.time = time;
            this.logger = logger ?? NullLogger<SignalClient>.Instance;
            dispatcher.TopicsChanged += OnTopicsChanged;
            environment.Changed += OnEnvironmentChanged;
            pausedWhileHidden = options.CurrentValue.PauseWhenHidden && !environment.IsVisible;
            metered = environment.IsMetered;
        }

        /// <inheritdoc/>
        public SignalClientState State
        {
            get { lock (gate) return state; }
        }

        /// <inheritdoc/>
        public string? ConnectionName
        {
            get { lock (gate) return connectionName; }
        }

        /// <inheritdoc/>
        public Exception? LastError { get; private set; }

        /// <inheritdoc/>
        public IReadOnlyList<string> RefusedTopics
        {
            get { lock (gate) return refused; }
        }

        /// <inheritdoc/>
        public event Action? StateChanged;

        /// <inheritdoc/>
        public void Start()
        {
            lock (gate)
            {
                if (loop is not null)
                    return;
                stopping = new CancellationTokenSource();
                var token = stopping.Token;
                loop = Task.Run(() => RunAsync(token));
            }
        }

        /// <inheritdoc/>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Task? running;
            lock (gate)
            {
                running = loop;
                stopping?.Cancel();
                loop = null;
            }
            if (running is not null)
                await running.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                stopping?.Dispose();
                stopping = null;
            }
        }

        /// <inheritdoc/>
        public void Pause()
        {
            lock (gate)
                pausedByHand = true;
            Restart();
        }

        /// <inheritdoc/>
        public void Resume()
        {
            lock (gate)
                pausedByHand = false;
            WakeIfResumed();
        }

        async Task RunAsync(CancellationToken cancellationToken)
        {
            var failures = 0;
            var index = 0;
            var everConnected = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                var settings = options.CurrentValue;

                Task? waitForResume = null;
                lock (gate)
                {
                    if (pausedByHand || pausedWhileHidden)
                    {
                        resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        waitForResume = resumed.Task;
                    }
                    if (restartFromFirst)
                    {
                        restartFromFirst = false;
                        index = 0;
                    }
                }
                if (waitForResume is not null)
                {
                    SetState(SignalClientState.Paused, null);
                    try
                    {
                        await waitForResume.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }

                var order = Order(settings);
                if (order.Count == 0)
                {
                    LastError = new InvalidOperationException("None of the configured signal connections is registered.");
                    logger.LogError("None of the signal connections {Connections} is registered.", string.Join(", ", settings.Connections));
                    break;
                }
                if (index >= order.Count)
                    index = 0;

                var connection = order[index];
                SetState(everConnected ? SignalClientState.Reconnecting : SignalClientState.Connecting, connection.Name);

                using var retryFirst = index > 0 ? new CancellationTokenSource(settings.RetryPreferredAfter, time) : null;
                using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retryFirst?.Token ?? CancellationToken.None);
                IReadOnlyCollection<string> topics;
                lock (gate)
                {
                    currentRun = run;
                    topics = dispatcher.SubscribedTopics;
                    currentTopics = new HashSet<string>(topics, StringComparer.Ordinal);
                }

                var cursor = dispatcher.Cursor;
                var request = new SignalConnectRequest(cursor > 0 ? cursor : null, topics);
                DateTimeOffset? openedAt = null;
                try
                {
                    await foreach (var happened in connection.RunAsync(request, run.Token).ConfigureAwait(false))
                    {
                        switch (happened)
                        {
                            case SignalConnectionOpened opened:
                                openedAt = time.GetUtcNow();
                                everConnected = true;
                                LastError = null;
                                dispatcher.StartAt(opened.Cursor);
                                lock (gate)
                                    refused = opened.Refused;
                                SetState(SignalClientState.Connected, connection.Name);
                                break;
                            case SignalConnectionReceived received:
                                // With the client's lifetime, not this connection's: a reconnect (new page topics, a page
                                // navigated away) must not cancel a handler halfway, such as permissions being reloaded.
                                await dispatcher.ReceiveAsync(received.Signal, cancellationToken).ConfigureAwait(false);
                                break;
                        }
                    }

                    // The server ended it, such as when the access token expired: reconnect at once, unless it ends at once.
                    failures = openedAt is { } at && time.GetUtcNow() - at >= ShortRun ? 0 : failures + 1;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException) when (run.IsCancellationRequested)
                {
                    // Restarted on purpose: new page topics, paused, or time to try the first way again.
                    if (retryFirst?.IsCancellationRequested == true)
                        index = 0;
                    failures = 0;
                    continue;
                }
                catch (Exception exception)
                {
                    LastError = exception;
                    failures = openedAt is null ? failures + 1 : 1;
                    logger.LogWarning(exception, "Signal connection {Connection} failed.", connection.Name);

                    // Only what says something about this way of connecting moves on to the next: an answer that is not
                    // a refusal of the caller (another way would be refused as well). A server out of reach answers nothing,
                    // and says nothing about the way: it is waited out.
                    var unsupported = exception is SignalConnectionException { Unsupported: true };
                    var waysFault = exception is SignalConnectionException { StatusCode: { } status } && status is not (401 or 403);
                    if (openedAt is null && (unsupported || (failures >= settings.FallbackAfterFailures && waysFault)) && index + 1 < order.Count)
                    {
                        index++;
                        failures = 0;
                        logger.LogInformation("Signals move on to {Connection}.", order[index].Name);
                        continue;
                    }
                }
                finally
                {
                    lock (gate)
                        currentRun = null;
                }

                if (failures > 0)
                {
                    SetState(SignalClientState.Reconnecting, connection.Name);
                    try
                    {
                        await Task.Delay(Backoff(failures, settings), time, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            SetState(SignalClientState.Stopped, null);
        }

        List<ISignalConnection> Order(SignalClientOptions settings)
        {
            var names = environment.IsMetered && settings.MeteredConnections is { } meteredNames ? meteredNames : settings.Connections;
            return [.. names.Select(name => connections.FirstOrDefault(c => c.Name == name)).OfType<ISignalConnection>()];
        }

        static TimeSpan Backoff(int failures, SignalClientOptions settings)
        {
            var seconds = Math.Min(Math.Pow(2, Math.Min(failures - 1, 16)), settings.MaxBackoff.TotalSeconds);
            if (settings.BackoffJitter > 0)
                seconds *= 1 + settings.BackoffJitter * (2 * Random.Shared.NextDouble() - 1);
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        void SetState(SignalClientState value, string? name)
        {
            bool changed;
            lock (gate)
            {
                changed = state != value || connectionName != name;
                state = value;
                connectionName = name;
            }
            if (changed)
                StateChanged?.Invoke();
        }

        /// <summary>Ends the current connection; the loop starts the next as it should.</summary>
        void Restart()
        {
            CancellationTokenSource? run;
            lock (gate)
                run = currentRun;
            try
            {
                run?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void WakeIfResumed()
        {
            TaskCompletionSource? waiting = null;
            lock (gate)
            {
                if (!pausedByHand && !pausedWhileHidden)
                {
                    waiting = resumed;
                    resumed = null;
                }
            }
            waiting?.TrySetResult();
        }

        void OnTopicsChanged()
        {
            lock (gate)
            {
                var delay = options.CurrentValue.TopicsChangeDelay;
                if (topicsTimer is null)
                    topicsTimer = time.CreateTimer(static state => ((SignalClient)state!).ApplyTopics(), this, delay, Timeout.InfiniteTimeSpan);
                else
                    topicsTimer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        void ApplyTopics()
        {
            bool differ;
            lock (gate)
                differ = currentRun is not null && !currentTopics.SetEquals(dispatcher.SubscribedTopics);
            if (differ)
                Restart();
        }

        void OnEnvironmentChanged()
        {
            var settings = options.CurrentValue;
            bool pause, resume, meteredOrder;
            lock (gate)
            {
                var hidden = settings.PauseWhenHidden && !environment.IsVisible;
                pause = hidden && !pausedWhileHidden;
                resume = !hidden && pausedWhileHidden;
                pausedWhileHidden = hidden;
                meteredOrder = settings.MeteredConnections is not null && environment.IsMetered != metered;
                metered = environment.IsMetered;
                if (meteredOrder)
                    restartFromFirst = true;
            }

            if (pause || meteredOrder)
                Restart();
            if (resume)
                WakeIfResumed();
        }

        /// <summary>Stops the connection.</summary>
        public async ValueTask DisposeAsync()
        {
            dispatcher.TopicsChanged -= OnTopicsChanged;
            environment.Changed -= OnEnvironmentChanged;
            lock (gate)
            {
                topicsTimer?.Dispose();
                topicsTimer = null;
            }
            await StopAsync().ConfigureAwait(false);
        }
    }
}
