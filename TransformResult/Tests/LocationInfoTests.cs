using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Subro.Generators.Tests;

/// <summary><see cref="LocationInfo"/>: a location kept without the syntax tree, and back.</summary>
public class LocationInfoTests
{
    const string Source = """
        class A
        {
            void M(B b)
            {
                b.Call(1);
                Local(2);
                b.Generic<int>(3);
            }

            void Local(int i) { }
        }

        class B
        {
            public void Call(int i) { }
            public void Generic<T>(T value) { }
        }
        """;

    static SyntaxTree Tree() => CSharpSyntaxTree.ParseText(Source, path: "source.cs");

    static InvocationExpressionSyntax Invocation(SyntaxTree tree, string text)
        => tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single(i => i.ToString().StartsWith(text));

    [Test]
    public async Task None_IsTheDefault_AndNoLocation()
    {
        await Assert.That(new LocationInfo()).IsEqualTo(LocationInfo.None);
        await Assert.That(default(LocationInfo)).IsEqualTo(LocationInfo.None);
        await Assert.That((LocationInfo)Location.None).IsEqualTo(LocationInfo.None);
        await Assert.That(LocationInfo.None.ToLocation()).IsEqualTo(Location.None);
    }

    [Test]
    public async Task ALocation_RoundTrips()
    {
        var tree = Tree();
        var location = Invocation(tree, "b.Call").GetLocation();

        LocationInfo info = location;
        var back = info.ToLocation();

        await Assert.That(info.FilePath).IsEqualTo("source.cs");
        await Assert.That(back.SourceSpan).IsEqualTo(location.SourceSpan);
        await Assert.That(back.GetLineSpan()).IsEqualTo(location.GetLineSpan());
    }

    [Test]
    public async Task EqualLocations_AreEqualInfos()
    {
        await Assert.That((LocationInfo)Invocation(Tree(), "b.Call").GetLocation())
            .IsEqualTo((LocationInfo)Invocation(Tree(), "b.Call").GetLocation());
    }

    [Test]
    public async Task FromASymbol_ItsFirstLocation_OrNone()
    {
        var compilation = CSharpCompilation.Create("t",
            [CSharpSyntaxTree.ParseText("partial class A { }", path: "a1.cs"), CSharpSyntaxTree.ParseText("partial class A { }", path: "a2.cs")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var symbol = compilation.GetTypeByMetadataName("A")!;
        var nowhere = compilation.CreateArrayTypeSymbol(symbol);

        await Assert.That(LocationInfo.From(symbol).FilePath).IsEqualTo("a1.cs");
        await Assert.That(new LocationInfo(symbol)).IsEqualTo(LocationInfo.From(symbol));
        await Assert.That(LocationInfo.FromAll(symbol).Select(static l => l.FilePath ?? "")).IsEquivalentTo(["a1.cs", "a2.cs"]);
        await Assert.That(LocationInfo.FromAll(nowhere)).IsEquivalentTo([LocationInfo.None]);
        await Assert.That(LocationInfo.From(nowhere)).IsEqualTo(LocationInfo.None);
    }

    [Test]
    public async Task FromAnAttribute_WhereItIsApplied()
    {
        var tree = CSharpSyntaxTree.ParseText("[System.Obsolete] class A { }", path: "a.cs");
        var compilation = CSharpCompilation.Create("t", [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var attribute = compilation.GetTypeByMetadataName("A")!.GetAttributes().Single();

        var info = LocationInfo.From(attribute);

        await Assert.That(tree.GetText().ToString(info.TextSpan)).IsEqualTo("System.Obsolete");
    }

    [Test]
    [Arguments("b.Call", "Call")]
    [Arguments("Local", "Local")]
    [Arguments("b.Generic", "Generic<int>")]
    public async Task FromCaller_TheNameOfTheMethodCalled(string call, string expected)
    {
        var tree = Tree();

        var info = LocationInfo.FromCaller(Invocation(tree, call));

        await Assert.That(tree.GetText().ToString(info.TextSpan)).IsEqualTo(expected);
    }
}
