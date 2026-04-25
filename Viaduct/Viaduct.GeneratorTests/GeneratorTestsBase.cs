using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Subro.Generators.Tests;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Viaduct.Client;
using Viaduct.Client.UrlBuilding;
using Viaduct.Generation;
using Viaduct.Server;

namespace Viaduct.Tests
{
    public partial class GeneratorTestsBase
    {
        const string BaseSource = """
using System;
using System.Threading;
using System.Threading.Tasks;
using Viaduct;
using Viaduct.Server;
using Viaduct.Client;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;

""";



        protected static TestCompiler createCompiler()
        {
            // Force assemblies to load
            Type[] _ = [typeof(Viaduct.Client.ViaductClientFunctions),
                    typeof(ServerMappings), typeof(ViaductException), typeof(IServiceProvider),
                    typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute), typeof(IHttpClientBuilder),
                    typeof(IServiceCollection),   typeof(Action),  typeof(System.Text.Json.JsonSerializer),
                    typeof(Task), typeof(IEndpointRouteBuilder),typeof(HttpClient),typeof(System.Text.Json.JsonSerializer),
                    typeof(TypedResults),typeof(ModuleInitializerAttribute),typeof(Uri),
                    typeof(RouteGroupBuilder), typeof(AuthorizationEndpointConventionBuilderExtensions), typeof(IAuthorizeData)];


            var compiler = new TestCompiler();
            compiler.InterceptorNamespaces.Add("Viaduct.Generated");
            return compiler;
        }
        protected static TestCompilation CreateCompilation(string source) => createCompiler().Create(source);



        protected static TestCompilation RunFullPipeline(string source, params IIncrementalGenerator[] additionalGenerators)
        {
            var compiler = createCompiler();
            compiler.IncrementalGenerators.Add(new ViaductGenerator());
            compiler.IncrementalGenerators.AddRange(additionalGenerators);

            return compiler.Create(source);
        }

        protected static TestCompilation CreateModel(string source) 
            => createCompiler().Create(BaseSource + source);





        // ─── Client compilation helpers ─────────────────────────────────────

        protected static readonly CSharpParseOptions ClientParseOptions =
            new CSharpParseOptions(LanguageVersion.Preview)
                .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Viaduct.Generated")]);

        /// <summary>
        /// Creates a Roslyn compilation from multiple source strings.
        /// Loads Viaduct.Client types so the generated client code can resolve its dependencies.
        /// </summary>
        internal static TestCompilation CreateClientCompilation(params string[] sources)
            => createCompiler().Create(sources);


        /// <summary>
        /// Auto-generates a stub C# source containing the interface and any custom DTO types
        /// matching the given InterfaceMetaData. Custom types in the interface's namespace are
        /// emitted as empty stub classes.
        /// </summary>
        internal static string BuildStubInterfaceSource(
            InterfaceMetaData meta,
            params string[] extraTypeDeclarations)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Threading;");
            sb.AppendLine();
            sb.AppendLine($"namespace {meta.Namespace}");
            sb.AppendLine("{");

            // Emit any extra types the caller provides (e.g. "public class UserDto {}")
            foreach (var decl in extraTypeDeclarations)
                sb.AppendLine($"    {decl}");

            sb.AppendLine($"    public interface {meta.Name}");
            sb.AppendLine("    {");
            foreach (var m in meta.Methods)
            {
                sb.Append($"        {m.ReturnType.FullName} {m.Name}(");
                for (int i = 0; i < m.Parameters.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append($"{m.Parameters[i].Type.FullName} {m.Parameters[i].Name}");
                }
                if (m.CancellationToken is not null)
                {
                    if (m.Parameters.Length > 0) sb.Append(", ");
                    sb.Append($"CancellationToken {m.CancellationToken.Name}");
                }
                sb.AppendLine(");");
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }
    }
}
