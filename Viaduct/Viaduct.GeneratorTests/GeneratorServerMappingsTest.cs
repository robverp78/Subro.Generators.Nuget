using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Subro.Generators.Tests;
using System.Reflection;
using System.Runtime.CompilerServices;
using Viaduct.Generation;

namespace Viaduct.Tests
{
    public class GeneratorServerMappingsTest : GeneratorTestsBase
    {
        private static TestCompilation RunGenerator(string source)
        {
            var compiler = createCompiler();
            compiler.IncrementalGenerators.Add(new ViaductGenerator());

            return compiler.Create(source);
        }


        private const string Usings = """
            using System;
            using System.Threading.Tasks;
            using Viaduct;
            using Viaduct.Server;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.AspNetCore.Routing;

        """;

        private const string AsyncInterfaceSource = Usings + """

            namespace TestApp
            {
                public interface IUserApi
                {
                    Task<string> GetUsers();
                    Task CreateUser(string name);
                }

                class Startup
                {
                    void Configure()
                    {
                        ServerMappings.RegisterEndpoints<IUserApi>();
                    }
                }
            }
            """;



        [Test]
        public async Task GeneratedCodeCompiles()
        {
            var compilation = RunFullPipeline(AsyncInterfaceSource);

            var errors = compilation.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error
                && !d.ToString().Contains("Viaduct_AotTypes"))
                .ToArray();

            await Assert.That(errors).IsEmpty();
        }

        static IIncrementalGenerator CreateStjGenerator()
        {
            // The assembly is already loaded because typeof(JsonSerializer) references it.
            // The generator type is internal but accessible via reflection.
            var stjAssembly = typeof(System.Text.Json.JsonSerializer).Assembly;

            // In .NET 8+, the source generator lives in a separate assembly
            // that ships alongside System.Text.Json
            var generatorAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "System.Text.Json.SourceGeneration")
                ?? throw new InvalidOperationException(
                    "System.Text.Json.SourceGeneration assembly not loaded. " +
                    "Ensure your test project references it.");

            var generatorType = generatorAssembly.GetType(
                "System.Text.Json.SourceGeneration.JsonSourceGenerator")
                ?? throw new InvalidOperationException(
                    "JsonSourceGenerator type not found in assembly.");

            return (IIncrementalGenerator)Activator.CreateInstance(generatorType)!;
        }

        [Test]
        public async Task GeneratedCodeContainsExpectedMappings()
        {
            var result = RunGenerator(AsyncInterfaceSource);
            var generatedCode = string.Join("\r\n", result.GeneratorResults.SelectMany(g=>g.GeneratedTrees.Select(t=>t.GetText().ToString())));

            await Assert.That(generatedCode).Contains("IUserApi_Viaduct_ServerMappings");
            await Assert.That(generatedCode).Contains("MapMethods");
            await Assert.That(generatedCode).Contains("GetUsers");
            await Assert.That(generatedCode).Contains("CreateUser");
        }

        [Test]
        public async Task NoWarningsForFullyAsyncInterface()
        {
            var result = RunGenerator(AsyncInterfaceSource);

            var warnings = result.Diagnostics
                .Where(d => d.Id.StartsWith(ViaductGeneratorFunctions.ViaductErrorCodePrefix))
                .ToArray();

            await Assert.That(warnings).IsEmpty();
        }

        [Test]
        public async Task EmitsWarningsForNonAsyncMethods()
        {
            const string source = Usings + """

                namespace TestApp
                {
                    public interface IMixedApi
                    {
                        Task<string> GetUsersAsync();
                        void DeleteUser(int id);
                        int GetCount();
                    }

                    class Startup
                    {
                        void Configure()
                        {
                            ServerMappings.RegisterEndpoints<IMixedApi>();
                        }
                    }
                }
                """;

            var result = RunGenerator(source);

            var warnings = result.Diagnostics
                .Where(d => d.Id == ViaductErrors.NotAnAsyncMethod.GetCode())
                .ToArray();

            // Two non-async methods: DeleteUser and GetCount
            await Assert.That(warnings.Length).IsEqualTo(2);
        }



        [Test]
        public async Task StillGeneratesCodeWhenWarningsExist()
        {
            const string source = Usings + """

                namespace TestApp
                {
                    public interface IPartialAsyncApi
                    {
                        Task GetAsync();
                        void FireAndForget();
                    }

                    class Startup
                    {
                        void Configure()
                        {
                            ServerMappings.RegisterEndpoints<IPartialAsyncApi>();
                        }
                    }
                }
                """;

            var result = RunGenerator(source);

            // Warnings emitted but code still generated
            await Assert.That(result.Diagnostics).Contains(d => d.Id == ViaductErrors.NotAnAsyncMethod.GetCode());
            await Assert.That(result.GeneratorResults.FirstOrDefault()?.GeneratedTrees ?? []).IsNotEmpty();
        }
    }
}
