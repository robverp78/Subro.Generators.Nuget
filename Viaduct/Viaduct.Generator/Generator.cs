using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Viaduct.Generation.ViaductGeneratorFunctions;
using Subro.Generators;

namespace Viaduct.Generation
{

    [Generator]
    public partial class ViaductGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            //general helper functions needed for the generated code to work
            context.RegisterPostInitializationOutput(static ctx => ctx.AddSource("Viaduct_Helperfunctions.g.cs", HelperFunctionsCode));


            //register diagnostics and get the results
            var results = context.CreateValuesProvider(
                predicate: IsCandidateInvocation,
                transform: GetMethodCallInfo
            );

            //aot types
            var aotTypes = results.Select(static (p,_) => p.AotTypes).Where(static tc=> tc is not null).SelectMany(static (t,_) => t!.Value);
            Subro.Generators.Json.JsonContextGenerator.AddAotTypeRegistration(context, aotTypes, "Viaduct_JsonTypes.g.cs");

            // One output per interface and side: multiple call sites for the same interface (e.g. AddEndpoints +
            // AddScopedAndMap) generate one mapping file. Grouped into items that compare by value, so an edit that
            // leaves an interface and its calls as they were leaves its file alone, rather than every file being
            // written again because one call anywhere changed.
            var interfaceCalls = results
                .Select(static (p, _) => p.Method)
                .Where(static m => m is not null)
                .Collect()
                .SelectMany(static (infos, _) => infos
                    .GroupBy(static info => (info!.IsServerCall, info.InterfaceMetaData.FullyQualifiedName))
                    .Select(static group => new InterfaceCalls(
                        group.Key.IsServerCall, group.First()!.InterfaceMetaData, group.Select(static m => m!).ToEquatable())));

            // The XML documentation files of referenced projects, handed to the generator as additional files by the
            // props in Viaduct.Server and Viaduct.Client. Only an interface that needs one opens it; see ReferenceDocumentation.
            var documentationFiles = context.AdditionalTextsProvider
                .Where(static file => file.Path.EndsWith(ViaductGeneratorFunctions.DocumentationFileExtension, StringComparison.OrdinalIgnoreCase))
                .Collect();

            context.RegisterSourceOutput(interfaceCalls.Combine(documentationFiles), static (spc, input) =>
            {
                var (calls, files) = input;
                {
                    var ifInfo = calls.InterfaceMetaData.WithDocumentation(files, spc.CancellationToken);

                    string generated, type;
                    if (calls.IsServerCall) //server
                    {                  
                        generated = CreateServerMappingCode(ifInfo, calls.Callers);
                        type = "Server";
                    }
                    else //client
                    {
                        generated = new Client.ClientGeneratorFunctions.ClientCodeCreator(ifInfo, calls.Callers).Create();
                        type = "Client";
                    }

                    spc.AddSource($"Viaduct_{ifInfo.Identifier}_{type}Mapping.g.cs", generated);
                }                   
            });          
        }
    }



    public static partial class ViaductGeneratorFunctions
    {
        /// <summary>
        /// The extension the build copies referenced XML documentation to. Not <c>.xml</c>: every generator is
        /// handed the same AdditionalFiles, and ASP.NET Core's XML comment generator processes any .xml among
        /// them — given the framework's documentation it generates a file large enough to fail the build.
        /// </summary>
        internal const string DocumentationFileExtension = ".viaductdoc";

        internal const string interceptorAttribute = """
            namespace System.Runtime.CompilerServices
            {
                [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
                file sealed class InterceptsLocationAttribute(int version, string data) : Attribute
                {
                }
            }
            """;

        internal static StringBuilder AppendInterceptor(this StringBuilder sb, MethodCallerInfo method)
        {
            sb.AppendLine($"\t\t\t{method.Interceptor}");
            /*
            var location = method.Location;
            var lineSpan = location.GetLineSpan();

            int line = lineSpan.StartLinePosition.Line + 1; // 0-based to 1-based
            int column = lineSpan.StartLinePosition.Character + 1;
            string filePath = lineSpan.Path;

            sb.AppendLine($"\t\t\t[InterceptsLocation(@\"{filePath}\",{line},{column})]");
            */
            return sb;
        }

        const string getBasePathFunctionName = "GetBasePath";
        internal const string GeneratedNameSpace = "Viaduct.Generated";
        const string HelperFunctionsClass = "ViaductGeneratedFunctions";
        const string AppendRoutePartFunction = "AppendRoutePart";
        public const string HelperFunctionsCode = $$"""
#nullable enable


namespace {{GeneratedNameSpace}}
{
    using System.Text;

    internal static class {{HelperFunctionsClass}}
    {
        public static string {{getBasePathFunctionName}}({{nameof(ViaductOptions)}} options, string? ifBasePath, string interfaceName, string? typeArgSegment = null)
        {
            ifBasePath ??= options.{{nameof(ViaductOptions.InterfaceBasePath)}};
            string interfacePart;
            if(ifBasePath != null)
                interfacePart = ifBasePath;
            else if(options.{{nameof(ViaductOptions.UseInterfaceNameForPath)}} || typeArgSegment != null)
                interfacePart = "/" + GetInterfaceName(interfaceName);
            else
                interfacePart = "";

            if(typeArgSegment != null)
            {
                var typePart = "/" + typeArgSegment;
                return options.{{nameof(ViaductOptions.GenericTypeArgPathFirst)}}
                    ? options.{{nameof(ViaductOptions.BasePath)}} + typePart + interfacePart
                    : options.{{nameof(ViaductOptions.BasePath)}} + interfacePart + typePart;
            }
            return options.{{nameof(ViaductOptions.BasePath)}} + interfacePart;
        }

        static string GetInterfaceName(string name)
        {
            if (name.Length > 2 && name[0] == 'I' && char.IsUpper(name[1]))
                return name.Substring(1);
            return name;
        }

        public static StringBuilder {{AppendRoutePartFunction}}(this StringBuilder sb, string routePart)
        {
            if (string.IsNullOrEmpty(routePart)) return sb;
            if (routePart[0] != '/')
                sb.Append('/');
            return sb.Append(routePart);            
        }
    }


}
""";

        internal static string createGetBasePathCode(string? basePath, string interfaceName, string? typeArgSegment = null, string optionsName = "options")
            => $"{HelperFunctionsClass}.{getBasePathFunctionName}({optionsName}, {(basePath is null ? "null" : $"\"{basePath}\"")}, \"{interfaceName}\", {(typeArgSegment is null ? "null" : $"\"{typeArgSegment}\"")})";


    }





}