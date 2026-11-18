using Microsoft.Extensions.Options;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// The server's entry to the engine: stamps a signal, stores it unless it is ephemeral, and tells every server through
    /// the bus.
    /// </summary>
    public sealed class SignalPublisher(ISignalStore store, ISignalBus bus, IOptionsMonitor<SignalOptions> options, TimeProvider? time = null)
        : ISignalPublisher
    {
        readonly TimeProvider time = time ?? TimeProvider.System;

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">The signal has no type or topic, or its payload is larger than <see cref="SignalOptions.MaxPayloadBytes"/>.</exception>
        public async ValueTask PublishAsync(SignalEnvelope signal, CancellationToken cancellationToken = default)
        {
            var stamped = Prepare(signal, options.CurrentValue, time);
            if (stamped.Ephemeral)
            {
                await bus.SendEphemeralAsync(stamped, cancellationToken).ConfigureAwait(false);
                return;
            }

            await store.AppendAsync(stamped, cancellationToken).ConfigureAwait(false);
            await bus.AnnounceAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Checks a signal the way every publisher does and stamps it with the time: for a publisher of a store's own, such
        /// as one that writes in the transaction of a save.
        /// </summary>
        /// <exception cref="ArgumentException">No type or topic, a reset, or a payload larger than <see cref="SignalOptions.MaxPayloadBytes"/>.</exception>
        public static SignalEnvelope Prepare(SignalEnvelope signal, SignalOptions options, TimeProvider time)
        {
            ArgumentNullException.ThrowIfNull(signal);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(time);
            if (string.IsNullOrEmpty(signal.Type) || string.IsNullOrEmpty(signal.Topic))
                throw new ArgumentException("A signal needs a type and a topic.", nameof(signal));
            if (signal.IsReset)
                throw new ArgumentException("A reset is the engine's own, not published.", nameof(signal));

            var max = options.MaxPayloadBytes;
            if (signal.Data is { } data && data.Length > max / 4 && Encoding.UTF8.GetByteCount(data) > max)
                throw new ArgumentException(
                    $"The payload of signal '{signal.Type}' is larger than {max} bytes. A signal says what changed; the data is fetched.", nameof(signal));

            return signal with { Id = 0, At = time.GetUtcNow() };
        }
    }
}
