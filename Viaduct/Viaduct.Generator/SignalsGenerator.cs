using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Subro.Generators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using static Subro.Generators.GeneratorFunctions;

namespace Viaduct.Generation
{
    /// <summary>
    /// For an interface marked <c>[Signals]</c>, next to it: the contract (metadata, decoding, calling a handler, merging),
    /// the implementation that publishes, and typed subscriptions. See <c>SignalsDesign.md</c>.
    /// </summary>
    /// <remarks>
    /// Generated in the project that declares the interface, so server and client use the same generated code and cannot
    /// drift. The code uses Viaduct.Core and Subro.Json, which that project already references.
    /// </remarks>
    [Generator]
    public sealed class SignalsGenerator : IIncrementalGenerator
    {
        const string AttributeName = "Viaduct.Signals.SignalsAttribute";
        const string TopicAttribute = "Viaduct.Signals.SignalTopicAttribute";
        const string EphemeralAttribute = "Viaduct.Signals.EphemeralAttribute";
        const string NameAttribute = "Viaduct.Signals.SignalNameAttribute";
        const string DeliveryAttribute = "Viaduct.Signals.SignalDeliveryAttribute";
        const string MergeableInterface = "Viaduct.Signals.ISignalMergeable`1";

        const string Ns = "global::Viaduct.Signals.";
        const string Tasks = "global::System.Threading.Tasks.";
        const string Token = "global::System.Threading.CancellationToken";

        static readonly Regex PrefixPattern = new Regex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*$", RegexOptions.CultureInvariant);
        static readonly Regex HolePattern = new Regex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);
        static readonly Regex HoleName = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

        static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
            .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        /// <summary>The types a topic hole can be, by the name of the <c>SignalTopicValues.Parse…</c> method for it.</summary>
        static readonly Dictionary<SpecialType, string> HoleKinds = new()
        {
            [SpecialType.System_String] = "String",
            [SpecialType.System_Int32] = "Int32",
            [SpecialType.System_Int64] = "Int64",
            [SpecialType.System_Int16] = "Int16",
            [SpecialType.System_Boolean] = "Boolean",
        };

        internal enum ParameterRole { Hole, Payload, Token }

        /// <summary>A method parameter: what it is for, and for a hole, its place in the template and how it is read.</summary>
        internal sealed record SignalParameter(string Name, string Type, ParameterRole Role, int HoleIndex, string? HoleKind);

        internal sealed record SignalMethod(
            string Name, string SignalName, string Key, string Topic, bool Ephemeral, int? DeliveryKind, int DeliveryMilliseconds,
            EquatableArray<SignalParameter> Parameters, string? PayloadType, string? PayloadTypeOf, bool PayloadMergeable)
        {
            public SignalParameter? Payload => Parameters.FirstOrDefault(static p => p.Role == ParameterRole.Payload);
            public bool HasToken => Parameters.Any(static p => p.Role == ParameterRole.Token);
        }

        internal sealed record SignalsInfo(
            string Namespace, string InterfaceName, string InterfaceType, string BaseName, string Accessibility, string Prefix,
            EquatableArray<SignalMethod> Methods);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var contracts = context.ValuesForAttributeWithMetadataName(AttributeName,
                static (node, _) => node is InterfaceDeclarationSyntax, GetInfo);

