using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Subro.RecordForge;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;

namespace Subro.Generators
{
    partial class InterfaceImplementer
    {
        internal static readonly DiagnosticDescriptorInfo NotPartial =
            Diagnostics.Error("AIRIMPPART", "'{0}' must be declared partial (and so must every type it is nested in) to be completed by " + nameof(ImplementsAttribute));

        internal static readonly DiagnosticDescriptorInfo NotAnInterface =
            Diagnostics.Error("AIRIMPIFACE", "'{0}' is not an interface and cannot be implemented by " + nameof(ImplementsAttribute));

        internal static readonly DiagnosticDescriptorInfo UnboundGeneric =
            Diagnostics.Error("AIRIMPOPEN", "'{0}' is an unbound generic type; specify the type arguments to implement it");

        internal static readonly DiagnosticDescriptorInfo TypeMismatch =
            Diagnostics.Warning("AIRIMPMISMATCH", "'{0}' already has a member named '{1}', so it was not generated for '{2}'. The existing member does not satisfy the interface, which will produce a compile error");

        /// <summary>
        /// One transform per annotated type: all <see cref="ImplementsAttribute"/> occurrences on the type are
        /// handled together, so that two interfaces asking for the same member produce one property rather
        /// than a duplicate-member error across two generated files.
        /// </summary>
        static TransformResult<ImplementInfo> TransformType(GeneratorAttributeSyntaxContext context, CancellationToken token = default)
        {
            if (context.TargetSymbol is not INamedTypeSymbol type || context.TargetNode is not TypeDeclarationSyntax node)
                return default; //the attribute only targets class/struct declarations, so this should not happen

            var builder = new TransformResultBuilder<ImplementInfo>();

            if (!IsPartialAllTheWayUp(node))
                return NotPartial.CreateDiagnosticInfo(LocationInfo.From(node), type.Name);

            var properties = ImmutableArray.CreateBuilder<ImplementedPropertyInfo>();
            var interfaces = ImmutableArray.CreateBuilder<string>();
            // Names claimed during this run, so a member requested by two interfaces is only emitted once.
            var claimed = new HashSet<string>();

            foreach (var attributeData in context.Attributes)
            {
                if (token.IsCancellationRequested) return default;

                bool alwaysCreateSetters = false, declareInterface = ImplementsAttribute.DefaultDeclareInterface;
                foreach (var arg in attributeData.NamedArguments)
                    switch (arg.Key)
                    {
                        case nameof(ImplementsAttribute.AlwaysCreateSetters):
                            alwaysCreateSetters = arg.Value.Value as bool? ?? false; break;
                        case nameof(ImplementsAttribute.DeclareInterface):
                            declareInterface = arg.Value.Value as bool? ?? ImplementsAttribute.DefaultDeclareInterface; break;
                    }

                foreach (var interfaceSymbol in GetInterfaceArguments(attributeData))
                {
                    if (token.IsCancellationRequested) return default;
                    var location = LocationInfo.From(attributeData, token);

                    if (interfaceSymbol.TypeKind != TypeKind.Interface)
                    {
                        builder.Diagnostics.Add(NotAnInterface.CreateDiagnosticInfo(location, interfaceSymbol.Name));
                        continue;
                    }

                    if (interfaceSymbol.IsUnboundGenericType)
                    {
                        builder.Diagnostics.Add(UnboundGeneric.CreateDiagnosticInfo(location, interfaceSymbol.Name));
                        continue;
                    }

                    if (declareInterface && !AlreadyImplements(type, interfaceSymbol))
                    {
                        var name = interfaceSymbol.ToDisplayString(TypeFormat);
                        if (!interfaces.Contains(name)) interfaces.Add(name);
                    }

                    CollectProperties(type, interfaceSymbol, alwaysCreateSetters, claimed, properties, builder, token);
                }
            }

            if (properties.Count == 0 && interfaces.Count == 0)
                return builder; //nothing missing: the type already satisfies everything asked of it

            var containingHeaders = ImmutableArray.CreateBuilder<string>();
            for (var containing = type.ContainingType; containing is not null; containing = containing.ContainingType)
                containingHeaders.Insert(0, BuildHeader(containing));

            builder.Result = new ImplementInfo(
                type.GetNamespace(),
                containingHeaders.ToEquatable(),
                BuildHeader(type),
                interfaces.ToEquatable(),
                properties.ToEquatable(),
                BuildHintName(type));

            return builder;
        }

