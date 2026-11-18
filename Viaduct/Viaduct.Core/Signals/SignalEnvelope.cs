using System;

namespace Viaduct.Signals
{
    /// <summary>
    /// A signal as the engine moves it: stored, sent between servers and to clients, without looking inside.
    /// </summary>
    /// <remarks>
    /// What the signal means is its contract's business (<see cref="SignalsAttribute"/>). The engine only reads the
    /// <see cref="Topic"/> to know who hears it and the <see cref="Id"/> to know who has had it.
    /// </remarks>
    /// <param name="Type">The type key, such as <c>chat.MessagesArrived</c>.</param>
    /// <param name="Topic">The filled topic, such as <c>chat.room/0193…</c>.</param>
    /// <param name="Data">The payload as JSON; null without one.</param>
    public sealed record SignalEnvelope(string Type, string Topic, string? Data = null)
    {
        /// <summary>
        /// The cursor: increasing across the store, given when stored. 0 before that, and for an
        /// <see cref="Ephemeral"/> signal, which is never stored.
        /// </summary>
        public long Id { get; init; }

        /// <summary>When it was published.</summary>
        public DateTimeOffset At { get; init; }

        /// <summary>Only sent live: not stored, never sent again after a gap.</summary>
        public bool Ephemeral { get; init; }

        /// <summary>The type key of <see cref="Reset"/>.</summary>
        public const string ResetType = "viaduct.reset";

        /// <summary>
        /// Tells a client it lost track: what came before <paramref name="cursor"/> is no longer stored, so what it shows
        /// should be loaded again, and it continues from <paramref name="cursor"/>.
        /// </summary>
        public static SignalEnvelope Reset(long cursor) => new(ResetType, "") { Id = cursor };

        /// <summary>True for a <see cref="Reset"/>.</summary>
        public bool IsReset => Type == ResetType;

        /// <inheritdoc/>
        public override string ToString() => Id == 0 ? $"{Type} → {Topic}" : $"#{Id} {Type} → {Topic}";
    }
}
