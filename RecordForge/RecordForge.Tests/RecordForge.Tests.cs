using Subro.RecordForge;
using Subro.Generators;

using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

using static Subro.Generators.RecordForger;

namespace Subro.Generators.Tests
{
    public class TestAutoImplementedRecords
    {
        TestCompiler GetTestCompilation()
        {
            var res = new TestCompiler();
            res.IncrementalGenerators.Add(new RecordForger());
            res.AdditionalReferences.Add(
                Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                    typeof(GenerateRecordAttribute).Assembly.Location));
            return res;
        }


        const string testInterface =
        "interface IBar { int AnInt { get; } string AString{ get; set; } DateTime? SomeDate{get;init;} }";

        static string interfaceInNamespace(string Interface)
            => $"using Subro.RecordForge;using System; namespace Foo{{ {Interface} }}";

        public static IEnumerable<RecordKind> GetRecordKinds()
            => Enum.GetValues<RecordKind>();

        [Test]
        [MethodDataSource(nameof(GetRecordKinds))]
        public async Task TestRecordIsCreatedForKind(RecordKind kind)
        {
            var comp = GetTestCompilation().Create(interfaceInNamespace($"[GenerateRecord({nameof(RecordKind)}.{kind})]{testInterface}"));
            await Assert.That(comp.GetSymbol("Foo.Bar")).IsNotNull();
        }

        [Test]
        public void TestCreatedRecordIsUsable()
        {
            // Passes if the compilation does not throw.
            _ = GetTestCompilation().Create(interfaceInNamespace($"[GenerateRecord]{testInterface} class AClass{{public AClass(){{var rec = new Bar(1);}}}}"));
        }

        [Test]
        public async Task TestUseOtherNamespaceAndName()
        {
            var comp = GetTestCompilation().Create(interfaceInNamespace($"[GenerateRecord(NameSpace=\"Test\", RecordName=\"Bar2\")]{testInterface}"));
            await Assert.That(comp.GetSymbol("Test.Bar2")).IsNotNull();
        }

        [Test]
        public async Task TestAssemblyLevelDeclaration()
        {
            var code = @$"
using Subro.RecordForge;
using System; 

[assembly:GenerateRecordFromInterface(typeof(Foo.IBar))]
[assembly:GenerateRecordFromInterface(typeof(Foo.IBar),RecordName = ""Bar2"")]
namespace Foo{{ {testInterface} }}";
            var comp = GetTestCompilation().Create(code);
            await Assert.That(comp.GetSymbol("Foo.Bar")).IsNotNull();
            await Assert.That(comp.GetSymbol("Foo.Bar2")).IsNotNull();
        }

        [Test]
        public async Task TestAssemblyLevelDeclarationCrossAssembly()
        {
            // Compile the interface in a separate assembly
            var interfaceAssembly = new TestCompiler { AssemblyName = "InterfaceLib" }
                .Create($"using System; namespace Foo{{ public {testInterface} }}");

            // Compile the assembly-level attribute in a different assembly that references the interface assembly
            var compiler = GetTestCompilation();
            compiler.AdditionalReferences.Add(interfaceAssembly.ToMetadataReference());
            var comp = compiler.Create(@"
using Subro.RecordForge;
[assembly:GenerateRecordFromInterface(typeof(Foo.IBar))]
");
            await Assert.That(comp.GetSymbol("Foo.Bar")).IsNotNull();
        }

    }

}
