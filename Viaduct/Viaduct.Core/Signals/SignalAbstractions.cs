using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// Where a signal goes when it is published: the engine's entry. The generated implementation of a contract calls it;
    /// application code calls the contract instead.
    /// </summary>
    public interface ISignalPublisher
    {
        /// <summary>
        /// Publishes <paramref name="signal"/>: stores it unless it is ephemeral and tells every server, which tell their
        /// clients listening to its topic. Call it after the change it reports is saved.
        /// </summary>
        ValueTask PublishAsync(SignalEnvelope signal, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// A client's subscriptions for a page or a component: one signal on one topic, until disposed. The generated
    /// subscriptions of a contract (<c>signals.ChatSignals().OnMessagesArrived(roomId, …)</c>) are written on it.
    /// </summary>
    public interface ISignalSubscriptions
    {
        /// <summary>
        /// Calls <paramref name="onSignal"/> with the decoded payload whenever <paramref name="type"/> arrives on
        /// <paramref name="topic"/>, delivered as the client delivers that signal, until the result is disposed.
        /// </summary>
        /// <param name="type">The kind of signal.</param>
        /// <param name="topic">The filled topic.</param>
        /// <param name="onSignal">Gets the payload, null for a signal without one.</param>
        /// <param name="onReset">Called when the client lost track of what happened and what is shown should be loaded again.</param>
        IDisposable Subscribe(SignalTypeInfo type, string topic, Func<object?, CancellationToken, ValueTask> onSignal,
            Func<CancellationToken, ValueTask>? onReset = null);
    }

    /// <summary>
    /// A payload that knows how to merge with a newer one of the same signal and topic, when a client holds signals
    /// before delivering them. Without it, the newer payload wins.
    /// </summary>
    /// <typeparam name="T">The payload type itself.</typeparam>
    public interface ISignalMergeable<T>
    {
        /// <summary>This payload and <paramref name="newer"/> as one: a total count, the latest sender.</summary>
        T Merge(T newer);
    }

    /// <summary>
    /// A client's handler that wants to know when the client lost track of what happened (it was away longer than the
    /// server keeps signals), so it can load what it shows again.
    /// </summary>
    public interface ISignalResettable
    {
        /// <summary>Load again what was shown from signals.</summary>
        ValueTask ResetAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// The signal a client's handler is being called for, beyond its payload: its id, time and topic, and how many were
    /// merged into the call. Read <see cref="Current"/> inside the handler.
    /// </summary>
    /// <param name="Type">The kind of signal.</param>
    /// <param name="Topic">The filled topic.</param>
    /// <param name="Id">The latest one's id; 0 for an ephemeral signal.</param>
    /// <param name="At">When the latest one was published.</param>
    /// <param name="FirstAt">When the first one merged into this call was published.</param>
    /// <param name="Count">How many arrived and were merged into this call.</param>
    public sealed record SignalInfo(SignalTypeInfo Type, string Topic, long Id, DateTimeOffset At, DateTimeOffset FirstAt, int Count)
    {
        static readonly AsyncLocal<SignalInfo?> current = new();

        /// <summary>The signal being delivered; null outside a handler.</summary>
        public static SignalInfo? Current => current.Value;

        /// <summary>Set by the client around a handler's call.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void SetCurrent(SignalInfo? info) => current.Value = info;
    }
}
