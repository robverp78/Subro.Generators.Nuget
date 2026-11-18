using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// This server's side of the bus: reads what was stored once, and hands each signal to the listeners here whose topics
    /// it is on. A transport opens a listener per connected client.
    /// </summary>
    public sealed class SignalFanOut : ISignalBusSubscriber, IDisposable
    {
        internal const int PageSize = 500;

        readonly ISignalStore store;
        readonly ISignalBus bus;
        readonly IOptionsMonitor<SignalOptions> options;
        readonly ILogger logger;
        readonly SemaphoreSlim reading = new(1, 1);
        readonly object gate = new();

        SignalListener[] listeners = [];
        Task? starting;
        IDisposable? subscription;
        long seen;

        /// <summary>The fan-out of this server.</summary>
        public SignalFanOut(ISignalStore store, ISignalBus bus, IOptionsMonitor<SignalOptions> options, ILogger<SignalFanOut>? logger = null)
        {
            this.store = store;
            this.bus = bus;
            this.options = options;
            this.logger = logger ?? NullLogger<SignalFanOut>.Instance;
        }

        /// <summary>How many listeners are open here.</summary>
        public int ListenerCount => Volatile.Read(ref listeners).Length;

        /// <summary>
        /// Opens a listener on <paramref name="topics"/>. With a cursor, it first gets what was stored after it (or a reset
        /// when that is gone); without one, it starts from now. Dispose it when the client leaves.
        /// </summary>
        public ValueTask<SignalListener> ListenAsync(long? after, IReadOnlySet<string> topics, CancellationToken cancellationToken = default)
            => ListenAsync(after, topics, resolveTopicsAgainOn: null, cancellationToken);

        /// <summary>
        /// As <see cref="ListenAsync(long?, IReadOnlySet{string}, CancellationToken)"/>, for a reader that works the topics out
        /// again after the signals of <paramref name="resolveTopicsAgainOn"/>; see <see cref="SignalListener.ResolveTopicsAgainOn"/>.
        /// </summary>
        public async ValueTask<SignalListener> ListenAsync(long? after, IReadOnlySet<string> topics, IReadOnlySet<string>? resolveTopicsAgainOn,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(topics);
            await EnsureStartedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

            // Registered while no signals are being handed out, so everything handed out later is newer than `seen`.
            await reading.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var listener = new SignalListener(this, store, topics, after, seen, Math.Max(1, options.CurrentValue.ListenerCapacity), resolveTopicsAgainOn);
                lock (gate)
                    listeners = [.. listeners, listener];
                return listener;
            }
            finally
            {
                reading.Release();
            }
        }

        internal void Remove(SignalListener listener)
        {
            lock (gate)
                listeners = Array.FindAll(listeners, l => !ReferenceEquals(l, listener));
        }

        Task EnsureStartedAsync()
        {
            // A start that failed (the store out of reach) is tried again by the next caller, rather than kept failing.
            lock (gate)
            {
                if (starting is null || starting.IsFaulted || starting.IsCanceled)
                    starting = StartAsync();
                return starting;
            }
        }

        async Task StartAsync()
        {
            // Subscribed before the cursor is read, so nothing stored in between is missed; once, however often it starts.
            lock (gate)
                subscription ??= bus.Subscribe(this);
            seen = await store.GetCursorAsync().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async ValueTask OnStoredAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await EnsureStartedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                await reading.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await HandOutAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    reading.Release();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The signal is stored: a client catches up at its next connection. The publisher is not failed for it.
                logger.LogError(exception, "Could not hand out stored signals after {Cursor}.", seen);
            }
        }

        async ValueTask HandOutAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = Volatile.Read(ref listeners);
                if (current.Length == 0)
                {
                    seen = await store.GetCursorAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var batch = await store.ReadAsync(seen, null, PageSize, cancellationToken).ConfigureAwait(false);
                if (batch.Reset)
                {
                    // This server fell behind what is stored: every listener here lost track.
                    foreach (var listener in current)
                        listener.Offer(SignalEnvelope.Reset(batch.Cursor));
                    seen = batch.Cursor;
                    return;
                }

                foreach (var signal in batch.Signals)
                    foreach (var listener in current)
                        listener.Offer(signal);

                seen = batch.Cursor;
                if (!batch.More)
                    return;
            }
        }

        /// <inheritdoc/>
        public ValueTask OnEphemeralAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(signal);
            foreach (var listener in Volatile.Read(ref listeners))
                listener.Offer(signal);
            return default;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            subscription?.Dispose();
            foreach (var listener in Volatile.Read(ref listeners))
                listener.Dispose();
        }
    }

    /// <summary>
    /// One client's stream of signals on this server: what it missed after its cursor, then what arrives live, in order of
    /// id and each once. Read it with <see cref="ReadAllAsync"/>; dispose it when the client leaves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A listener that falls too far behind (<see cref="SignalOptions.ListenerCapacity"/>) ends with
    /// <see cref="Overflowed"/>; its client reconnects with its cursor and loses nothing.
    /// </para>
    /// <para>
    /// With <see cref="ResolveTopicsAgainOn"/>, such a signal (<see cref="IViaductSignals.TopicsChangedAsync"/> for its user,
    /// say) makes it hold everything that follows until <see cref="SetTopics"/> gives the new topics, so a signal sent right
    /// after joining a room is not filtered with the old ones.
    /// </para>
    /// </remarks>
    public sealed class SignalListener : IDisposable
    {
        readonly SignalFanOut fanOut;
        readonly ISignalStore store;
        readonly Channel<SignalEnvelope> channel;
        readonly int capacity;
        readonly object gate = new();
        readonly long? catchUpFrom;
        readonly IReadOnlySet<string>? holdOn;

        IReadOnlySet<string> topics;
        /// <summary>What arrived while catching up or waiting for topics, not yet filtered.</summary>
        List<SignalEnvelope>? held;
        bool catchingUp, waitingForTopics;
        long lastId;
        int reading;

        internal SignalListener(SignalFanOut fanOut, ISignalStore store, IReadOnlySet<string> topics, long? after, long seen, int capacity, IReadOnlySet<string>? resolveTopicsAgainOn)
        {
            this.fanOut = fanOut;
            this.store = store;
            this.topics = topics;
            this.capacity = capacity;
            holdOn = resolveTopicsAgainOn is { Count: > 0 } ? resolveTopicsAgainOn : null;
            channel = Channel.CreateBounded<SignalEnvelope>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

            if (after is long from)
            {
                catchUpFrom = from;
                lastId = from;
                catchingUp = true;
                held = [];
            }
            else
                lastId = seen;
        }

        /// <summary>The cursor of the last signal handed to this listener's client, or where it started.</summary>
        public long Cursor
        {
            get { lock (gate) return lastId; }
        }

        /// <summary>The topics it listens to.</summary>
        public IReadOnlySet<string> Topics => Volatile.Read(ref topics);

        /// <summary>Ended because its client did not keep up. The client reconnects with its cursor.</summary>
        public bool Overflowed { get; private set; }

        /// <summary>
        /// The signals after which what follows is held until <see cref="SetTopics"/> is called: for a reader that works the
        /// topics out again then, as the signal endpoint does; given when it is opened. Null for none.
        /// </summary>
        public IReadOnlySet<string>? ResolveTopicsAgainOn => holdOn;

        /// <summary>Changes the topics, and lets through what was held for them.</summary>
        public void SetTopics(IReadOnlySet<string> topics)
        {
            ArgumentNullException.ThrowIfNull(topics);
            lock (gate)
            {
                Volatile.Write(ref this.topics, topics);
                waitingForTopics = false;
                if (!catchingUp)
                    Release();
            }
        }

        /// <summary>
        /// The signals for this client: first what it missed after its cursor (or a reset when that is gone), then what
        /// arrives, until it is disposed or overflows. Read once.
        /// </summary>
        public async IAsyncEnumerable<SignalEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref reading, 1) != 0)
                throw new InvalidOperationException("A signal listener is read once.");

            if (catchUpFrom is long from)
            {
                var cursor = from;
                var more = true;
                while (more && !Overflowed)
                {
                    var batch = await store.ReadAsync(cursor, Topics, SignalFanOut.PageSize, cancellationToken).ConfigureAwait(false);
                    if (batch.Reset)
                    {
                        lock (gate)
                            lastId = batch.Cursor;
                        cursor = batch.Cursor;
                        yield return SignalEnvelope.Reset(batch.Cursor);
                        break;
                    }

                    var restarted = false;
                    foreach (var signal in batch.Signals)
                    {
                        if (!Accept(signal))
                            continue;
                        yield return signal;

                        // The reader may have changed the topics: read on with them, from here.
                        if (holdOn?.Contains(signal.Type) == true)
                        {
                            cursor = signal.Id;
                            restarted = true;
                            break;
                        }
                    }

                    if (!restarted)
                    {
                        cursor = batch.Cursor;
                        more = batch.More;
                    }
                }

                // Caught up: what arrived meanwhile follows, without what the store already gave.
                lock (gate)
                {
                    if (cursor > lastId)
                        lastId = cursor;
                    catchingUp = false;
                    if (!waitingForTopics)
                        Release();
                }
            }

            await foreach (var signal in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return signal;
        }

        /// <summary>Hands a signal to this listener.</summary>
        internal void Offer(SignalEnvelope signal)
        {
            lock (gate)
            {
                if (held is not null)
                {
                    if (held.Count >= capacity)
                        Overflow();
                    else
                        held.Add(signal);
                    return;
                }
                Deliver(signal);
            }
        }

        /// <summary>Writes a signal when it is on the topics; after one of <see cref="ResolveTopicsAgainOn"/>, starts holding. Under the lock.</summary>
        void Deliver(SignalEnvelope signal)
        {
            if (!signal.IsReset && !Topics.Contains(signal.Topic))
                return;
            Write(signal);
            if (holdOn?.Contains(signal.Type) == true && !Overflowed)
            {
                waitingForTopics = true;
                held = [];
            }
        }

        /// <summary>Delivers what was held, in order; one of <see cref="ResolveTopicsAgainOn"/> among it holds the rest again. Under the lock.</summary>
        void Release()
        {
            if (held is not { } waiting)
                return;
            held = null;
            foreach (var signal in waiting)
            {
                if (held is not null)
                    held.Add(signal);
                else
                    Deliver(signal);
            }
        }

        /// <summary>Whether a signal is new to this client, moving the cursor past it.</summary>
        bool Accept(SignalEnvelope signal)
        {
            lock (gate)
            {
                if (!IsNew(signal))
                    return false;
                Advance(signal);
                return true;
            }
        }

        bool IsNew(SignalEnvelope signal) => signal.IsReset || signal.Ephemeral || signal.Id > lastId;

        void Advance(SignalEnvelope signal)
        {
            if (signal.IsReset || !signal.Ephemeral)
                lastId = signal.Id;
        }

        /// <summary>Writes a new signal to the stream; the cursor only moves past what was written. Under the lock.</summary>
        void Write(SignalEnvelope signal)
        {
            if (Overflowed || !IsNew(signal))
                return;
            if (channel.Writer.TryWrite(signal))
                Advance(signal);
            else
                Overflow();
        }

        void Overflow()
        {
            Overflowed = true;
            held = null;
            channel.Writer.TryComplete();
        }

        /// <summary>Stops listening: the stream ends.</summary>
        public void Dispose()
        {
            fanOut.Remove(this);
            channel.Writer.TryComplete();
        }
    }
}
