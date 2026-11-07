using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Subro.RecordForge;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Subro.Generators
{
    partial class RecordForger
    {

        internal static readonly DiagnosticDescriptorInfo MetadataNameTaken =
            Diagnostics.Warning("AIRMETANAME", "'{0}' has a property named '{1}', which the generated type's metadata uses; no metadata was generated. Set Metadata = false to silence this");

        /// <summary>The static members the metadata adds, which a property of the interface must not be named.</summary>
        static readonly string[] metadataMemberNames = ["Fields", "Projection", "ProjectionFrom", "From"];

        static TransformResult<CreationInfo> Transform(INamedTypeSymbol InterfaceSymbol, GenerateTypeInfo info, AttributeData attributeData,
            Compilation compilation, CancellationToken token = default)
        {
            var builder = new TransformResultBuilder<CreationInfo>();
            builder.Result = Create(InterfaceSymbol, info, attributeData, compilation, builder, forBase: false, token);
            return builder;
        }

        /// <summary>
        /// What to generate for <paramref name="InterfaceSymbol"/>. For a base record (<paramref name="forBase"/>) only what a
        /// derived record needs is worked out: its name, its properties and its primary constructor, not its attributes.
        /// </summary>
        static CreationInfo? Create(INamedTypeSymbol InterfaceSymbol, GenerateTypeInfo info, AttributeData attributeData,
            Compilation compilation, TransformResultBuilder<CreationInfo> builder, bool forBase, CancellationToken token)
        {
            var settings = ReadSettings(attributeData, out var recName, out var recNameSpace);
            string InterfaceNamespace = InterfaceSymbol.GetNamespace();
            recName ??= InterfaceSymbol.Name.Substring(1);
            recNameSpace ??= InterfaceNamespace;

            //a record whose interface inherits one that has a record of its own derives from that record
            CreationInfo? baseInfo = null;
            if (info.KindValue == (int)RecordKind.Record)
                foreach (var baseInterface in InterfaceSymbol.Interfaces)
                {
                    if (token.IsCancellationRequested) return null;
                    if (GetGenerateRecordAttribute(baseInterface) is not { } baseAttribute) continue;

                    var baseKind = GetGenerateTypeInfo(baseAttribute, 0);
                    if (baseKind.KindValue != (int)RecordKind.Record) continue;

                    baseInfo = Create(baseInterface, baseKind, baseAttribute, compilation, builder, forBase: true, token);
                    if (baseInfo is not null) break;
                }

            var baseProperties = new Dictionary<string, PropertyCreationInfo>();
            var baseCtor = new HashSet<string>();
            if (baseInfo is not null)
            {
                foreach (var p in baseInfo.Properties) baseProperties[p.Name] = p;
                foreach (var p in baseInfo.GetConstructors().First()) baseCtor.Add(p.Name);
            }

            bool describe = !forBase && settings.Metadata;
            var props = ImmutableArray.CreateBuilder<PropertyCreationInfo>();
            var declaringInterfaces = new List<INamedTypeSymbol>();
            var claimed = new HashSet<string>();

            foreach (var source in BaseFirst(InterfaceSymbol))
            {
                bool declares = false;
                foreach (var member in source.GetMembers())
                {
                    if (token.IsCancellationRequested) return null;

                    //interface contains other symbols (methods) and/or properties without getters.
                    //For now assume implementers will create their own if it is partial.
                    if (member is not IPropertySymbol p || p.GetMethod is null || p.IsStatic || p.IsIndexer || !claimed.Add(p.Name))
                        continue;

                    declares = true;
                    baseProperties.TryGetValue(p.Name, out var fromBase);
                    props.Add(new PropertyCreationInfo(p)
                    {
                        QualifiedType = p.Type.ToDisplayString(InterfaceImplementer.TypeFormat),
                        DeclaringInterface = source.ToDisplayString(TypeOfFormat),
                        AllowsNull = AllowsNull(p.Type),
                        Attributes = describe ? WriteAttributes(p.GetAttributes(), compilation, token) : GeneratorFunctions.EmptyStrings,
                        FromBase = fromBase is not null,
                        BaseSettable = fromBase is not null && !baseCtor.Contains(p.Name)
                            && (baseInfo!.CreatesSetter(fromBase) || fromBase.BaseSettable),
                    });
                }
                if (declares) declaringInterfaces.Add(source);
            }

            if (props.Count == 0)
            {
                if (!forBase)
                    builder.Diagnostics.Add(Diagnostics.Warning("AIRNOPROPS", "{0} does not contain properties. No record was generated")
                        .CreateDiagnosticInfo(null, InterfaceSymbol.Name));
                return null;
            }

            if (describe && props.FirstOrDefault(static p => Array.IndexOf(metadataMemberNames, p.Name) >= 0) is { } taken)
            {
                builder.Diagnostics.Add(MetadataNameTaken.CreateDiagnosticInfo(LocationInfo.From(attributeData, token), InterfaceSymbol.Name, taken.Name));
                settings = settings with { Metadata = false };
                describe = false;
            }

            return new CreationInfo(
                info,
                InterfaceSymbol.Name,
                InterfaceNamespace,
                recName,
                recNameSpace,
                props.ToEquatable(),
                settings)
            {
                QualifiedInterface = InterfaceSymbol.ToDisplayString(TypeOfFormat),
                InterfaceAccess = IsPublic(InterfaceSymbol) ? "public" : "internal",
                BaseRecord = baseInfo is null ? null : QualifiedName(baseInfo.RecordNameSpace, baseInfo.RecordName),
                BasePrimaryConstructor = baseInfo is null ? GeneratorFunctions.EmptyStrings
                    : baseInfo.GetConstructors().First().Select(static p => p.Name).ToEquatable(),
                BaseHasMetadata = baseInfo is not null && baseInfo.Settings.Metadata
                    && !baseInfo.Properties.Any(static p => Array.IndexOf(metadataMemberNames, p.Name) >= 0),
                InterfaceAttributes = describe
                    ? declaringInterfaces.Select(i => new InterfaceAttributeInfo(i.ToDisplayString(TypeOfFormat), WriteAttributes(i.GetAttributes(), compilation, token))).ToEquatable()
                    : new(ImmutableArray<InterfaceAttributeInfo>.Empty),
            };
        }

        static CreationSettings ReadSettings(AttributeData attributeData, out string? recName, out string? recNameSpace)
        {
            var settings = new CreationSettings();
            recName = recNameSpace = null;

            foreach (var arg in attributeData.NamedArguments)
                switch (arg.Key)
                {
                    case nameof(GenerateRecordAttribute.RecordName): recName = arg.Value.Value?.ToString(); break;
                    case nameof(GenerateRecordAttribute.NameSpace): recNameSpace = arg.Value.Value?.ToString(); break;
                    case nameof(GenerateRecordAttribute.AsPartial):
                        settings = settings with { AsPartial = arg.Value.Value as bool? ?? GenerateRecordAttribute.DefaultAsPartial }; break;
                    case nameof(GenerateRecordAttribute.AsAbstract): settings = settings with { AsAbstract = arg.Value.Value as bool? ?? false }; break;
                    case nameof(GenerateRecordAttribute.AlwaysCreateSetters): settings = settings with { AlwaysCreateSetters = arg.Value.Value as bool? ?? false }; break;
                    case nameof(GenerateRecordAttribute.ConstructorUsage):
                        settings = settings with { ConstructorUsage = (CtorUsage)(arg.Value.Value as int? ?? 0) }; break;
                    case nameof(GenerateRecordAttribute.Metadata):
                        settings = settings with { Metadata = arg.Value.Value as bool? ?? GenerateRecordAttribute.DefaultMetadata }; break;
                }

            return settings;
        }

        /// <summary>The <see cref="GenerateRecordAttribute"/> on an interface, in this compilation or a referenced one.</summary>
        static AttributeData? GetGenerateRecordAttribute(INamedTypeSymbol interfaceSymbol)
        {
            foreach (var attribute in interfaceSymbol.GetAttributes())
                if (attribute.AttributeClass is { Name: nameof(GenerateRecordAttribute) } attributeClass
                    && attributeClass.GetNamespace() == attributeNamespace)
                    return attribute;
            return null;
        }

        /// <summary>
        /// The interface and everything it inherits, the deepest base first, each once: so a base interface's properties come
        /// before the ones of the interfaces built on it, in the order they are declared.
        /// </summary>
        static List<INamedTypeSymbol> BaseFirst(INamedTypeSymbol interfaceSymbol)
        {
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var ordered = new List<INamedTypeSymbol>();
            Visit(interfaceSymbol);
            return ordered;

            void Visit(INamedTypeSymbol current)
            {
                if (!seen.Add(current)) return;
                foreach (var inherited in current.Interfaces) Visit(inherited);
                ordered.Add(current);
            }
        }

        static bool AllowsNull(ITypeSymbol type) =>
            type.IsReferenceType
                ? type.NullableAnnotation != NullableAnnotation.NotAnnotated
                : type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

        /// <summary>Whether the type can be seen from outside its assembly: it and every type it is nested in are public.</summary>
        static bool IsPublic(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
                if (current.DeclaredAccessibility != Accessibility.Public)
                    return false;
            return true;
        }

        static string QualifiedName(string nameSpace, string name) =>
            string.IsNullOrEmpty(nameSpace) ? "global::" + name : "global::" + nameSpace + "." + name;

        /// <summary>
        /// Used for <see cref="GenerateRecordFromInterfaceAttribute"/>, so assembly level
        /// </summary>
        static IEnumerable<TransformResult<CreationInfo>> TransformAssemblyAttribute(
            GeneratorAttributeSyntaxContext context, CancellationToken token = default)
        {
            foreach (var attributeData in context.Attributes)
            {
                if (token.IsCancellationRequested) yield break;

                if (attributeData.ConstructorArguments.Length == 0)
                    continue; //this shouldn't be possible since the attribute requires at least one argument, but just in case, skip if no arguments

                var typeArg = attributeData.ConstructorArguments[0];

                // Normalize single Type or Type[] into one sequence
                IEnumerable<INamedTypeSymbol> typeSymbols = typeArg.Kind switch
                {
                    TypedConstantKind.Type
                        when typeArg.Value is INamedTypeSymbol single
                        => [single],

                    TypedConstantKind.Array
                        => typeArg.Values
                            .Where(static v  => v.Value is INamedTypeSymbol)
                            .Select(static v => (INamedTypeSymbol)v.Value!),

                    _ => []
                };

                var validTypes = typeSymbols.ToList();

                if (validTypes.Count == 0)
                {
                    yield return Diagnostics.Warning("AIRINVTYPE",
                        nameof(GenerateRecordFromInterfaceAttribute) + " on assembly level needs at least one valid type")
                        .CreateDiagnosticInfo(LocationInfo.From(attributeData, token));
                    continue;
                }

                // Resolve kind from second constructor argument if present
                var info = GetGenerateTypeInfo(attributeData, 1);
                if (info.IsNull)
                    yield return CreateInvalidKindDiagnostic(attributeData);
                else
                    foreach (var typeSymbol in validTypes)
                    {
                        if (token.IsCancellationRequested) yield break;
                        yield return Transform(typeSymbol, info, attributeData, context.SemanticModel.Compilation, token);
                    }
            }
        }



        /// <summary>
        /// Used for <see cref="GenerateRecordAttribute"/>
        /// </summary>
        static IEnumerable<TransformResult<CreationInfo>> TransformInterfaceAttribute(
            GeneratorAttributeSyntaxContext context, CancellationToken token = default)
        {
            foreach (var attributeData in context.Attributes)
            {
                if (token.IsCancellationRequested) yield break;
                var info = GetGenerateTypeInfo(attributeData, 0);
                if (info.IsNull)
                    yield return CreateInvalidKindDiagnostic(attributeData);
                else if (context.TargetNode is InterfaceDeclarationSyntax interfaceNode
                    && context.SemanticModel.GetDeclaredSymbol(interfaceNode) is INamedTypeSymbol InterfaceSymbol
                    )
                {      
                    yield return Transform(InterfaceSymbol, info, attributeData, context.SemanticModel.Compilation, token);
                }
                else
                {
                    //this should not be possible since the attribute is only allowed on interfaces
                    // and should be a named symbol
                    //Emit diagnostic? For now, just skip.
                }
            }
        }

  
            
    }
}
