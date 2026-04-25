using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Runtime.CompilerServices;

namespace Subro.Generators.Tests
{
    public record TestCompilation(Compilation Compilation, 
        TestCompiler CreatedBy, 
        CSharpParseOptions ParseOptions, 
        IReadOnlyList<Diagnostic> Diagnostics,
         List<GeneratorDriverRunResult> GeneratorResults)
    {
        public TestTree? FirstTree => SyntaxTrees.FirstOrDefault();


        public IReadOnlyCollection<TestTree> SyntaxTrees 
            => field ??= [.. Compilation.SyntaxTrees.Select(Tree => (new TestTree(Tree, Compilation.GetSemanticModel(Tree))))];


        public IMethodSymbol? GetMethodSymbol(string methodName) 
            => SyntaxTrees.Select(t => t.GetMethodSymbol(methodName)).FirstOrDefault(m => m != null);

        public INamedTypeSymbol? GetSymbol(string symbolName)
        {
            return (INamedTypeSymbol)Compilation
                .GetTypeByMetadataName(symbolName)!;
        }

        /// <summary>
        /// Returns a MetadataReference for this compilation, so it can be used as a dependency in another compilation.
        /// </summary>
        public MetadataReference ToMetadataReference() => Compilation.ToMetadataReference();
    }

    public record TestTree(SyntaxTree Tree, SemanticModel Model)
    {
        public IMethodSymbol? GetMethodSymbol(string methodName)
        {
            return Tree.GetRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Select(m => (IMethodSymbol)Model.GetDeclaredSymbol(m)!)
                .FirstOrDefault(m => m.Name == methodName);
        }
    }

    public record TestCompiler
    {
        //public readonly HashSet<string> BaseUsings = ["System", "System.Threading", "System.Threading.Tasks"];
        public readonly HashSet<string> InterceptorNamespaces = [];
        public readonly List<IIncrementalGenerator> IncrementalGenerators = [];
        public readonly List<MetadataReference> AdditionalReferences = [];

        public LanguageVersion LanguageVersion { get; set; } = LanguageVersion.CSharp12;

        public CSharpParseOptions CreateOptions()
        {
            var res = new CSharpParseOptions(LanguageVersion);
            if (InterceptorNamespaces.Count > 0)
            {
                res = res.WithFeatures([new("InterceptorsNamespaces", string.Join(';', InterceptorNamespaces))]);
            }
            return res;
        }

        public string AssemblyName { get; set; } = "Tests";

        /// <summary>
        /// make sure to force type references to the needed types are loaded before calling
        /// </summary>
        public TestCompilation Create(string source) => Create([source]);

        public TestCompilation Create(IEnumerable<string> sources)
        {
            var options = CreateOptions();
            Compilation compilation = CSharpCompilation.Create(
                assemblyName: AssemblyName,
                syntaxTrees: sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, options, path: $"test{index}.cs")),
                references: GetReferences().Concat(AdditionalReferences),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            List<Diagnostic> diagnostics = [];
            List<GeneratorDriverRunResult> generatorResults = [];
     



            foreach (var generator in IncrementalGenerators)
            {
                var driver = CSharpGeneratorDriver
                    .Create([generator.AsSourceGenerator()])
                    .WithUpdatedParseOptions(options);

                driver = driver.RunGeneratorsAndUpdateCompilation(
                    compilation,
                    out var newCompilation,
                    out var newdiagnostics);

                diagnostics.AddRange(newdiagnostics);
                compilation = newCompilation;

                generatorResults.Add( driver.GetRunResult());
            }

            

            diagnostics.AddRange(compilation.GetDiagnostics());
            if (CheckErrors)
                foreach (var d in diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error))
                    throw new Exception("Could not create compilation: " + d.ToString());

            return new(compilation, this, options, diagnostics, generatorResults);
        }

        public bool CheckErrors { get; set; } = true;

        /// <summary>
        /// make sure to force type references to the needed types are loaded before calling
        /// </summary>
        /// <returns></returns>
        public IEnumerable<MetadataReference> GetReferences()
        {

            // Force assemblies to load
            Type[] _ = [typeof(IServiceProvider), typeof(Action),  typeof(System.Text.Json.JsonSerializer),
                    typeof(Task),typeof(HttpClient),typeof(System.Text.Json.JsonSerializer),typeof(ModuleInitializerAttribute),typeof(Uri)];

            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(static a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)
                && !a.FullName!.Contains("Generator"))
                .Select(static a => MetadataReference.CreateFromFile(a.Location));
        }
    }
}
