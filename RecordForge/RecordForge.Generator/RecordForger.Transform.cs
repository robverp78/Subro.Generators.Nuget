using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Subro.RecordForge;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Subro.Generators
{
    partial class RecordForger
    {

        static TransformResult<CreationInfo> Transform(INamedTypeSymbol InterfaceSymbol, GenerateTypeInfo info, AttributeData attributeData, CancellationToken token = default)
        {
            var props = ImmutableArray.CreateBuilder<PropertyCreationInfo>();

            foreach (var member in InterfaceSymbol.GetMembers())
            {
                if (token.IsCancellationRequested) return default;
                if (member is IPropertySymbol p && p.GetMethod is not null)
                {
                    props.Add(new(p));
                }
                else
                {
                    //interface  contains other symbols (methods) and/or properties without setters.
                    //For now assume implementers will create their own if it is partial.
                    //TODO: warning if not partial (generate a notimplementedException instead?)
                }
            }

            if (props.Count == 0) return
                    Diagnostics.Warning("AIRNOPROPS", "{0} does not contain properties. No record was generated")
                    .CreateDiagnosticInfo(null, InterfaceSymbol.Name);


            if (token.IsCancellationRequested) return default;
            var settings = new CreationSettings();
            string? recName = null, recNameSpace = null;

            var namedArgs = attributeData.NamedArguments;
            foreach (var arg in namedArgs)
                switch (arg.Key)
                {
                    case nameof(GenerateRecordAttribute.RecordName): recName = arg.Value.Value?.ToString(); break;
                    case nameof(GenerateRecordAttribute.NameSpace): recNameSpace = arg.Value.Value?.ToString(); break;
                    case nameof(GenerateRecordAttribute.AsPartial):
                        settings = settings with { AsPartial = arg.Value.Value as bool? ?? GenerateRecordAttribute.DefaultAsPartial }; break;
                    case nameof(GenerateRecordAttribute.AsAbstract): settings = settings with { AsAbstract = arg.Value.Value as bool? ?? false }; break;
                    case nameof(GenerateRecordAttribute.AlwaysCreateSetters): settings = settings with { AlwaysCreateSetters = arg.Value.Value as bool? ?? false } ;break;
                    case nameof(GenerateRecordAttribute.ConstructorUsage):
                        settings = settings with {ConstructorUsage = (CtorUsage)(arg.Value.Value as int? ?? 0)}; break;
                }

            string InterfaceNamespace = InterfaceSymbol.GetNamespace();

            return new CreationInfo(
                info,
                InterfaceSymbol.Name,
                InterfaceNamespace,
                recName ?? InterfaceSymbol.Name.Substring(1),
                recNameSpace ?? InterfaceNamespace,
                props.ToEquatable(),
                settings);
        
        }

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
                        yield return Transform(typeSymbol, info, attributeData, token);
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
                    yield return Transform(InterfaceSymbol, info, attributeData, token);
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
