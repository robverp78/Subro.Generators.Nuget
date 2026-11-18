using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// Keeps signals for a while, so a client that was away can catch up from its cursor: the in-memory store, a database.
    /// Ephemeral signals never reach it.
    /// </summary>
    public interface ISignalStore
    {
        /// <summary>Stores <paramref name="signal"/> and returns it with its <see cref="SignalEnvelope.Id"/>.</summary>
        ValueTask<SignalEnvelope> AppendAsync(SignalEnvelope signal, CancellationToken cancellationToken = default);

        /// <summary>
        /// The stored signals after <paramref name="after"/>, oldest first, at most <paramref name="max"/>; only those on
        /// <paramref name="topics"/> unless it is null.
        /// </summary>
        /// <remarks>
        /// When signals after <paramref name="after"/> are no longer stored, or the cursor is not one this store gave, the
        /// batch is a reset: no signals, and the cursor to continue from.
        /// </remarks>
        ValueTask<SignalBatch> ReadAsync(long after, IReadOnlySet<string>? topics, int max, CancellationToken cancellationToken = default);

        /// <summary>The cursor of the latest signal that can be read: where a new reader starts.</summary>
        ValueTask<long> GetCursorAsync(CancellationToken cancellationToken = default);

        /// <summary>Removes the signals published before <paramref name="before"/>; returns how many.</summary>
        ValueTask<int> RemoveBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
    }

    /// <summary>What <see cref="ISignalStore.ReadAsync"/> found.</summary>
    /// <param name="Signals">The signals, oldest first.</param>
    /// <param name="Cursor">Where to read on from: past everything looked at, including signals on other topics.</param>
    /// <param name="Reset">The signals after the cursor asked for are gone; the reader lost track.</param>
    /// <param name="More">There may be more after <paramref name="Cursor"/>.</param>
    public sealed record SignalBatch(IReadOnlyList<SignalEnvelope> Signals, long Cursor, bool Reset = false, bool More = false);

    /// <summary>
    /// Carries word of new signals between the servers of an application: in process, Postgres <c>NOTIFY</c>. Each server
    /// subscribes once and passes what arrives to its own listeners.
    /// </summary>
    public interface ISignalBus
    {
        /// <summary>Tells every server that new signals were stored.</summary>
        ValueTask AnnounceAsync(CancellationToken cancellationToken = default);

        /// <summary>Sends an ephemeral signal, which is not stored, to every server.</summary>
        ValueTask SendEphemeralAsync(SignalEnvelope signal, CancellationToken cancellationToken = default);

        /// <summary>Hands what arrives to <paramref name="subscriber"/>, until the result is disposed.</summary>
        IDisposable Subscribe(ISignalBusSubscriber subscriber);
    }

    /// <summary>What a server does with what arrives over the <see cref="ISignalBus"/>.</summary>
    public interface ISignalBusSubscriber
    {
        /// <summary>New signals were stored somewhere: read them from the store.</summary>
        ValueTask OnStoredAsync(CancellationToken cancellationToken = default);

        /// <summary>An ephemeral signal arrived.</summary>
        ValueTask OnEphemeralAsync(SignalEnvelope signal, CancellationToken cancellationToken = default);
    }
}
