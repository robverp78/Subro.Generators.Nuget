using Microsoft.CodeAnalysis;
using Subro.JsonTypes;
using System.Collections.Immutable;
using System.Text;
using Subro.Generators;

namespace Subro.Generators.Json
{

    [Generator]
    internal partial class JsonContextGenerator: IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(static ctx => AddGeneralAotTypeRegistration(ctx));


            // Only the types that can have a contract come through; the rest are left out by TransformResult.
            var aotTypes = context.ValuesForAttributeWithMetadataName(
                $"{JsonTypesNamepace}.{nameof(MarkAsJsonSerializableAttribute)}",
                static (_, _) => true,
                TransformAttributeType);

            AddAotTypeRegistration(context, aotTypes);
        }

        /// <summary>Left out without a word: no result, and no diagnostics.</summary>
        /// <remarks>
        /// Not <c>default</c>: a default result's diagnostics array is uninitialized, and the pipeline throws when it collects
        /// one, from inside Roslyn and without naming this generator.
        /// </remarks>
        static readonly TransformResult<TypeReference> NoContract = ImmutableArray<DiagnosticInfo>.Empty;

        internal static TransformResult<TypeReference> TransformAttributeType(GeneratorAttributeSyntaxContext ctx, CancellationToken token)
        {
            var target = ctx.TargetSymbol;

            var typeSymbol = target switch
            {
                ITypeSymbol type => type,
                IParameterSymbol parameter => parameter.Type,
                IMethodSymbol method => method.ReturnType,
                _ => null
            };
            // A type parameter (or a type built from one) has no contract until it is known, void has none at all, and an
            // error type is the compiler's to report.
            if (typeSymbol is null or IErrorTypeSymbol || typeSymbol.SpecialType == SpecialType.System_Void || ContainsTypeParameter(typeSymbol))
                return NoContract;

            // Fully qualified, keywords kept: typeof(int?) and typeof(global::System.Collections.Generic.List<global::My.Sample>).
            return new TypeReference(typeSymbol.Name, typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        static bool ContainsTypeParameter(ITypeSymbol type) => type switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
            _ => false,
        };

        

        internal static void AddGeneralAotTypeRegistration(IncrementalGeneratorPostInitializationContext ctx, string fileName = "Subro_JsonSerializerContext.g.cs")
        {

            ctx.AddEmbeddedAttributeDefinition();
            ctx.AddSource(fileName, $@"
#nullable enable
using System.Runtime.CompilerServices;

namespace {JsonTypesNamepace}
{{
    [global::System.Text.Json.Serialization.JsonSerializable(typeof(int))] //to have at least one implementation, in order for the json type generator to run

    [global::System.Runtime.CompilerServices.CompilerGenerated]
    internal partial class {jsonTypesInternalClassName}: global::System.Text.Json.Serialization.JsonSerializerContext 
    {{
        [ModuleInitializer]
        internal static void Register()
        {{
            Subro.Json.AddContextToDefault(Default);
        }}
    }}


}}

");
        }


    }
}
