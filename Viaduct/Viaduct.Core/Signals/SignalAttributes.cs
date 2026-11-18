using System;

namespace Viaduct.Signals
{
    /// <summary>
    /// Marks an interface as a signal contract: what a server can tell its clients changed. The generator writes, next to
    /// the interface, an implementation that publishes (for the server), the code that calls a handler implementing the
    /// interface (for the client), typed subscriptions for a page, and the contract's metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each method is one kind of signal. It returns <see cref="System.Threading.Tasks.ValueTask"/> and ends with a
    /// <see cref="System.Threading.CancellationToken"/>. Its simple parameters fill the holes of its topic
    /// (<see cref="SignalTopicAttribute"/>); at most one other parameter is the payload, written as JSON.
    /// </para>
    /// <para>
    /// The <paramref name="prefix"/> starts every type key of the contract (<c>chat.MessagesArrived</c>). The keys are
    /// stored and sent to clients that may be older than the server, so name the prefix explicitly and do not rename it.
    /// </para>
    /// </remarks>
    /// <param name="prefix">Starts every type key of the contract: letters, digits, <c>_</c>, <c>-</c> and <c>.</c>.</param>
    [AttributeUsage(AttributeTargets.Interface, Inherited = false)]
    public sealed class SignalsAttribute(string prefix) : Attribute
    {
        /// <summary>Starts every type key of the contract.</summary>
        public string Prefix { get; } = prefix;
    }

    /// <summary>
    /// The topic a signal is sent to, such as <c>chat.room/{roomId}</c>: who hears it. Each <c>{hole}</c> is filled by the
    /// method's parameter of that name. On the interface, the topic of every method without one of its own.
    /// </summary>
    /// <remarks>
    /// Without a topic on the method or the interface, a signal goes to the contract's prefix as a topic.
    /// A hole's parameter is a <see cref="string"/>, <see cref="Guid"/>, <see cref="int"/>, <see cref="long"/>,
    /// <see cref="short"/> or <see cref="bool"/>.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, Inherited = false)]
    public sealed class SignalTopicAttribute(string template) : Attribute
    {
        /// <summary>The topic, with a <c>{hole}</c> per parameter that fills it.</summary>
        public string Template { get; } = template;
    }

    /// <summary>
    /// A signal that is only worth something live, such as someone typing: not stored, never sent again after a gap.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class EphemeralAttribute : Attribute
    {
    }

    /// <summary>
    /// The name in the signal's type key, instead of the method name without <c>Async</c>. For renaming a method without
    /// changing the stored key.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class SignalNameAttribute(string name) : Attribute
    {
        /// <summary>The name after the contract's prefix.</summary>
        public string Name { get; } = name;
    }

    /// <summary>
    /// How a client delivers this signal by default, unless the application says otherwise. On the interface, for every
    /// method without one of its own.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, Inherited = false)]
    public sealed class SignalDeliveryAttribute(SignalDeliveryKind kind) : Attribute
    {
        /// <summary>The way of delivering.</summary>
        public SignalDeliveryKind Kind { get; } = kind;

        /// <summary>The pause for <see cref="SignalDeliveryKind.Debounce"/>, the window for <see cref="SignalDeliveryKind.Batch"/>.</summary>
        public int Milliseconds { get; set; }
    }
}