            context.RegisterSourceOutput(contracts, static (spc, info) =>
            {
                var file = info.Namespace.Length == 0 ? info.InterfaceName : $"{info.Namespace}.{info.InterfaceName}";
                spc.AddSource($"{file}.Signals.g.cs", BuildCode(info));
            });
        }

        static TransformResult<SignalsInfo> GetInfo(GeneratorAttributeSyntaxContext context, CancellationToken token)
        {
            if (context.TargetSymbol is not INamedTypeSymbol contract)
                return default;

            var location = LocationInfo.From(contract);
            if (contract.ContainingType is not null || contract.IsGenericType || contract.Interfaces.Length > 0)
                return ViaductErrors.SignalsNotAPlainInterface.CreateError(location,
                    "'{0}' is marked [Signals] and must be an interface that is not generic, not nested in a type and extends no other interface", contract.Name);

            var prefix = context.Attributes[0].ConstructorArguments.FirstOrDefault().Value as string ?? "";
            if (!PrefixPattern.IsMatch(prefix))
                return ViaductErrors.SignalsInvalidPrefix.CreateError(location,
                    "'{0}' is not a valid signal prefix for '{1}': use letters, digits, '_' and '-', in parts separated by '.'", prefix, contract.Name);

            var result = Transform.Build<SignalsInfo>();
            var failed = false;
            void Error(ViaductErrors code, ISymbol at, string format, params object[] args)
            {
                result.Diagnostics.Add(code.CreateError(LocationInfo.From(at), format, args));
                failed = true;
            }

            var interfaceTopic = StringArgument(contract, TopicAttribute);
            var interfaceDelivery = Delivery(contract);
            var methods = new List<SignalMethod>();
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var member in contract.GetMembers())
            {
                token.ThrowIfCancellationRequested();

                if (member.IsStatic || member is IMethodSymbol { MethodKind: not MethodKind.Ordinary })
                    continue;

                if (member is not IMethodSymbol method)
                {
                    Error(ViaductErrors.SignalsMemberNotAMethod, member, "'{0}' on signal contract '{1}' is not a method: a contract holds methods only", member.Name, contract.Name);
                    continue;
                }

                var signal = GetMethod(method, contract, prefix, interfaceTopic, interfaceDelivery, Error);
                if (signal is null)
                    continue;

                if (!keys.Add(signal.Key))
                {
                    Error(ViaductErrors.SignalDuplicateKey, method, "Two signals of '{0}' have the key '{1}': rename one or give it [SignalName]", contract.Name, signal.Key);
                    continue;
                }

                if (!signal.HasToken)
                    result.Diagnostics.Add(ViaductErrors.SignalWithoutCancellationToken.CreateWarning(LocationInfo.From(method),
                        "Signal '{0}' has no CancellationToken: end it with 'CancellationToken cancellationToken = default'", method.Name));

                methods.Add(signal);
            }

            if (failed)
                return result;

            var ns = contract.ContainingNamespace;
            var name = contract.Name;
            var baseName = name.Length > 2 && name[0] == 'I' && char.IsUpper(name[1]) ? name.Substring(1) : name;

            result.Result = new SignalsInfo(
                ns.IsGlobalNamespace ? "" : ns.ToDisplayString(),
                name,
                contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                baseName,
                contract.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
                prefix,
                methods.ToEquatable());
            return result;
        }

        static SignalMethod? GetMethod(IMethodSymbol method, INamedTypeSymbol contract, string prefix, string? interfaceTopic,
            (int Kind, int Milliseconds)? interfaceDelivery, Action<ViaductErrors, ISymbol, string, object[]> error)
        {
            if (method.IsGenericMethod || !method.IsAbstract || method.Parameters.Any(static p => p.RefKind != RefKind.None || p.IsParams))
            {
                error(ViaductErrors.SignalMethodNotSupported, method, "Signal '{0}' cannot be generic, have a body, or take ref, out, in or params parameters", [method.Name]);
                return null;
            }

            if (method.ReturnType.ToDisplayString() != "System.Threading.Tasks.ValueTask")
            {
                error(ViaductErrors.SignalMethodNotValueTask, method, "Signal '{0}' must return ValueTask", [method.Name]);
                return null;
            }

            var signalName = StringArgument(method, NameAttribute) ?? (method.Name.EndsWith("Async", StringComparison.Ordinal) && method.Name.Length > 5
                ? method.Name.Substring(0, method.Name.Length - 5)
                : method.Name);
            var topic = StringArgument(method, TopicAttribute) ?? interfaceTopic ?? prefix;

            // The holes, in the order they appear: a name, read up to the next literal part.
            var holes = new List<string>();
            foreach (Match match in HolePattern.Matches(topic))
            {
                var hole = match.Groups[1].Value;
                if (!HoleName.IsMatch(hole) || holes.Contains(hole))
                {
                    error(ViaductErrors.SignalTopicInvalid, method, "The topic '{0}' of signal '{1}' has a hole '{{{2}}}' that is empty, not a parameter name, or appears twice", [topic, method.Name, hole]);
                    return null;
                }
                holes.Add(hole);
            }
            if (HolePattern.Replace(topic, "").IndexOfAny(['{', '}']) >= 0 || topic.Contains("}{"))
            {
                error(ViaductErrors.SignalTopicInvalid, method, "The topic '{0}' of signal '{1}' has an unclosed brace, or two holes next to each other that cannot be told apart", [topic, method.Name]);
                return null;
            }

            var parameters = new List<SignalParameter>();
            string? payloadType = null, payloadTypeOf = null;
            var payloadMergeable = false;
            var failed = false;

            for (var i = 0; i < method.Parameters.Length; i++)
            {
                var parameter = method.Parameters[i];
                var type = parameter.Type;
                var typeName = type.ToDisplayString(TypeFormat);

                if (type.ToDisplayString() == "System.Threading.CancellationToken")
                {
                    if (i != method.Parameters.Length - 1)
                    {
                        error(ViaductErrors.SignalCancellationTokenNotLast, parameter, "Signal '{0}' has a CancellationToken that is not its last parameter", [method.Name]);
                        failed = true;
                    }
                    parameters.Add(new SignalParameter(parameter.Name, typeName, ParameterRole.Token, -1, null));
                    continue;
                }

                var holeIndex = holes.IndexOf(parameter.Name);
                var holeKind = HoleKindOf(type);

                if (holeIndex >= 0)
                {
                    if (holeKind is null)
                    {
                        error(ViaductErrors.SignalHoleTypeNotSupported, parameter,
                            "Parameter '{0}' of signal '{1}' fills a topic hole and must be a string, Guid, int, long, short or bool, not '{2}'", [parameter.Name, method.Name, type.ToDisplayString()]);
                        failed = true;
                        continue;
                    }
                    parameters.Add(new SignalParameter(parameter.Name, typeName, ParameterRole.Hole, holeIndex, holeKind));
                    continue;
                }

                if (holeKind is not null)
                {
                    error(ViaductErrors.SignalParameterNotInTopic, parameter,
                        "Parameter '{0}' of signal '{1}' is not in its topic '{2}': add '{{{0}}}' to the topic, or put it in the payload", [parameter.Name, method.Name, topic]);
                    failed = true;
                    continue;
                }

                if (payloadType is not null)
                {
                    error(ViaductErrors.SignalMoreThanOnePayload, parameter, "Signal '{0}' has more than one payload: put '{1}' in the same type as the other", [method.Name, parameter.Name]);
                    failed = true;
                    continue;
                }

                payloadType = typeName;
                payloadTypeOf = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                payloadMergeable = type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Viaduct.Signals.ISignalMergeable<T>"
                    && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.NotAnnotated), type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)));
                parameters.Add(new SignalParameter(parameter.Name, typeName, ParameterRole.Payload, -1, null));
            }

            foreach (var hole in holes)
                if (!method.Parameters.Any(p => p.Name == hole))
                {
                    error(ViaductErrors.SignalTopicHoleNotFilled, method, "The topic '{0}' of signal '{1}' has a hole '{{{2}}}' that no parameter fills", [topic, method.Name, hole]);
                    failed = true;
                }

            var delivery = Delivery(method) ?? interfaceDelivery;
            if (delivery is { Kind: 1 or 2, Milliseconds: <= 0 })
            {
                error(ViaductErrors.SignalDeliveryNeedsTime, method, "Signal '{0}' is delivered with a debounce or batch: give it Milliseconds", [method.Name]);
                failed = true;
            }

            if (failed)
                return null;

            return new SignalMethod(method.Name, signalName, prefix + "." + signalName, topic, HasAttribute(method, EphemeralAttribute),
                delivery?.Kind, delivery?.Milliseconds ?? 0, parameters.ToEquatable(), payloadType, payloadTypeOf, payloadMergeable);
        }

        static string? HoleKindOf(ITypeSymbol type)
        {
            if (HoleKinds.TryGetValue(type.SpecialType, out var kind))
                return kind;
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Guid" ? "Guid" : null;
        }

        static AttributeData? FindAttribute(ISymbol symbol, string name)
            => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == name);

        static bool HasAttribute(ISymbol symbol, string name) => FindAttribute(symbol, name) is not null;

        static string? StringArgument(ISymbol symbol, string attribute)
            => FindAttribute(symbol, attribute)?.ConstructorArguments.FirstOrDefault().Value as string;

        static (int Kind, int Milliseconds)? Delivery(ISymbol symbol)
        {
            var attribute = FindAttribute(symbol, DeliveryAttribute);
            if (attribute is null || attribute.ConstructorArguments.FirstOrDefault().Value is not int kind)
                return null;
            var milliseconds = attribute.NamedArguments.FirstOrDefault(static a => a.Key == "Milliseconds").Value.Value as int? ?? 0;
            return (kind, milliseconds);
        }

        // ─── Code ───────────────────────────────────────────────────────────

        internal static string BuildCode(SignalsInfo info)
        {
            var contract = info.BaseName + "Contract";
            var subscriptions = info.BaseName + "Subscriptions";
            var reserved = new HashSet<string>(StringComparer.Ordinal)
            {
                "Instance", "Types", "Prefix", "ContractType", "Find", "Decode", "Merge", "InvokeAsync", "CreatePublisher",
                "Register", "Equals", "GetHashCode", "ToString", "GetType", "Publisher", "Own", "Mismatch", contract,
            };

            int At(SignalMethod method)
            {
                for (var i = 0; i < info.Methods.Length; i++)
                    if (ReferenceEquals(info.Methods[i], method))
                        return i;
                return -1;
            }

            var sb = new StringBuilder(@"// <auto-generated/>
#nullable enable
#pragma warning disable CA2255 // the contract registers itself when its assembly is first used
");
            if (info.Namespace.Length > 0)
                sb.Append("\nnamespace ").Append(info.Namespace).Append(";\n");

            // The contract.
            sb.Append(@"
/// <summary>The signal contract of <see cref=""").Append(info.InterfaceName).Append(@"""/>. Generated by Viaduct from [Signals].</summary>
[global::System.CodeDom.Compiler.GeneratedCode(""Viaduct"", null)]
").Append(info.Accessibility).Append(" sealed class ").Append(contract).Append(" : ").Append(Ns).Append("SignalContract<").Append(info.InterfaceType).Append(@">
{
    /// <summary>The one instance, which registers itself in <see cref=""").Append(Ns).Append(@"SignalContracts""/>.</summary>
    public static ").Append(contract).Append(" Instance { get; } = new ").Append(contract).Append(@"();

    readonly ").Append(Ns).Append(@"SignalTypeInfo[] types;

    ").Append(contract).Append(@"()
    {
        types = new ").Append(Ns).Append("SignalTypeInfo[]\n        {");

            for (var i = 0; i < info.Methods.Length; i++)
            {
                var m = info.Methods[i];
                sb.Append("\n            new ").Append(Ns).Append("SignalTypeInfo(this, ").Append(i).Append(", ")
                    .Append(Literal(m.Key)).Append(", ").Append(Literal(m.Name)).Append(", ")
                    .Append(Ns).Append("SignalTopicTemplate.Parse(").Append(Literal(m.Topic)).Append("), ")
                    .Append(m.Ephemeral ? "true" : "false").Append(", ")
                    .Append(m.DeliveryKind is int kind
                        ? $"new {Ns}SignalDelivery(({Ns}SignalDeliveryKind){kind}, global::System.TimeSpan.FromMilliseconds({m.DeliveryMilliseconds}))"
                        : "null").Append(", ")
                    .Append(m.PayloadTypeOf is null ? "null" : $"typeof({m.PayloadTypeOf})").Append("),");
            }

            sb.Append(@"
        };
    }

    [global::System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => ").Append(Ns).Append(@"SignalContracts.Register(Instance);

    /// <inheritdoc/>
    public override string Prefix => ").Append(Literal(info.Prefix)).Append(@";

    /// <inheritdoc/>
    public override global::System.Collections.Generic.IReadOnlyList<").Append(Ns).Append(@"SignalTypeInfo> Types => types;
");
            foreach (var m in info.Methods.Where(m => !reserved.Contains(m.SignalName)))
                sb.Append(@"
    /// <summary>The signal of <see cref=""").Append(info.InterfaceName).Append('.').Append(m.Name).Append(@"""/>.</summary>
    public ").Append(Ns).Append("SignalTypeInfo ").Append(m.SignalName).Append(" => types[").Append(At(m)).Append("];\n");

            sb.Append(@"
    /// <inheritdoc/>
    public override ").Append(info.InterfaceType).Append(" CreatePublisher(").Append(Ns).Append(@"ISignalPublisher publisher)
    {
        global::System.ArgumentNullException.ThrowIfNull(publisher);
        return new Publisher(this, publisher);
    }

    /// <inheritdoc/>
    public override object? Decode(").Append(Ns).Append(@"SignalTypeInfo type, string? data)
    {
        switch (Own(type).Index)
        {");
            foreach (var m in info.Methods.Where(static m => m.PayloadType is not null))
                sb.Append(@"
            case ").Append(At(m)).Append(": return data is null ? null : global::Subro.Json.Parse<").Append(m.PayloadType).Append(">(data);");
            sb.Append(@"
            default: return null;
        }
    }
");

            if (info.Methods.Any(static m => m.PayloadMergeable))
            {
                sb.Append(@"
    /// <inheritdoc/>
    public override object? Merge(").Append(Ns).Append(@"SignalTypeInfo type, object? held, object? newer)
    {
        switch (Own(type).Index)
        {");
                foreach (var m in info.Methods.Where(static m => m.PayloadMergeable))
                    sb.Append(@"
            case ").Append(At(m)).Append(": return held is ").Append(m.PayloadTypeOf).Append(" h && newer is ").Append(m.PayloadTypeOf)
                        .Append(" n ? h.Merge(n) : newer;");
                sb.Append(@"
            default: return newer;
        }
    }
");
            }

            sb.Append(@"
    /// <inheritdoc/>
    public override ").Append(Tasks).Append("ValueTask InvokeAsync(object handler, ").Append(Ns).Append("SignalTypeInfo type, string topic, object? payload, ")
                .Append(Token).Append(@" cancellationToken = default)
    {
        global::System.ArgumentNullException.ThrowIfNull(handler);
        var target = (").Append(info.InterfaceType).Append(@")handler;
        switch (Own(type).Index)
        {");
            foreach (var m in info.Methods)
            {
                sb.Append(@"
            case ").Append(At(m)).Append(@":
            {
                if (!type.Topic.TryMatch(topic, out var values))
                    throw Mismatch(type, topic);
                return target.").Append(Identifier(m.Name)).Append('(')
                    .Append(string.Join(", ", m.Parameters.Select(static p => p.Role switch
                    {
                        ParameterRole.Hole => $"{Ns}SignalTopicValues.Parse{p.HoleKind}(values[{p.HoleIndex}])",
                        ParameterRole.Payload => $"({p.Type})payload!",
                        _ => "cancellationToken",
                    }))).Append(@");
            }");
            }
            sb.Append(@"
            default:
                throw new global::System.ArgumentException($""'{type.Key}' is not a signal of ").Append(info.InterfaceName).Append(@"."", nameof(type));
        }
    }

    ").Append(Ns).Append("SignalTypeInfo Own(").Append(Ns).Append(@"SignalTypeInfo type)
    {
        global::System.ArgumentNullException.ThrowIfNull(type);
        if (!ReferenceEquals(type.Contract, this))
            throw new global::System.ArgumentException($""'{type.Key}' is not a signal of ").Append(info.InterfaceName).Append(@"."", nameof(type));
        return type;
    }

    static global::System.FormatException Mismatch(").Append(Ns).Append(@"SignalTypeInfo type, string topic)
        => new($""'{topic}' is not a topic of signal '{type.Key}', whose template is '{type.Topic}'."");

    sealed class Publisher(").Append(contract).Append(" contract, ").Append(Ns).Append("ISignalPublisher publisher) : ").Append(info.InterfaceType).Append(@"
    {");
            foreach (var m in info.Methods)
            {
                var index = At(m);
                var declared = string.Join(", ", m.Parameters.Select(static p => p.Type + " " + Identifier(p.Name)));
                var holes = m.Parameters.Where(static p => p.Role == ParameterRole.Hole).OrderBy(static p => p.HoleIndex)
                    .Select(static p => $"{Ns}SignalTopicValues.Format({Identifier(p.Name)})");
                var payload = m.Payload is { } pl ? $"global::Subro.Json.Stringify<{pl.Type}>({Identifier(pl.Name)})" : "null";
                var tokenName = m.Parameters.FirstOrDefault(static p => p.Role == ParameterRole.Token) is { } t ? Identifier(t.Name) : "default";

                sb.Append(@"
        ").Append(Tasks).Append("ValueTask ").Append(info.InterfaceType).Append('.').Append(Identifier(m.Name)).Append('(').Append(declared).Append(@")
        {
            var type = contract.types[").Append(index).Append(@"];
            return publisher.PublishAsync(new ").Append(Ns).Append("SignalEnvelope(type.Key, type.Topic.Fill(").Append(string.Join(", ", holes)).Append("), ").Append(payload)
                    .Append(") { Ephemeral = type.Ephemeral }, ").Append(tokenName).Append(@");
        }
");
            }
            sb.Append(@"    }
}
");

            // Typed subscriptions.
            sb.Append(@"
/// <summary>Typed subscriptions to the signals of <see cref=""").Append(info.InterfaceName).Append(@"""/>, each until disposed. Generated by Viaduct from [Signals].</summary>
[global::System.CodeDom.Compiler.GeneratedCode(""Viaduct"", null)]
").Append(info.Accessibility).Append(" readonly struct ").Append(subscriptions).Append(@"
{
    readonly ").Append(Ns).Append(@"ISignalSubscriptions subscriptions;

    /// <summary>Subscriptions made through <paramref name=""subscriptions""/>.</summary>
    public ").Append(subscriptions).Append('(').Append(Ns).Append(@"ISignalSubscriptions subscriptions)
    {
        global::System.ArgumentNullException.ThrowIfNull(subscriptions);
        this.subscriptions = subscriptions;
    }
");
            foreach (var m in info.Methods)
            {
                var index = At(m);
                var holes = m.Parameters.Where(static p => p.Role == ParameterRole.Hole).OrderBy(static p => p.HoleIndex).ToList();
                var payload = m.Payload;
                var handlerType = payload is null
                    ? $"global::System.Func<{Token}, {Tasks}ValueTask>"
                    : $"global::System.Func<{payload.Type}, {Token}, {Tasks}ValueTask>";
                var call = payload is null ? "(_, token) => onSignal(token)" : $"(payload, token) => onSignal(({payload.Type})payload!, token)";
                var declared = string.Join("", holes.Select(static p => p.Type + " " + Identifier(p.Name) + ", "));

                sb.Append(@"
    /// <summary>Calls <paramref name=""onSignal""/> whenever <see cref=""").Append(info.InterfaceName).Append('.').Append(m.Name).Append(@"""/> arrives")
                    .Append(holes.Count > 0 ? " on this topic" : "").Append(@", until the result is disposed.</summary>
    public global::System.IDisposable On").Append(m.SignalName).Append('(').Append(declared).Append(handlerType).Append(" onSignal, global::System.Func<")
                    .Append(Token).Append(", ").Append(Tasks).Append(@"ValueTask>? onReset = null)
    {
        global::System.ArgumentNullException.ThrowIfNull(onSignal);
        var type = ").Append(contract).Append(".Instance.Types[").Append(index).Append(@"];
        return subscriptions.Subscribe(type, type.Topic.Fill(").Append(string.Join(", ", holes.Select(static p => $"{Ns}SignalTopicValues.Format({Identifier(p.Name)})")))
                    .Append("), ").Append(call).Append(@", onReset);
    }
");
            }
            sb.Append(@"}

/// <summary>The typed subscriptions of <see cref=""").Append(info.InterfaceName).Append(@"""/>. Generated by Viaduct from [Signals].</summary>
[global::System.CodeDom.Compiler.GeneratedCode(""Viaduct"", null)]
").Append(info.Accessibility).Append(" static class ").Append(info.BaseName).Append(@"SignalExtensions
{
    /// <summary>Subscriptions to the signals of <see cref=""").Append(info.InterfaceName).Append(@"""/>.</summary>
    public static ").Append(subscriptions).Append(' ').Append(info.BaseName).Append("(this ").Append(Ns).Append("ISignalSubscriptions subscriptions) => new ")
                .Append(subscriptions).Append(@"(subscriptions);
}
");
            return sb.ToString();
        }

        static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
    }
}
