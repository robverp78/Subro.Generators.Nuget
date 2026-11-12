using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using Viaduct.Generation;

namespace Viaduct.Tests
{
    /// <summary>
    /// What the generator costs a build: which documentation files it opens, and what it does again when the code changes.
    /// </summary>
    public class GeneratorPipelineTests : GeneratorTestsBase
    {
        /// <summary>An interface in an assembly of its own, documented only in that assembly's XML file.</summary>
        const string ContractsSource = """
            using System.Threading;
            using System.Threading.Tasks;
            using Viaduct;

            namespace Shop.Contracts
            {
                [BasePath("/orders")]
                public interface IOrderApi
                {
                    Task<string> FindAsync(string text, CancellationToken cancellationToken = default);
                }
            }
            """;

        const string ContractsDocumentation = """
            <?xml version="1.0"?>
            <doc>
                <assembly><name>Shop.Contracts</name></assembly>
                <members>
                    <member name="T:Shop.Contracts.IOrderApi"><summary>Orders.</summary></member>
                    <member name="M:Shop.Contracts.IOrderApi.FindAsync(System.String,System.Threading.CancellationToken)">
                        <summary>Finds orders by their text.</summary>
                    </member>
                </members>
            </doc>
            """;

        const string AppSource = """
            using Microsoft.Extensions.DependencyInjection;
            using Viaduct.Server;
            using Shop.Contracts;

            namespace Shop.App
            {
                class Startup
                {
                    void Configure(IServiceCollection services) => services.RegisterEndpoints<IOrderApi>();
                }
            }
            """;

        /// <summary>An additional file that counts how often it is read.</summary>
        sealed class CountingText(string path, string text) : AdditionalText
        {
            public int Reads { get; private set; }

            public override string Path { get; } = path;

            public override SourceText GetText(CancellationToken cancellationToken = default)
            {
                Reads++;
                return SourceText.From(text);
            }
        }

        /// <summary>The contracts assembly as a reference read from metadata: no syntax, so documentation only from its file.</summary>
        static MetadataReference ContractsReference()
        {
            var compiler = createCompiler();
            compiler.AssemblyName = "Shop.Contracts";
            var contracts = compiler.Create(ContractsSource).Compilation;

            using var image = new MemoryStream();
            var emitted = contracts.Emit(image);
            if (!emitted.Success)
                throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
            return MetadataReference.CreateFromImage(image.ToArray());
        }

        static (GeneratorDriver Driver, Compilation Compilation) Run(IEnumerable<AdditionalText> files, params string[] sources)
        {
            var compiler = createCompiler();
            var options = compiler.CreateOptions();
            var compilation = CSharpCompilation.Create(
                "Shop.App",
                sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, options, path: $"app{index}.cs")),
                compiler.GetReferences().Append(ContractsReference()),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                [new ViaductGenerator().AsSourceGenerator()],
                additionalTexts: [.. files],
                parseOptions: options,
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

            return (driver.RunGenerators(compilation), compilation);
        }

        static string Generated(GeneratorDriver driver, string fileNamePart)
            => string.Join("\n", driver.GetRunResult().Results.SelectMany(static r => r.GeneratedSources)
                .Where(source => source.HintName.Contains(fileNamePart))
                .Select(static source => source.SourceText.ToString()));

        [Test]
        public async Task ReferenceDocumentation_IsReadFromTheDeclaringAssemblysFile_Only()
        {
            var contracts = new CountingText("obj/viaduct/Shop.Contracts.viaductdoc", ContractsDocumentation);
            var other = new CountingText("obj/viaduct/Some.Package.viaductdoc", "<doc><members/></doc>");

            var (driver, _) = Run([contracts, other], AppSource);

            await Assert.That(Generated(driver, "IOrderApi")).Contains("Finds orders by their text.");
            await Assert.That(contracts.Reads).IsEqualTo(1);
            await Assert.That(other.Reads).IsEqualTo(0);
        }

        /// <summary>
        /// An edit that changes nothing Viaduct generates from leaves its output as it was: the models compare by value,
        /// and each interface is an output of its own.
        /// </summary>
        [Test]
        public async Task AnUnrelatedEdit_LeavesTheGeneratedFilesCached()
        {
            var contracts = new CountingText("obj/viaduct/Shop.Contracts.viaductdoc", ContractsDocumentation);
            var (driver, compilation) = Run([contracts], AppSource);

            var options = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
            var edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Shop.App { class Unrelated { } }", options, path: "unrelated.cs"));
            driver = driver.RunGenerators(edited);

            var outputs = driver.GetRunResult().Results.Single().TrackedOutputSteps
                .SelectMany(static step => step.Value)
                .SelectMany(static step => step.Outputs)
                .Select(static output => output.Reason)
                .ToList();

            await Assert.That(outputs).IsNotEmpty();
            await Assert.That(outputs.All(static reason => reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged)).IsTrue();
            // Nothing produced again, so the documentation was not read again either.
            await Assert.That(contracts.Reads).IsEqualTo(1);
        }
    }
}
