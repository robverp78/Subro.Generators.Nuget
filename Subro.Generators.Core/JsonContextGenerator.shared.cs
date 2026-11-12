using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Subro.Generators.Json
{
    internal partial class JsonContextGenerator
    {
        const string JsonTypesNamepace = "Subro.JsonTypes",
            jsonTypesInternalClassName = "InternalJsonTypes";

        /// <summary>
        /// A <c>[JsonSerializable]</c> attribute for each type, written as given: each name must be a C# type as it can stand
        /// in <c>typeof</c>, fully qualified (<see cref="SymbolDisplayFormat.FullyQualifiedFormat"/>), so that <c>int</c> and
        /// <c>int?</c> stay keywords rather than becoming <c>global::int</c>, which does not compile.
        /// </summary>
        public static string CreateTypeAttributes(IReadOnlyCollection<string> typeFullNames)
        {
            var sb = new StringBuilder($$"""
namespace {{JsonTypesNamepace}}
{

""");
            foreach (var type in typeFullNames)
                sb.Append($@"
    [global::System.Text.Json.Serialization.JsonSerializable(typeof({type}))]");


            sb.Append(@"
    partial class ").Append(jsonTypesInternalClassName).Append(@"
    {
    }
}");
            return sb.ToString();
        }

        public static string? CreateTypeAttributes(IEnumerable<TypeReference> typeRefs)
        {
            var types = GetTypeNames(typeRefs);
            if (types.Count == 0) return null;
            return CreateTypeAttributes(types);
        }

        public static IReadOnlyCollection<string> GetTypeNames(IEnumerable<TypeReference> typeRefs) => [.. typeRefs.Select(static t => t!.FullName).Distinct()];


        const string DefaultFileName = "Subro_JsonTypes.g.cs";

        public static void AddAotTypeRegistration(IncrementalGeneratorInitializationContext context, IncrementalValuesProvider<TypeReference> typeRefs, string fileName = DefaultFileName)
        {
            context.RegisterSourceOutput(typeRefs.Collect(), (ctx, types) =>
            {
                AddAotTypeRegistration(ctx, types, fileName);
            });
        }
        public static void AddAotTypeRegistration(SourceProductionContext ctx, IEnumerable<TypeReference> typeRefs, string fileName = DefaultFileName)
        {
            var types = GetTypeNames(typeRefs);
            if (types.Count == 0) return;


            ctx.AddSource(fileName, CreateTypeAttributes(types));
        }
    }
}
