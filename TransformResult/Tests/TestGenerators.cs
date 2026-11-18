using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Subro.Generators.Tests;

/// <summary>
/// Small generators over the package's providers, all judging a name the same way (<see cref="Judge"/>), and a way to
/// run one.
/// </summary>
static class TestGenerators
{
    public static readonly DiagnosticDescriptorInfo Bad = Diagnostics.Error("TRG01", "'{0}' is bad");
    public static readonly DiagnosticDescriptorInfo Odd = Diagnostics.Warning("TRG02", "'{0}' is odd");

    /// <summary>
    /// <c>Skip…</c> is left out with <c>return default;</c>, <c>Bad…</c> is an error without a result, <c>Odd…</c> a result
    /// with a warning; anything else is just a result.
    /// </summary>
    public static TransformResult<string> Judge(SyntaxToken identifier)
    {
        var name = identifier.Text;
        if (name.StartsWith("Skip", StringComparison.Ordinal))
            return default;
        if (name.StartsWith("Bad", StringComparison.Ordinal))
            return Bad.CreateDiagnosticInfo(identifier.GetLocation(), name);
        if (name.StartsWith("Odd", StringComparison.Ordinal))
            return new(name, [Odd.CreateDiagnosticInfo(identifier.GetLocation(), name)]);
        return name;
    }

    public static IEnumerable<TransformResult<string>> JudgeMethods(TypeDeclarationSyntax type)
        => type.Members.OfType<MethodDeclarationSyntax>().Select(static m => Judge(m.Identifier));

    public static void Emit(SourceProductionContext context, string name) => context.AddSource(name + ".g.cs", "// " + name);

    public const string Attribute = "GenAttribute";

    /// <summary>Classes of each kind, each with methods of each kind: for the generators that look at every class.</summary>
    public const string Classes = """
        class Plain { void Run() { } void SkipThis() { } void BadOne() { } void OddOne() { } }
        class SkipMe { }
        class BadClass { }
        class OddClass { }
        """;

    /// <summary>The same, marked for the generators that look for <see cref="Attribute"/>, and one class that is not.</summary>
    public const string AttributedClasses = """
        class GenAttribute : System.Attribute { }

        [Gen] class Plain { void Run() { } void SkipThis() { } void BadOne() { } void OddOne() { } }
        [Gen] class SkipMe { }
        [Gen] class BadClass { }
        [Gen] class OddClass { }
        class NotMarked { void Run2() { } }
        """;

    static readonly MetadataReference[] References = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

    public static CSharpCompilation Compilation(string source)
        => CSharpCompilation.Create("tests", [CSharpSyntaxTree.ParseText(source, path: "source.cs")], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    public static GeneratorDriver Driver(IIncrementalGenerator generator)
        => CSharpGeneratorDriver.Create([generator.AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    public static Generated Run(IIncrementalGenerator generator, string source)
        => Run(Driver(generator), Compilation(source), out _);

    public static Generated Run(GeneratorDriver driver, Compilation compilation, out GeneratorDriver ran)
    {
        ran = driver.RunGenerators(compilation);
        return new(ran.GetRunResult().Results.Single(), compilation.SyntaxTrees.First());
    }

    public sealed record Generated(GeneratorRunResult Run, SyntaxTree Source)
    {
        public string[] Names => [.. Run.GeneratedSources.Select(static s => s.HintName.Replace(".g.cs", "")).Order()];

        /// <summary>Each diagnostic as its id and the text it points at.</summary>
        public string[] Diagnostics => [.. Run.Diagnostics
            .Select(d => d.Id + " " + Source.GetText().ToString(d.Location.SourceSpan))
            .Order()];

        /// <summary>Why each output was produced on this run: new, modified, unchanged or cached.</summary>
        public IncrementalStepRunReason[] OutputReasons => [.. Run.TrackedOutputSteps
            .SelectMany(static s => s.Value)
            .SelectMany(static s => s.Outputs)
            .Select(static o => o.Reason)];
    }
}

[Generator]
sealed class SyntaxGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
        => context.RegisterSourceOutput(
            context.CreateValuesProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => TestGenerators.Judge(((ClassDeclarationSyntax)ctx.Node).Identifier)),
            TestGenerators.Emit);
}

[Generator]
sealed class SyntaxManyGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
        => context.RegisterSourceOutput(
            context.CreateValuesProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => TestGenerators.JudgeMethods((ClassDeclarationSyntax)ctx.Node)),
            TestGenerators.Emit);
}

[Generator]
sealed class AttributeGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
        => context.RegisterSourceOutput(
            context.ValuesForAttributeWithMetadataName(TestGenerators.Attribute,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => TestGenerators.Judge(((ClassDeclarationSyntax)ctx.TargetNode).Identifier)),
            TestGenerators.Emit);
}

[Generator]
sealed class AttributeManyGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
        => context.RegisterSourceOutput(
            context.ValuesForAttributeWithMetadataName(TestGenerators.Attribute,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => TestGenerators.JudgeMethods((ClassDeclarationSyntax)ctx.TargetNode)),
            TestGenerators.Emit);
}

/// <summary>The parts on their own: <c>WhereNotEmpty</c>, <c>RegisterDiagnosticsOutput</c> and <c>GetResults</c>.</summary>
[Generator]
sealed class SeparateStepsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => TestGenerators.Judge(((ClassDeclarationSyntax)ctx.Node).Identifier))
            .WhereNotEmpty();

        context.RegisterDiagnosticsOutput(results);
        context.RegisterSourceOutput(results.GetResults(), TestGenerators.Emit);
    }
}

/// <summary>
/// Metadata that is a struct: what is left out (<c>return default;</c>) or only an error must reach the generator as
/// little as it would for a class.
/// </summary>
[Generator]
sealed class StructGenerator : IIncrementalGenerator
{
    public readonly record struct NameInfo(string Name);

    static TransformResult<NameInfo> Judge(SyntaxToken identifier)
    {
        var judged = TestGenerators.Judge(identifier);
        if (judged.Result is { } name)
            return new(new NameInfo(name), judged.Diagnostics);
        return judged.Diagnostics;
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
        => context.RegisterSourceOutput(
            context.CreateValuesProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => Judge(((ClassDeclarationSyntax)ctx.Node).Identifier)),
            static (spc, info) => spc.AddSource((info.Name ?? "default") + ".g.cs", "// " + info.Name));
}
