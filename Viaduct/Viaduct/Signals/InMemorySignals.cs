using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// Keeps signals in this process: for development, tests and a single server. Lost on restart, which a client sees as
    /// a reset.
    /// </summary>
    /// <remarks>
    /// Ids start from the time the store was made, so a cursor from before a restart is recognised as from another life and
    /// answered with a reset, rather than taken as current.
    /// </remarks>
    public sealed class InMemorySignalStore : ISignalStore
    {
        readonly object gate = new();
        readonly List<SignalEnvelope> signals = [];
        readonly IOptionsMonitor<SignalOptions> options;

        /// <summary>The highest id that cannot be read any more: removed, or from before this store.</summary>
        long floor;
        long last;

        /// <summary>A store whose ids start from the current time.</summary>
        public InMemorySignalStore(IOptionsMonitor<SignalOptions> options, TimeProvider? time = null)
        {
            this.options = options;
            last = floor = (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds() * 1000;
        }

        /// <inheritdoc/>
        public ValueTask<SignalEnvelope> AppendAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(signal);
            cancellationToken.ThrowIfCancellationRequested();

            lock (gate)
            {
                var stored = signal with { Id = ++last, Ephemeral = false };
                signals.Add(stored);

                var capacity = Math.Max(1, options.CurrentValue.InMemoryCapacity);
                if (signals.Count > capacity)
                {
                    var excess = signals.Count - capacity;
                    floor = signals[excess - 1].Id;
                    signals.RemoveRange(0, excess);
                }
                return new(stored);
            }
        }

        /// <inheritdoc/>
        public ValueTask<SignalBatch> ReadAsync(long after, IReadOnlySet<string>? topics, int max, CancellationToken cancellationToken = default)
        {
            if (max <= 0)
                throw new ArgumentOutOfRangeException(nameof(max), max, "Read at least one signal.");
            cancellationToken.ThrowIfCancellationRequested();

            lock (gate)
            {
                if (after < floor || after > last)
                    return new(new SignalBatch([], last, Reset: true));

                var found = new List<SignalEnvelope>();
                var cursor = after;
                var start = FirstAfter(after);
                for (var i = start; i < signals.Count; i++)
                {
                    var signal = signals[i];
                    if (topics is null || topics.Contains(signal.Topic))
                    {
                        if (found.Count == max)
                            return new(new SignalBatch(found, cursor, More: true));
                        found.Add(signal);
                    }
                    cursor = signal.Id;
                }
                return new(new SignalBatch(found, cursor));
            }
        }

        /// <inheritdoc/>
        public ValueTask<long> GetCursorAsync(CancellationToken cancellationToken = default)
        {
            lock (gate)
                return new(last);
        }

        /// <inheritdoc/>
        public ValueTask<int> RemoveBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                var count = 0;
                while (count < signals.Count && signals[count].At < before)
                    count++;
                if (count > 0)
                {
                    floor = signals[count - 1].Id;
                    signals.RemoveRange(0, count);
                }
                return new(count);
            }
        }

        /// <summary>The index of the first signal with an id above <paramref name="after"/>; ids are in order.</summary>
        int FirstAfter(long after)
        {
            int low = 0, high = signals.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (signals[middle].Id <= after)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }
    }

    /// <summary>The bus of a single server: what is announced reaches this process's subscribers only.</summary>
    public sealed class InProcessSignalBus : ISignalBus
    {
        readonly object gate = new();
        ISignalBusSubscriber[] subscribers = [];

        /// <inheritdoc/>
        public async ValueTask AnnounceAsync(CancellationToken cancellationToken = default)
        {
            foreach (var subscriber in Volatile.Read(ref subscribers))
                await subscriber.OnStoredAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async ValueTask SendEphemeralAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(signal);
            foreach (var subscriber in Volatile.Read(ref subscribers))
                await subscriber.OnEphemeralAsync(signal, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public IDisposable Subscribe(ISignalBusSubscriber subscriber)
        {
            ArgumentNullException.ThrowIfNull(subscriber);
            lock (gate)
                subscribers = [.. subscribers, subscriber];
            return new Unsubscriber(this, subscriber);
        }

        sealed class Unsubscriber(InProcessSignalBus bus, ISignalBusSubscriber subscriber) : IDisposable
        {
            public void Dispose()
            {
                lock (bus.gate)
                    bus.subscribers = Array.FindAll(bus.subscribers, s => !ReferenceEquals(s, subscriber));
            }
        }
    }
}
