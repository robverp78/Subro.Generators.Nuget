using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// One kind of signal of a contract: one method of the interface marked <see cref="SignalsAttribute"/>.
    /// </summary>
    /// <param name="contract">The contract it belongs to.</param>
    /// <param name="index">Its place among the contract's <see cref="SignalContract.Types"/>.</param>
    /// <param name="key">The stored type key, such as <c>chat.MessagesArrived</c>.</param>
    /// <param name="method">The interface method.</param>
    /// <param name="topic">Who hears it.</param>
    /// <param name="ephemeral">Only sent live.</param>
    /// <param name="delivery">How a client delivers it unless the application says otherwise; null for the client's default.</param>
    /// <param name="payloadType">The type of its payload; null without one.</param>
    public sealed class SignalTypeInfo(SignalContract contract, int index, string key, string method,
        SignalTopicTemplate topic, bool ephemeral, SignalDelivery? delivery, Type? payloadType)
    {
        /// <summary>The contract it belongs to.</summary>
        public SignalContract Contract { get; } = contract;

        /// <summary>Its place among the contract's <see cref="SignalContract.Types"/>.</summary>
        public int Index { get; } = index;

        /// <summary>The stored type key, such as <c>chat.MessagesArrived</c>.</summary>
        public string Key { get; } = key;

        /// <summary>The interface method.</summary>
        public string Method { get; } = method;

        /// <summary>Who hears it.</summary>
        public SignalTopicTemplate Topic { get; } = topic;

        /// <summary>Only sent live: not stored, never sent again.</summary>
        public bool Ephemeral { get; } = ephemeral;

        /// <summary>How a client delivers it unless the application says otherwise; null for the client's default.</summary>
        public SignalDelivery? Delivery { get; } = delivery;

        /// <summary>The type of its payload; null without one.</summary>
        public Type? PayloadType { get; } = payloadType;

        /// <inheritdoc/>
        public override string ToString() => Key;
    }

    /// <summary>
    /// What the generator knows of a signal contract, for the engine: its kinds of signal, and how to read a payload and
    /// call a handler. One instance per contract, written by the generator; find it with <see cref="SignalContracts"/>.
    /// </summary>
    public abstract class SignalContract
    {
        Dictionary<string, SignalTypeInfo>? byKey;

        /// <summary>The interface marked <see cref="SignalsAttribute"/>.</summary>
        public abstract Type ContractType { get; }

        /// <summary>Starts every type key of the contract.</summary>
        public abstract string Prefix { get; }

        /// <summary>Its kinds of signal, one per method.</summary>
        public abstract IReadOnlyList<SignalTypeInfo> Types { get; }

        /// <summary>The kind of signal with this type key; null when the contract has none.</summary>
        public SignalTypeInfo? Find(string key)
        {
            var map = byKey;
            if (map is null)
            {
                map = new Dictionary<string, SignalTypeInfo>(StringComparer.Ordinal);
                foreach (var type in Types)
                    map[type.Key] = type;
                byKey = map;
            }
            return map.TryGetValue(key, out var found) ? found : null;
        }

        /// <summary>The payload read from its JSON; null for a signal without one.</summary>
        public abstract object? Decode(SignalTypeInfo type, string? data);

        /// <summary>
        /// Two payloads of the same signal and topic as one, for a client holding them: the held one's
        /// <see cref="ISignalMergeable{T}.Merge"/> when its type has one, else the newer.
        /// </summary>
        public virtual object? Merge(SignalTypeInfo type, object? held, object? newer) => newer;

        /// <summary>
        /// Calls the method of <paramref name="handler"/>, an implementation of <see cref="ContractType"/>, with the values
        /// read back from <paramref name="topic"/> and the decoded <paramref name="payload"/>.
        /// </summary>
        /// <exception cref="FormatException">The topic is not the signal's template filled in.</exception>
        public abstract ValueTask InvokeAsync(object handler, SignalTypeInfo type, string topic, object? payload, CancellationToken cancellationToken = default);

        /// <inheritdoc/>
        public override string ToString() => ContractType.Name;
    }

    /// <summary>A signal contract for the interface <typeparamref name="T"/>.</summary>
    public abstract class SignalContract<T> : SignalContract where T : class
    {
        /// <inheritdoc/>
        public sealed override Type ContractType => typeof(T);

        /// <summary>The implementation of <typeparamref name="T"/> that turns each call into a published signal.</summary>
        public abstract T CreatePublisher(ISignalPublisher publisher);
    }

    /// <summary>
    /// The signal contracts of an application. A generated contract registers itself when its assembly is first used, so
    /// <see cref="Get{T}"/> finds it without registering anything by hand.
    /// </summary>
    public static class SignalContracts
    {
        static readonly ConcurrentDictionary<Type, SignalContract> contracts = new();
        static readonly ConcurrentDictionary<string, SignalTypeInfo> types = new(StringComparer.Ordinal);

        /// <summary>The contracts registered so far.</summary>
        public static IReadOnlyCollection<SignalContract> All => (IReadOnlyCollection<SignalContract>)contracts.Values;

        /// <summary>
        /// Adds a contract. Called by the generated code; registering the same contract again does nothing.
        /// </summary>
        /// <exception cref="InvalidOperationException">Another contract already has one of its type keys.</exception>
        public static void Register(SignalContract contract)
        {
            ArgumentNullException.ThrowIfNull(contract);

            foreach (var type in contract.Types)
            {
                var existing = types.GetOrAdd(type.Key, type);
                if (existing.Contract.ContractType != contract.ContractType)
                    throw new InvalidOperationException(
                        $"The signal type key '{type.Key}' is declared by both {existing.Contract.ContractType} and {contract.ContractType}.");
            }

            contracts.TryAdd(contract.ContractType, contract);
        }

        /// <summary>The contract of <typeparamref name="T"/>.</summary>
        /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not marked <see cref="SignalsAttribute"/>.</exception>
        public static SignalContract<T> Get<T>() where T : class => (SignalContract<T>)Get(typeof(T));

        /// <summary>The contract of <paramref name="contractType"/>.</summary>
        /// <exception cref="InvalidOperationException">The type is not marked <see cref="SignalsAttribute"/>.</exception>
        public static SignalContract Get(Type contractType)
            => Find(contractType) ?? throw new InvalidOperationException(
                $"{contractType} is not a signal contract. Mark it [Signals(\"…\")] in a project that references Viaduct.Core, so the generator writes one.");

        /// <summary>The contract of <paramref name="contractType"/>; null when it is none.</summary>
        public static SignalContract? Find(Type contractType)
        {
            ArgumentNullException.ThrowIfNull(contractType);
            if (contracts.TryGetValue(contractType, out var contract))
                return contract;

            // The generated contract registers itself from a module initializer, which only runs once code in its assembly
            // runs. Run it now, as Subro.Json does for its contexts.
            RuntimeHelpers.RunModuleConstructor(contractType.Module.ModuleHandle);
            return contracts.TryGetValue(contractType, out contract) ? contract : null;
        }

        /// <summary>The kind of signal with this type key, among the registered contracts; null when there is none.</summary>
        public static SignalTypeInfo? FindType(string key) => types.TryGetValue(key, out var type) ? type : null;
    }
}
