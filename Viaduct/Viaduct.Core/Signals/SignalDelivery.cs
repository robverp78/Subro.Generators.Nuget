using System;

namespace Viaduct.Signals
{
    /// <summary>The ways a client can deliver a signal to its handlers.</summary>
    public enum SignalDeliveryKind
    {
        /// <summary>At once.</summary>
        Immediate,

        /// <summary>After a pause without another of the same signal: a burst becomes one call.</summary>
        Debounce,

        /// <summary>A fixed time after the first one: what arrives in that window becomes one call.</summary>
        Batch,

        /// <summary>Held while the application is not in view, delivered when it is.</summary>
        WhenVisible,

        /// <summary>Held until the application asks for it.</summary>
        Manual,
    }

    /// <summary>
    /// How a client delivers a signal to its handlers: the kind, and the time a <see cref="SignalDeliveryKind.Debounce"/>
    /// or <see cref="SignalDeliveryKind.Batch"/> waits. What is held merges with what arrives for the same signal and
    /// topic (<see cref="ISignalMergeable{T}"/>, else the latest wins).
    /// </summary>
    public sealed record SignalDelivery(SignalDeliveryKind Kind, TimeSpan Time = default)
    {
        /// <summary>At once.</summary>
        public static SignalDelivery Immediate { get; } = new(SignalDeliveryKind.Immediate);

        /// <summary>Held while the application is not in view.</summary>
        public static SignalDelivery WhenVisible { get; } = new(SignalDeliveryKind.WhenVisible);

        /// <summary>Held until the application asks for it.</summary>
        public static SignalDelivery Manual { get; } = new(SignalDeliveryKind.Manual);

        /// <summary>After <paramref name="pause"/> without another of the same signal and topic.</summary>
        public static SignalDelivery Debounce(TimeSpan pause) => new(SignalDeliveryKind.Debounce, Positive(pause));

        /// <summary><paramref name="window"/> after the first of the same signal and topic.</summary>
        public static SignalDelivery Batch(TimeSpan window) => new(SignalDeliveryKind.Batch, Positive(window));

        static TimeSpan Positive(TimeSpan time)
        {
            if (time <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(time), time, "A delivery's time must be more than zero.");
            return time;
        }

        /// <inheritdoc/>
        public override string ToString() => Kind is SignalDeliveryKind.Debounce or SignalDeliveryKind.Batch ? $"{Kind} {Time}" : Kind.ToString();
    }
}
