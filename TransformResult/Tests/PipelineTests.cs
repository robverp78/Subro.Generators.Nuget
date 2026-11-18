using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static Subro.Generators.Tests.TestGenerators;

namespace Subro.Generators.Tests;

/// <summary>
/// The providers in a real generator run: only results reach the generator, <c>default</c> is left out, diagnostics are
/// reported where they point, and a result's diagnostics do not cost it its caching.
/// </summary>
public class PipelineTests
{
    static readonly string[] ClassResults = ["OddClass", "Plain"];
    static readonly string[] ClassDiagnostics = ["TRG01 BadClass", "TRG02 OddClass"];
    static readonly string[] MethodResults = ["OddOne", "Run"];
    static readonly string[] MethodDiagnostics = ["TRG01 BadOne", "TRG02 OddOne"];

    static async Task AssertRun(Generated generated, string[] results, string[] diagnostics)
    {
        await Assert.That(generated.Run.Exception).IsNull();
        await Assert.That(generated.Names).IsEquivalentTo(results);
        await Assert.That(generated.Diagnostics).IsEquivalentTo(diagnostics);
    }

    [Test]
    public Task CreateValuesProvider_OneResultEach()
        => AssertRun(Run(new SyntaxGenerator(), Classes), ClassResults, ClassDiagnostics);

    [Test]
    public Task CreateValuesProvider_SeveralResultsEach()
        => AssertRun(Run(new SyntaxManyGenerator(), Classes), MethodResults, MethodDiagnostics);

    [Test]
    public Task ValuesForAttributeWithMetadataName_OneResultEach()
        => AssertRun(Run(new AttributeGenerator(), AttributedClasses), ClassResults, ClassDiagnostics);

    [Test]
    public Task ValuesForAttributeWithMetadataName_SeveralResultsEach()
        => AssertRun(Run(new AttributeManyGenerator(), AttributedClasses), MethodResults, MethodDiagnostics);

    [Test]
    public Task TheSeparateSteps_DoWhatTheFastTrackDoes()
        => AssertRun(Run(new SeparateStepsGenerator(), Classes), ClassResults, ClassDiagnostics);

    /// <summary>Metadata that is a struct: neither <c>default</c> nor an error reaches the generator as a result.</summary>
    [Test]
    public Task AStructResult_OnlyWhenThereIsOne()
        => AssertRun(Run(new StructGenerator(), Classes), ClassResults, ClassDiagnostics);

    /// <summary>
    /// A change elsewhere runs the transforms again; what they give is the same, so nothing is generated or reported anew,
    /// also for the result that came with a warning.
    /// </summary>
    [Test]
    [Arguments(nameof(SyntaxGenerator))]
    [Arguments(nameof(AttributeGenerator))]
    [Arguments(nameof(SyntaxManyGenerator))]
    [Arguments(nameof(AttributeManyGenerator))]
    public async Task AnUnrelatedChange_ChangesNoOutput(string generatorName)
    {
        IIncrementalGenerator generator = generatorName switch
        {
            nameof(SyntaxGenerator) => new SyntaxGenerator(),
            nameof(AttributeGenerator) => new AttributeGenerator(),
            nameof(SyntaxManyGenerator) => new SyntaxManyGenerator(),
            _ => new AttributeManyGenerator(),
        };
        var source = generatorName.StartsWith("Attribute", StringComparison.Ordinal) ? AttributedClasses : Classes;
        var compilation = Compilation(source);

        var first = Run(Driver(generator), compilation, out var driver);
        var changed = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("enum Unrelated { One }", path: "other.cs"));
        var second = Run(driver, changed, out _);

        await Assert.That(second.Names).IsEquivalentTo(first.Names);
        await Assert.That(second.Diagnostics).IsEquivalentTo(first.Diagnostics);
        await Assert.That(second.OutputReasons).IsNotEmpty();
        await Assert.That(second.OutputReasons.Where(static r => r is IncrementalStepRunReason.New or IncrementalStepRunReason.Modified))
            .IsEmpty();
    }
}
