using System;
using System.Collections.Generic;

namespace Viaduct.Signals
{
    /// <summary>
    /// A client's signal settings. Delivery: an app-wide default, then per contract, then per kind of signal, with a
    /// <see cref="SignalDeliveryAttribute"/> on the contract as the hint in between. Connection: which ways to try, in
    /// order, and how patiently.
    /// </summary>
    public sealed class SignalClientOptions
    {
        readonly Dictionary<Type, SignalDelivery> byContract = [];
        readonly Dictionary<string, SignalDelivery> byType = new(StringComparer.Ordinal);

        /// <summary>For signals nothing else is said about. Default <see cref="SignalDelivery.Immediate"/>.</summary>
        public SignalDelivery DefaultDelivery { get; set; } = SignalDelivery.Immediate;

        /// <summary>The server's signal endpoint, relative to the connection's base address. Default <c>/signals</c>.</summary>
        public string Path { get; set; } = "/signals";

        /// <summary>
        /// The connections to try, by name, in order: when one does not get through, the next. Default server-sent events,
        /// then polling.
        /// </summary>
        public IList<string> Connections { get; set; } = [SignalConnectionNames.ServerSentEvents, SignalConnectionNames.Polling];

        /// <summary>The connections to try while the connection is metered; null for the same as <see cref="Connections"/>.</summary>
        public IList<string>? MeteredConnections { get; set; }

        /// <summary>How long a stream may take to say it is connected before it counts as not getting through. Default 10 seconds.</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>How long a long poll is held by the server; zero for plain polling every <see cref="PollInterval"/>. Default 25 seconds.</summary>
        public TimeSpan PollWait { get; set; } = TimeSpan.FromSeconds(25);

        /// <summary>The pause between plain polls. Default 30 seconds.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>How long a later connection in the order is used before the first is tried again. Default 5 minutes.</summary>
        public TimeSpan RetryPreferredAfter { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How many times a connection may be answered with an error before the next is tried. Default 2. A server out of
        /// reach, or one refusing the caller (401, 403), does not count: another way would fare no better.
        /// </summary>
        public int FallbackAfterFailures { get; set; } = 2;

        /// <summary>The longest wait between attempts after failures, which double from a second. Default 60 seconds.</summary>
        public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>How much a wait between attempts varies, so clients do not all return at once. Default 0.2 (±20%).</summary>
        public double BackoffJitter { get; set; } = 0.2;

        /// <summary>How long page topic changes are gathered before reconnecting with them. Default 200 milliseconds.</summary>
        public TimeSpan TopicsChangeDelay { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>Disconnect while the application is not in view (an app in the background), and catch up when it is. Default false.</summary>
        public bool PauseWhenHidden { get; set; }

        /// <summary>Delivers every signal of the contract <typeparamref name="TContract"/> this way.</summary>
        public SignalClientOptions Deliver<TContract>(SignalDelivery delivery) where TContract : class
        {
            ArgumentNullException.ThrowIfNull(delivery);
            byContract[typeof(TContract)] = delivery;
            return this;
        }

        /// <summary>Delivers <paramref name="type"/> this way, such as <c>ChatSignalsContract.Instance.MessagesArrived</c>.</summary>
        public SignalClientOptions Deliver(SignalTypeInfo type, SignalDelivery delivery)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(delivery);
            byType[type.Key] = delivery;
            return this;
        }

        /// <summary>
        /// How <paramref name="type"/> is delivered: what was said for it, else for its contract, else its contract's
        /// hint, else <see cref="DefaultDelivery"/>.
        /// </summary>
        public SignalDelivery DeliveryFor(SignalTypeInfo type)
        {
            ArgumentNullException.ThrowIfNull(type);
            if (byType.TryGetValue(type.Key, out var delivery) || byContract.TryGetValue(type.Contract.ContractType, out delivery))
                return delivery;
            return type.Delivery ?? DefaultDelivery;
        }
    }

    /// <summary>
    /// What a client knows of its surroundings, for choosing how to deliver: whether it is in view, and whether data costs.
    /// The application feeds it: in a browser from the page's visibility, in an app from its lifecycle.
    /// </summary>
    public interface ISignalClientEnvironment
    {
        /// <summary>The application is in view.</summary>
        bool IsVisible { get; }

        /// <summary>The connection is metered, or the user asked to save data.</summary>
        bool IsMetered { get; }

        /// <summary>Raised when <see cref="IsVisible"/> or <see cref="IsMetered"/> changed.</summary>
        event Action? Changed;
    }

    /// <summary>
    /// An environment the application sets by hand: in view and unmetered until told otherwise. The default.
    /// </summary>
    public sealed class SignalClientEnvironment : ISignalClientEnvironment
    {
        bool visible = true, metered;

        /// <inheritdoc/>
        public bool IsVisible => visible;

        /// <inheritdoc/>
        public bool IsMetered => metered;

        /// <inheritdoc/>
        public event Action? Changed;

        /// <summary>Tells whether the application is in view.</summary>
        public void SetVisible(bool value)
        {
            if (visible == value)
                return;
            visible = value;
            Changed?.Invoke();
        }

        /// <summary>Tells whether data costs.</summary>
        public void SetMetered(bool value)
        {
            if (metered == value)
                return;
            metered = value;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Chooses how a signal is delivered given the surroundings, such as holding everything until visible on a metered
    /// connection. The default keeps what the options say.
    /// </summary>
    public interface ISignalDeliveryPolicy
    {
        /// <summary>The delivery for <paramref name="type"/> on <paramref name="topic"/>; <paramref name="configured"/> is what the options say.</summary>
        SignalDelivery Choose(SignalTypeInfo type, string topic, SignalDelivery configured, ISignalClientEnvironment environment);
    }

    /// <summary>Keeps what the options say.</summary>
    public sealed class ConfiguredSignalDeliveryPolicy : ISignalDeliveryPolicy
    {
        /// <inheritdoc/>
        public SignalDelivery Choose(SignalTypeInfo type, string topic, SignalDelivery configured, ISignalClientEnvironment environment) => configured;
    }
}
