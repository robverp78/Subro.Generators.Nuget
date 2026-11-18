using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Subro.Generators.Tests;

/// <summary><see cref="DiagnosticInfo"/>, <see cref="DiagnosticDescriptorInfo"/> and <see cref="DiagnosticsList"/>.</summary>
public class DiagnosticsTests
{
    static readonly DiagnosticDescriptorInfo Unknown = Diagnostics.Error("TR010", "'{0}' is unknown");

    static DiagnosticInfo Numbered(int number) => Diagnostics.Warning("TR1" + number.ToString("00"), "Number " + number)
        .CreateDiagnosticInfo(LocationInfo.None);

    [Test]
    public async Task DescriptorInfo_GivesTheSameDescriptor_EachTime()
    {
        var again = Diagnostics.Error("TR010", "'{0}' is unknown");

        await Assert.That(again.GetDescriptor()).IsSameReferenceAs(Unknown.GetDescriptor());
    }

    [Test]
    public async Task DescriptorInfo_FillsTitleAndCategory()
    {
        var descriptor = Unknown.GetDescriptor();
        var titled = (Unknown with { Title = "Unknown thing", Category = "Things" }).GetDescriptor();

        await Assert.That(descriptor.Id).IsEqualTo("TR010");
        await Assert.That(descriptor.Title.ToString()).IsEqualTo("TR010");
        await Assert.That(descriptor.Category).IsEqualTo("Generator Errors");
        await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
        await Assert.That(titled.Title.ToString()).IsEqualTo("Unknown thing");
        await Assert.That(titled.Category).IsEqualTo("Things");
    }

    [Test]
    public async Task Info_EqualByTheContentOfItsArguments()
    {
        var first = Unknown.CreateDiagnosticInfo(LocationInfo.None, "sport", 3);
        var second = Unknown.CreateDiagnosticInfo(LocationInfo.None, "sport", 3);
        var other = Unknown.CreateDiagnosticInfo(LocationInfo.None, "club", 3);

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(first.GetHashCode()).IsEqualTo(second.GetHashCode());
        await Assert.That(first).IsNotEqualTo(other);
    }

    [Test]
    public async Task Info_DiffersByLocation()
    {
        var tree = CSharpSyntaxTree.ParseText("class A { }", path: "a.cs");
        var located = Unknown.CreateDiagnosticInfo(tree.GetRoot().GetLocation(), "sport");
        var nowhere = Unknown.CreateDiagnosticInfo(LocationInfo.None, "sport");

        await Assert.That(located).IsNotEqualTo(nowhere);
        await Assert.That(located.NewLocation(LocationInfo.None)).IsEqualTo(nowhere);
    }

    /// <summary>A default info is the empty one; comparing it is no reason to throw.</summary>
    [Test]
    public async Task Default_Info_IsEmpty_AndEqualsItself()
    {
        DiagnosticInfo none = default;

        await Assert.That(none.IsEmpty).IsTrue();
        await Assert.That(none.Equals(default)).IsTrue();
        await Assert.That(none.Equals(Numbered(1))).IsFalse();
    }

    [Test]
    public async Task Info_ToDiagnostic_KeepsDescriptorLocationAndMessage()
    {
        var tree = CSharpSyntaxTree.ParseText("class A { }", path: "a.cs");
        var location = tree.GetRoot().GetLocation();

        var diagnostic = Unknown.CreateDiagnosticInfo(location, "sport").ToDiagnostic();

        await Assert.That(diagnostic.Id).IsEqualTo("TR010");
        await Assert.That(diagnostic.GetMessage()).IsEqualTo("'sport' is unknown");
        await Assert.That(diagnostic.Location.SourceSpan).IsEqualTo(location.SourceSpan);
        await Assert.That(diagnostic.Location.GetLineSpan().Path).IsEqualTo("a.cs");
    }

