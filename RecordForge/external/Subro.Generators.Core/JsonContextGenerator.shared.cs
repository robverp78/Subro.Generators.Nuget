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

        public static string CreateTypeAttributes(IReadOnlyCollection<string> typeFullNames)
        {
            var sb = new StringBuilder($$"""
namespace {{JsonTypesNamepace}}
{

""");
            foreach (var type in typeFullNames)
                sb.Append($@"
    [global::System.Text.Json.Serialization.JsonSerializable(typeof(global::{type}))]");


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
