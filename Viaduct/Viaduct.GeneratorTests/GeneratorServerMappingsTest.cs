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

        /// <summary>
        /// An interface in the global namespace has no namespace to import, and the generator used to emit
        /// the compiler's own spelling of it — <c>using &lt;global namespace&gt;;</c> — which does not parse.
        /// The failure landed in generated code, where the error message points at nothing the author wrote.
        /// </summary>
        [Test]
        public async Task InterfaceInGlobalNamespace_GeneratesCompilableCode()
        {
            var source = Usings + """

                public interface IGlobalApi
                {
                    Task<string> GetThings();
                }

                class GlobalStartup
                {
                    void Configure()
                    {
                        ServerMappings.RegisterEndpoints<IGlobalApi>();
                    }
                }
                """;

            var compilation = RunFullPipeline(source);

            var errors = compilation.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error
                && !d.ToString().Contains("Viaduct_AotTypes"))
                .ToArray();

            await Assert.That(errors).IsEmpty();
        }

        /// <summary>
        /// A compilation that does not compile must still not take the generator down with it.
        /// </summary>
        /// <remarks>
        /// A registration whose types cannot be resolved is what every project looks like halfway through
        /// being written, and what one looks like when it has an ordinary error elsewhere. The generator used
        /// to return a <c>default</c> transform result there, whose diagnostics array is uninitialized, and the
        /// incremental pipeline then threw <c>ArgumentNullException (Parameter 'many')</c> from inside Roslyn.
        /// The build failed with that stack trace and nothing else — the real compile error never printed.
        /// </remarks>
        [Test]
        public async Task BrokenCompilation_DoesNotCrashTheGenerator()
        {
            var source = Usings + """

                namespace TestApp
                {
                    public interface IBrokenApi
                    {
                        Task<string> GetThings();
                    }

                    public class BrokenService : IBrokenApi
                    {
                        public Task<string> GetThings() => Task.FromResult(NoSuchType.Value);
                    }

                    class BrokenStartup
                    {
                        void Configure()
                        {
                            ServerMappings.RegisterEndpoints<IBrokenApi>();
                        }
                    }
                }
                """;

            var compiler = createCompiler();
            compiler.IncrementalGenerators.Add(new ViaductGenerator());

            // The compilation is meant to be broken here, so the harness must not reject it for being so.
            compiler.CheckErrors = false;

            var compilation = compiler.Create(source);

            // The compiler's own error is what the author needs to see, and it has to survive the generator.
            await Assert.That(compilation.Diagnostics.Any(d => d.Id == "CS0103")).IsTrue();
        }

        /// <summary>
        /// The summary, description and operationId an endpoint carries into the OpenAPI document, taken from
        /// the interface's own documentation so the two cannot drift apart.
        /// </summary>
        [Test]
        public async Task GeneratedCodeCarriesEndpointDocumentation()
        {
            var source = Usings + """

                namespace TestApp
                {
                    public interface IDocumentedApi
                    {
                        /// <summary>Lists the things.</summary>
                        /// <remarks>All of them.</remarks>
                        Task<string> GetThingsAsync();
                    }

                    class DocumentedStartup
                    {
                        void Configure()
                        {
                            ServerMappings.RegisterEndpoints<IDocumentedApi>();
                        }
                    }
                }
                """;

            var result = RunGenerator(source);
            var generatedCode = string.Join(Environment.NewLine, result.GeneratorResults.SelectMany(g => g.GeneratedTrees.Select(t => t.GetText().ToString())));

            await Assert.That(generatedCode).Contains("WithSummary(\"Lists the things.\")");
            await Assert.That(generatedCode).Contains("WithDescription(\"All of them.\")");
            await Assert.That(generatedCode).Contains("WithName(\"DocumentedApi_GetThings\")");
            await Assert.That(generatedCode).Contains("WithTags(\"DocumentedApi\")");
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

        private const string GenericCrudSource = Usings + """

            namespace TestApp
            {
                public interface ICrud<TRecord, TKey>
                {
                    Task<TKey> CreateNew(TRecord values);
                    Task<TRecord> GetById(TKey id);
                }

                // Strong-key alias: only the record type varies, the key is fixed to Guid.
                public interface ICrudGuid<TRecord> : ICrud<TRecord, System.Guid> { }

                public record AddressRecord(int Id, string Street);
                public record PersonRecord(int Id, string Name);

                class Startup
                {
                    void Configure()
                    {
                        ServerMappings.RegisterEndpoints<ICrudGuid<AddressRecord>>();
                        ServerMappings.RegisterEndpoints<ICrudGuid<PersonRecord>>();
                    }
                }
            }
            """;

        [Test]
        public async Task ClosedGenericInterface_GeneratesDistinctMappingsPerClosure()
        {
            var result = RunGenerator(GenericCrudSource);
            var generatedCode = string.Join("\r\n", result.GeneratorResults.SelectMany(g => g.GeneratedTrees.Select(t => t.GetText().ToString())));

            // Inherited members from ICrud<,> are picked up through the closed alias.
            await Assert.That(generatedCode).Contains("CreateNew");
            await Assert.That(generatedCode).Contains("GetById");

            // Each closure gets its own mapping class (identifier encodes the type argument).
            await Assert.That(generatedCode).Contains("ICrudGuid_AddressRecord");
            await Assert.That(generatedCode).Contains("ICrudGuid_PersonRecord");

            // Routes are disambiguated by the type-argument segment.
            await Assert.That(generatedCode).Contains("\"AddressRecord\"");
            await Assert.That(generatedCode).Contains("\"PersonRecord\"");

            // The service is resolved as the fully-qualified closed generic.
            await Assert.That(generatedCode).Contains("ICrudGuid<global::TestApp.AddressRecord>");
        }

        [Test]
        public async Task ClosedGenericInterface_GeneratedCodeCompiles()
        {
            var compilation = RunFullPipeline(GenericCrudSource);

            var errors = compilation.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error
                && !d.ToString().Contains("Viaduct_AotTypes"))
                .ToArray();

            await Assert.That(errors).IsEmpty();
        }

        [Test]
        public async Task ViaductIgnoreForServer_ExcludesMethodFromServerMappings()
        {
            const string source = Usings + """

                namespace TestApp
                {
                    public interface IPartialServerApi
                    {
                        Task<string> GetUsers();

                        [ViaductIgnoreForServer]
                        Task<string> GetClientOnlyData();
                    }

                    class Startup
                    {
                        void Configure()
                        {
                            ServerMappings.RegisterEndpoints<IPartialServerApi>();
                        }
                    }
                }
                """;

            var result = RunGenerator(source);

            var serverMappingCode = string.Join("\r\n", result.GeneratorResults
                .SelectMany(g => g.GeneratedTrees.Select(t => t.GetText().ToString()))
                .Where(code => code.Contains("ServerMappings")));

            await Assert.That(serverMappingCode).IsNotEmpty();
            await Assert.That(serverMappingCode).Contains("GetUsers");
            await Assert.That(serverMappingCode.Contains("GetClientOnlyData")).IsFalse();
        }
    }
}