    [Test]
    public async Task ADescriptor_ConvertsToAnInfoWithoutLocation()
    {
        DiagnosticInfo info = Unknown.GetDescriptor();

        await Assert.That(info.Location).IsEqualTo(LocationInfo.None);
        await Assert.That(info.MessageArgs.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CreateInfos_OnePerLocation_OrOneWithout()
    {
        var compilation = CSharpCompilation.Create("t",
            [CSharpSyntaxTree.ParseText("partial class A { }", path: "a1.cs"), CSharpSyntaxTree.ParseText("partial class A { }", path: "a2.cs")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var symbol = compilation.GetTypeByMetadataName("A")!;

        var infos = Unknown.GetDescriptor().CreateInfos(symbol, "A");
        var nowhere = compilation.CreateArrayTypeSymbol(symbol);

        var withoutLocation = Unknown.GetDescriptor().CreateInfos(nowhere, "A[]");

        await Assert.That(infos.Select(static i => i.Location.FilePath ?? "")).IsEquivalentTo(["a1.cs", "a2.cs"]);
        await Assert.That(nowhere.Locations.Length).IsEqualTo(0);
        await Assert.That(withoutLocation.Select(static i => i.Location)).IsEquivalentTo([LocationInfo.None]);
    }

    [Test]
    public async Task List_Empty()
    {
        var list = new DiagnosticsList();

        await Assert.That(list.IsEmpty).IsTrue();
        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(list.All).IsEmpty();
        await Assert.That(list.GetDiagnosticInfos().IsDefault).IsFalse();
        await Assert.That(list.GetDiagnosticInfos().Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(5)]
    public async Task List_KeepsTheOrderAdded(int count)
    {
        var list = new DiagnosticsList();
        var added = Enumerable.Range(1, count).Select(Numbered).ToArray();
        list.AddRange(added);

        await Assert.That(list.Count).IsEqualTo(count);
        await Assert.That(list.All).IsEquivalentTo(added, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(list.GetDiagnosticInfos()).IsEquivalentTo(added, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(list.ToList()).IsEquivalentTo(added, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    public async Task List_SetMain_PutsItFirst(int before)
    {
        var list = new DiagnosticsList();
        var added = Enumerable.Range(1, before).Select(Numbered).ToArray();
        list.AddRange(added);
        var main = Numbered(99);

        list.SetMain(main);
        list.Add(Numbered(50));

        DiagnosticInfo[] expected = [main, .. added, Numbered(50)];
        await Assert.That(list.Count).IsEqualTo(expected.Length);
        await Assert.That(list.GetDiagnosticInfos()).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task List_AddRange_WithANewLocation()
    {
        var tree = CSharpSyntaxTree.ParseText("class A { }", path: "a.cs");
        LocationInfo location = tree.GetRoot().GetLocation();
        var list = new DiagnosticsList();

        list.AddRange([Numbered(1), Numbered(2)], location);

        await Assert.That(list.All.Select(static d => d.Location).Distinct()).IsEquivalentTo([location]);
    }

    [Test]
    public async Task List_Clear_EmptiesIt()
    {
        var list = new DiagnosticsList();
        list.AddRange([Numbered(1), Numbered(2)]);

        list.Clear();
        list.Add(Numbered(3));

        await Assert.That(list.GetDiagnosticInfos()).IsEquivalentTo([Numbered(3)]);
    }

    [Test]
    public async Task Container_AddsAnothersDiagnostics()
    {
        var inner = Transform.Build<string>();
        inner.Diagnostics.Add(Numbered(1));
        var empty = Transform.Build<string>();
        var outer = Transform.Build<string>();

        outer.Diagnostics.Add(inner);
        outer.Diagnostics.Add(empty);

        await Assert.That(outer.HasDiagnostics).IsTrue();
        await Assert.That(empty.HasDiagnostics).IsFalse();
        await Assert.That(outer.GetDiagnosticInfos()).IsEquivalentTo([Numbered(1)]);
    }
}