        /// <summary>
        /// Walks the interface and everything it inherits, adding each abstract, public, non-static property
        /// the target does not already have.
        /// </summary>
        static void CollectProperties(
            INamedTypeSymbol type,
            INamedTypeSymbol interfaceSymbol,
            bool alwaysCreateSetters,
            HashSet<string> claimed,
            ImmutableArray<ImplementedPropertyInfo>.Builder properties,
            TransformResultBuilder<ImplementInfo> builder,
            CancellationToken token)
        {
            bool targetIsReadOnly = type.IsReadOnly && type.TypeKind == TypeKind.Struct;

            foreach (var source in WithInherited(interfaceSymbol))
            {
                //the interface that declares the member, which is not necessarily the one named in the
                //attribute: an explicit implementation has to name the declaring interface
                string interfaceName = source.ToDisplayString(TypeFormat);

                foreach (var member in source.GetMembers())
                {
                    token.ThrowIfCancellationRequested();

                    if (member is not IPropertySymbol prop
                        || prop.IsIndexer                                    //no auto-implementation exists for an indexer
                        || prop.IsStatic                                     //static abstract members need a real implementation
                        || !prop.IsAbstract                                  //a default interface implementation already has a body
                        || prop.GetMethod is null                            //set-only: nothing sensible to auto-implement
                        || prop.DeclaredAccessibility != Accessibility.Public //non-public members require explicit implementation
                        || !claimed.Add(prop.Name))                          //already handled for another interface
                        continue;

                    if (FindExistingMember(type, prop.Name) is { } existing)
                    {
                        //the type (or a base) already provides this name; leave it alone, but say so when it
                        //cannot possibly satisfy the interface, because the compile error that follows points
                        //at the type rather than at the mismatch
                        if (existing is not IPropertySymbol existingProp
                            || !SymbolEqualityComparer.Default.Equals(existingProp.Type, prop.Type))
                            builder.Diagnostics.Add(TypeMismatch.CreateDiagnosticInfo(
                                LocationInfo.From(existing), type.Name, prop.Name, interfaceSymbol.Name));
                        continue;
                    }

                    //a readonly struct can only carry an init accessor, so a plain set from the interface
                    //becomes an init plus an explicit implementation that throws -- the same accommodation
                    //RecordForger makes for a readonly type with a settable interface property
                    bool needsShim = targetIsReadOnly && prop.SetMethod is { IsInitOnly: false };

                    var setter =
                        prop.SetMethod is null
                            ? alwaysCreateSetters
                                ? targetIsReadOnly ? SetterKind.Init : SetterKind.Set
                                : SetterKind.None
                        : prop.SetMethod.IsInitOnly || targetIsReadOnly
                            ? SetterKind.Init
                            : SetterKind.Set;

                    properties.Add(new(prop.Type.ToDisplayString(TypeFormat), prop.Name, setter, interfaceName, needsShim));
                }
            }
        }

        static IEnumerable<INamedTypeSymbol> WithInherited(INamedTypeSymbol interfaceSymbol)
        {
            yield return interfaceSymbol;
            foreach (var inherited in interfaceSymbol.AllInterfaces)
                yield return inherited;
        }

        /// <summary>
        /// Finds a member that blocks generating <paramref name="name"/>: anything at all declared on the type
        /// itself (all partial parts are merged into the symbol), or a non-static public/protected member
        /// inherited from a base class, which would implicitly implement the interface member.
        /// </summary>
        /// <remarks>
        /// Members produced by <em>other</em> source generators are invisible here — generators cannot see each
        /// other's output — so a member generated elsewhere on the same type will collide.
        /// </remarks>
        static ISymbol? FindExistingMember(INamedTypeSymbol type, string name)
        {
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                bool isTarget = SymbolEqualityComparer.Default.Equals(current, type);
                foreach (var member in current.GetMembers(name))
                {
                    if (isTarget) return member;
                    if (!member.IsStatic && member.DeclaredAccessibility
                        is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
                        return member;
                }
            }
            return null;
        }

        static bool AlreadyImplements(INamedTypeSymbol type, INamedTypeSymbol interfaceSymbol)
        {
            foreach (var implemented in type.AllInterfaces)
                if (SymbolEqualityComparer.Default.Equals(implemented, interfaceSymbol))
                    return true;
            return false;
        }

        /// <summary>
        /// Reads the interface types out of the attribute's <c>params Type[]</c> argument, which the compiler
        /// may hand over either as an array or, in error recovery, as a single type.
        /// </summary>
        static IEnumerable<INamedTypeSymbol> GetInterfaceArguments(AttributeData attributeData)
        {
            if (attributeData.ConstructorArguments.Length == 0) yield break;
            var arg = attributeData.ConstructorArguments[0];

            if (arg.Kind == TypedConstantKind.Type)
            {
                if (arg.Value is INamedTypeSymbol single) yield return single;
                yield break;
            }

            if (arg.Kind != TypedConstantKind.Array) yield break;
            foreach (var value in arg.Values)
                if (value.Value is INamedTypeSymbol symbol)
                    yield return symbol;
        }

        /// <summary>
        /// A partial part can only be added when the declaration and every enclosing declaration is partial.
        /// Checking the syntax of this part is enough: C# requires <c>partial</c> on all parts of a type.
        /// </summary>
        static bool IsPartialAllTheWayUp(TypeDeclarationSyntax node)
        {
            for (SyntaxNode? current = node; current is TypeDeclarationSyntax declaration; current = current.Parent)
                if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                    return false;
            return true;
        }
    }
}
