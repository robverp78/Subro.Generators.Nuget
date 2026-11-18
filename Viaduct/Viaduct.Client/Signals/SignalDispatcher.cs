using Microsoft.Extensions.DependencyInjection;
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
    /// <summary>
    /// A client's handler for one contract: how the dispatcher gets the instance. Added by
    /// <see cref="SignalClientRegistration.AddSignalHandler{TContract, THandler}"/>.
    /// </summary>
    public sealed class SignalHandlerRegistration(SignalContract contract, Func<IServiceProvider, object> resolve)
    {
        /// <summary>The contract it handles.</summary>
        public SignalContract Contract { get; } = contract;

        /// <summary>Gets the handler from the client's services.</summary>
        public Func<IServiceProvider, object> Resolve { get; } = resolve;
    }

    /// <summary>
    /// The client's side of signals, between the connection and the handlers: keeps the cursor, drops what it already had,
    /// holds and merges signals as their delivery says, and calls the app-wide handlers and the page subscriptions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Signals are delivered one at a time, so handlers do not run at once. A handler must therefore not wait for another
    /// signal to be delivered. A failing handler is logged and does not stop the others.
    /// </para>
    /// <para>
    /// What is held is keyed by kind of signal and topic: what arrives meanwhile merges into it
    /// (<see cref="ISignalMergeable{T}"/>, else the latest wins), and <see cref="SignalInfo.Count"/> says how many.
    /// </para>
    /// </remarks>
    public sealed class SignalDispatcher : ISignalSubscriptions, IDisposable
    {
        readonly IServiceProvider services;
        readonly SignalHandlerRegistration[] handlers;
        readonly SignalClientOptions options;
        readonly ISignalClientEnvironment environment;
        readonly ISignalDeliveryPolicy policy;
        readonly TimeProvider time;
        readonly ILogger logger;
        readonly SemaphoreSlim delivering = new(1, 1);
        readonly object gate = new();
        readonly Dictionary<(string Key, string Topic), Held> held = [];
        readonly List<Subscription> subscriptions = [];
        long cursor;
        bool disposed;

        /// <summary>A dispatcher over the registered handlers.</summary>
        public SignalDispatcher(IServiceProvider services, IEnumerable<SignalHandlerRegistration> handlers, IOptions<SignalClientOptions> options,
            ISignalClientEnvironment environment, ISignalDeliveryPolicy policy, TimeProvider time, ILogger<SignalDispatcher>? logger = null)
        {
            this.services = services;
            this.handlers = [.. handlers];
            this.options = options.Value;
            this.environment = environment;
            this.policy = policy;
            this.time = time;
            this.logger = logger ?? NullLogger<SignalDispatcher>.Instance;
            environment.Changed += OnEnvironmentChanged;
        }

        /// <summary>The id of the latest stored signal received: what a connection asks to continue after. 0 before any.</summary>
        public long Cursor
        {
            get { lock (gate) return cursor; }
        }

        /// <summary>
        /// Where a new connection starts: the cursor moves there unless it is past it already, so a reconnect catches up
        /// from it even when nothing arrived yet.
        /// </summary>
        public void StartAt(long cursor)
        {
            lock (gate)
                if (cursor > this.cursor)
                    this.cursor = cursor;
        }

        /// <summary>The topics pages subscribed to, which a connection asks the server for besides its base topics.</summary>
        public IReadOnlyCollection<string> SubscribedTopics
        {
            get { lock (gate) return [.. subscriptions.Select(static s => s.Topic).Distinct(StringComparer.Ordinal)]; }
        }

        /// <summary>Raised when <see cref="SubscribedTopics"/> changed.</summary>
        public event Action? TopicsChanged;

        /// <summary>
        /// Takes a signal from the connection: drops it when already had, else delivers it now or holds it, as its delivery
        /// says. A reset moves the cursor and tells the handlers and subscriptions to load again.
        /// </summary>
        public async ValueTask ReceiveAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(signal);
            ObjectDisposedException.ThrowIf(disposed, this);

            if (signal.IsReset)
            {
                lock (gate)
                {
                    cursor = signal.Id;
                    foreach (var item in held.Values)
                        item.Timer?.Dispose();
                    held.Clear();
                }
                await ResetAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!signal.Ephemeral)
                lock (gate)
                {
                    if (signal.Id <= cursor)
                        return;
                    cursor = signal.Id;
                }

            var type = SignalContracts.FindType(signal.Type);
            if (type is null)
            {
                logger.LogDebug("Ignored signal {Type}, which no contract here knows.", signal.Type);
                return;
            }

            object? payload;
            try
            {
                payload = type.Contract.Decode(type, signal.Data);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not read the payload of signal {Type} on {Topic}.", signal.Type, signal.Topic);
                return;
            }

            var delivery = policy.Choose(type, signal.Topic, options.DeliveryFor(type), environment);
            var key = (type.Key, signal.Topic);
            Held? now = null;

            lock (gate)
            {
                held.TryGetValue(key, out var item);
                if (item is null)
                    item = new Held(type, signal, payload, delivery);
                else
                    item.Add(signal, payload, delivery);

                switch (delivery.Kind)
                {
                    case SignalDeliveryKind.Debounce:
                        held[key] = item;
                        item.Restart(this, key, delivery.Time);
                        break;
                    case SignalDeliveryKind.Batch:
                        held[key] = item;
                        item.StartOnce(this, key, delivery.Time);
                        break;
                    case SignalDeliveryKind.Manual:
                    case SignalDeliveryKind.WhenVisible when !environment.IsVisible:
                        held[key] = item;
                        break;
                    default:
                        held.Remove(key);
                        item.Timer?.Dispose();
                        now = item;
                        break;
                }
            }

            if (now is not null)
                await DeliverAsync(now, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Delivers what is held now, whatever its delivery: everything, or only of <paramref name="type"/>, or only on
        /// <paramref name="topic"/>. For signals delivered <see cref="SignalDeliveryKind.Manual"/>ly, when the page wants them.
        /// </summary>
        public async ValueTask FlushAsync(SignalTypeInfo? type = null, string? topic = null, CancellationToken cancellationToken = default)
        {
            foreach (var item in Take(item => (type is null || item.Type == type) && (topic is null || item.Topic == topic)))
                await DeliverAsync(item, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>How many signals are held, by kind and topic.</summary>
        public int HeldCount
        {
            get { lock (gate) return held.Count; }
        }

        /// <inheritdoc/>
        public IDisposable Subscribe(SignalTypeInfo type, string topic, Func<object?, CancellationToken, ValueTask> onSignal,
            Func<CancellationToken, ValueTask>? onReset = null)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(topic);
            ArgumentNullException.ThrowIfNull(onSignal);
            ObjectDisposedException.ThrowIf(disposed, this);

            var subscription = new Subscription(this, type, topic, onSignal, onReset);
            bool added;
            lock (gate)
            {
                added = !subscriptions.Any(s => s.Topic == topic);
                subscriptions.Add(subscription);
            }
            if (added)
                TopicsChanged?.Invoke();
            return subscription;
        }

        void Unsubscribe(Subscription subscription)
        {
            bool removed;
            lock (gate)
                removed = subscriptions.Remove(subscription) && !subscriptions.Any(s => s.Topic == subscription.Topic);
            if (removed)
                TopicsChanged?.Invoke();
        }

        async ValueTask DeliverAsync(Held item, CancellationToken cancellationToken)
        {
            await delivering.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SignalInfo.SetCurrent(new SignalInfo(item.Type, item.Topic, item.LastId, item.LastAt, item.FirstAt, item.Count));

                foreach (var registration in handlers)
                {
                    if (registration.Contract != item.Type.Contract)
                        continue;
                    try
                    {
                        var handler = registration.Resolve(services);
                        await item.Type.Contract.InvokeAsync(handler, item.Type, item.Topic, item.Payload, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        logger.LogError(exception, "A handler of signal {Type} on {Topic} failed.", item.Type.Key, item.Topic);
                    }
                }

                Subscription[] matching;
                lock (gate)
                    matching = [.. subscriptions.Where(s => s.Type == item.Type && s.Topic == item.Topic)];

                foreach (var subscription in matching)
                {
                    try
                    {
                        await subscription.OnSignal(item.Payload, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        logger.LogError(exception, "A subscription to signal {Type} on {Topic} failed.", item.Type.Key, item.Topic);
                    }
                }
            }
            finally
            {
                SignalInfo.SetCurrent(null);
                delivering.Release();
            }
        }

        async ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            await delivering.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var registration in handlers)
                {
                    try
                    {
                        if (registration.Resolve(services) is ISignalResettable resettable)
                            await resettable.ResetAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        logger.LogError(exception, "A handler of {Contract} failed to reset.", registration.Contract);
                    }
                }

                Subscription[] current;
                lock (gate)
                    current = [.. subscriptions];

                foreach (var subscription in current)
                {
                    if (subscription.OnReset is null)
                        continue;
                    try
                    {
                        await subscription.OnReset(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        logger.LogError(exception, "A subscription to signal {Type} on {Topic} failed to reset.", subscription.Type.Key, subscription.Topic);
                    }
                }
            }
            finally
            {
                delivering.Release();
            }
        }

        List<Held> Take(Func<Held, bool> which)
        {
            lock (gate)
            {
                var taken = held.Where(pair => which(pair.Value)).ToList();
                foreach (var pair in taken)
                {
                    held.Remove(pair.Key);
                    pair.Value.Timer?.Dispose();
                }
                return [.. taken.Select(static pair => pair.Value)];
            }
        }

        void OnEnvironmentChanged()
        {
            if (environment.IsVisible)
                _ = DeliverInBackgroundAsync(Take(static item => item.Delivery.Kind == SignalDeliveryKind.WhenVisible));
        }

        void OnTimer((string Key, string Topic) key)
        {
            List<Held> due;
            lock (gate)
            {
                if (!held.TryGetValue(key, out var item))
                    return;
                held.Remove(key);
                item.Timer?.Dispose();
                due = [item];
            }
            _ = DeliverInBackgroundAsync(due);
        }

        async Task DeliverInBackgroundAsync(List<Held> items)
        {
            foreach (var item in items)
            {
                try
                {
                    await DeliverAsync(item, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Could not deliver held signal {Type} on {Topic}.", item.Type.Key, item.Topic);
                }
            }
        }

        /// <summary>Stops the timers and drops what is held.</summary>
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            environment.Changed -= OnEnvironmentChanged;
            lock (gate)
            {
                foreach (var item in held.Values)
                    item.Timer?.Dispose();
                held.Clear();
                subscriptions.Clear();
            }
        }

        /// <summary>What is held for one kind of signal on one topic: merged so far.</summary>
        sealed class Held(SignalTypeInfo type, SignalEnvelope first, object? payload, SignalDelivery delivery)
        {
            public SignalTypeInfo Type { get; } = type;
            public string Topic { get; } = first.Topic;
            public object? Payload { get; private set; } = payload;
            public int Count { get; private set; } = 1;
            public DateTimeOffset FirstAt { get; } = first.At;
            public DateTimeOffset LastAt { get; private set; } = first.At;
            public long LastId { get; private set; } = first.Id;
            public SignalDelivery Delivery { get; private set; } = delivery;
            public ITimer? Timer { get; private set; }

            public void Add(SignalEnvelope signal, object? payload, SignalDelivery delivery)
            {
                Payload = Type.Contract.Merge(Type, Payload, payload);
                Count++;
                LastAt = signal.At;
                if (signal.Id > LastId)
                    LastId = signal.Id;
                Delivery = delivery;
            }

            public void Restart(SignalDispatcher dispatcher, (string, string) key, TimeSpan due)
            {
                if (Timer is null)
                    Timer = Create(dispatcher, key, due);
                else
                    Timer.Change(due, Timeout.InfiniteTimeSpan);
            }

            public void StartOnce(SignalDispatcher dispatcher, (string, string) key, TimeSpan due) => Timer ??= Create(dispatcher, key, due);

            static ITimer Create(SignalDispatcher dispatcher, (string, string) key, TimeSpan due)
                => dispatcher.time.CreateTimer(static state => ((Action)state!)(), (Action)(() => dispatcher.OnTimer(key)), due, Timeout.InfiniteTimeSpan);
        }

        sealed class Subscription(SignalDispatcher dispatcher, SignalTypeInfo type, string topic,
            Func<object?, CancellationToken, ValueTask> onSignal, Func<CancellationToken, ValueTask>? onReset) : IDisposable
        {
            int disposed;
            public SignalTypeInfo Type { get; } = type;
            public string Topic { get; } = topic;
            public Func<object?, CancellationToken, ValueTask> OnSignal { get; } = onSignal;
            public Func<CancellationToken, ValueTask>? OnReset { get; } = onReset;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                    dispatcher.Unsubscribe(this);
            }
        }
    }
}
