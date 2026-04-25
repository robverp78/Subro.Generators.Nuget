using Microsoft.CodeAnalysis;
using Subro.JsonTypes;
using System.Runtime.InteropServices.ComTypes;
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


            var AotTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            $"{JsonTypesNamepace}.{nameof(MarkAsJsonSerializableAttribute)}",
            static (ctx, _) => true,
            TransformAttributeType)
                .Where(static type => type is not null)
                .Select(static (type,_) => type!);

            AddAotTypeRegistration(context, AotTypes);
        }

        internal static TypeReference? TransformAttributeType(GeneratorAttributeSyntaxContext ctx, CancellationToken token)
        {
            var target = ctx.TargetSymbol;

            var typeSymbol = target switch
            {
                ITypeSymbol type => type,
                IParameterSymbol parameter => parameter.Type,
                IMethodSymbol method => method.ReturnType,
                _ => null
            };
            if (typeSymbol == null)
                return null;
            return new TypeReference(typeSymbol);
        }

        

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
