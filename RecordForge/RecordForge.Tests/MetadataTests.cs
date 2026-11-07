using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Subro.RecordForge;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Subro.Generators.Tests
{
    /// <summary>
    /// What a generated type says about itself: <c>Fields</c> with the interface's attributes, the projection and the copy,
    /// <c>IForgedRecord&lt;TSelf&gt;</c>, and records deriving from the records of their base interfaces.
    /// </summary>
    public class MetadataTests
    {
        static TestCompiler GetTestCompilation(string assemblyName = "Tests")
        {
            var res = new TestCompiler { AssemblyName = assemblyName };
            res.IncrementalGenerators.Add(new RecordForger());
            res.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(GenerateRecordAttribute).Assembly.Location));
            res.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(Expression).Assembly.Location));
            return res;
        }

        const string attributes = """
            using System;
            using Subro.RecordForge;
            namespace Foo
            {
                public enum Kind { A, B = 5 }

                [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
                public sealed class MarkAttribute : Attribute
                {
                    public MarkAttribute(string text, int number, Kind kind, Type type, params string[] tags)
                    { Text = text; Number = number; Kind = kind; Type = type; Tags = tags; }
                    public MarkAttribute() : this("", 0, Kind.A, typeof(object)) { }
                    public MarkAttribute(string text) : this(text, 0, Kind.A, typeof(object)) { }
                    public string Text { get; }
                    public int Number { get; }
                    public Kind Kind { get; }
                    public Type Type { get; }
                    public string[] Tags { get; }
                    public double Weight { get; set; }
                    public string? Note { get; set; } = "unset";
                }
            }
            """;

        const string clubCode = """
            #nullable enable
            using System;
            using Subro.RecordForge;
            namespace Foo
            {
                [Mark("group")]
                [GenerateRecord]
                public interface IClubCode
                {
                    [Mark("code", -1, Kind.B, typeof(System.Collections.Generic.List<int>), "a", "b", Weight = 1.5, Note = null)]
                    string Code { get; }

                    [System.Diagnostics.CodeAnalysis.MaybeNull, Mark]
                    string? Name { get; set; }

                    int? Rank { get; init; }
                }

                public class Club : IClubCode
                {
                    public string Code { get; set; } = "";
                    public string? Name { get; set; }
                    public int? Rank { get; init; }
                    public string? City { get; set; }
                }
            }
            """;

        /// <summary>Compiles, emits and loads the sources, each list one assembly referencing the ones before it.</summary>
        static Assembly[] Load(params string[][] assemblies)
        {
            var context = new AssemblyLoadContext(null, isCollectible: true);
            var references = new List<MetadataReference>();
            var loaded = new List<Assembly>();

            for (int i = 0; i < assemblies.Length; i++)
            {
                var compiler = GetTestCompilation("Assembly" + i);
                compiler.AdditionalReferences.AddRange(references);
                var compilation = compiler.Create(assemblies[i]).Compilation;

                using var stream = new MemoryStream();
                var emitted = compilation.Emit(stream);
                if (!emitted.Success)
                    throw new Exception(string.Join("\n", emitted.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)));

                references.Add(compilation.ToMetadataReference());
                stream.Position = 0;
                loaded.Add(context.LoadFromStream(stream));
            }

            return [.. loaded];
        }

        static object? Get(object target, string property) => target.GetType().GetProperty(property)!.GetValue(target);

        static object? GetStatic(Type type, string property) =>
            type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)!.GetValue(null);

        static IReadOnlyList<RecordField> FieldsOf(Type type) => (IReadOnlyList<RecordField>)GetStatic(type, "Fields")!;

        static object NewClub(Assembly assembly, string code, string? name, int? rank)
        {
            var club = Activator.CreateInstance(assembly.GetType("Foo.Club")!)!;
            club.GetType().GetProperty("Code")!.SetValue(club, code);
            club.GetType().GetProperty("Name")!.SetValue(club, name);
            club.GetType().GetProperty("Rank")!.SetValue(club, rank);
            return club;
        }

        static IReadOnlyList<RecordField> ForgedFields<T>() where T : IForgedRecord<T> => T.Fields;

        static Type ForgedInterface<T>() where T : IForgedRecord<T> => T.Interface;

        static LambdaExpression? ForgedProjection<T>() where T : IForgedRecord<T> => T.Projection;

        static object? CallForged(string method, Type type) =>
            typeof(MetadataTests).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type).Invoke(null, null);

        [Test]
        public async Task FieldsDescribeTheProperties()
        {
            var assembly = Load([attributes, clubCode])[0];
            var fields = FieldsOf(assembly.GetType("Foo.ClubCode")!);
            var club = assembly.GetType("Foo.IClubCode")!;

            await Assert.That(fields.Select(static f => f.Name).ToArray()).IsEquivalentTo(new[] { "Code", "Name", "Rank" });

            await Assert.That(fields[0].Type).IsEqualTo(typeof(string));
            await Assert.That(fields[0].AllowsNull).IsFalse();
            await Assert.That(fields[0].HasSetter).IsFalse();
            await Assert.That(fields[0].DeclaringInterface).IsEqualTo(club);

            await Assert.That(fields[1].AllowsNull).IsTrue();
            await Assert.That(fields[1].HasSetter).IsTrue();

            await Assert.That(fields[2].Type).IsEqualTo(typeof(int?));
            await Assert.That(fields[2].AllowsNull).IsTrue();
            await Assert.That(fields[2].HasSetter).IsTrue();
        }

        [Test]
        public async Task AttributesArePassedOnAsInstances()
        {
            var assembly = Load([attributes, clubCode])[0];
            var fields = FieldsOf(assembly.GetType("Foo.ClubCode")!);

            var mark = fields[0].Attributes.Single();
            await Assert.That(Get(mark, "Text")).IsEqualTo("code");
            await Assert.That(Get(mark, "Number")).IsEqualTo(-1);
            await Assert.That(Get(mark, "Kind")!.ToString()).IsEqualTo("B");
            await Assert.That(Get(mark, "Type")).IsEqualTo(typeof(List<int>));
            await Assert.That((string[])Get(mark, "Tags")!).IsEquivalentTo(new[] { "a", "b" });
            await Assert.That(Get(mark, "Weight")).IsEqualTo(1.5);
            await Assert.That(Get(mark, "Note")).IsNull();

            //[MaybeNull] is the compiler's, and left out
            await Assert.That(fields[1].Attributes.Count).IsEqualTo(1);
            await Assert.That(Get(fields[1].Attributes[0], "Text")).IsEqualTo("");
            await Assert.That(fields[2].Attributes.Count).IsEqualTo(0);

            //the interface's own, without [GenerateRecord]
            var group = fields[0].InterfaceAttributes.Single();
            await Assert.That(Get(group, "Text")).IsEqualTo("group");
            await Assert.That(fields[1].InterfaceAttributes).IsSameReferenceAs(fields[0].InterfaceAttributes);
        }

        [Test]
        public async Task FieldsReadValues()
        {
            var assembly = Load([attributes, clubCode])[0];
            var fields = FieldsOf(assembly.GetType("Foo.ClubCode")!);
            var club = NewClub(assembly, "ROTT01", "Janssen", 3);

            await Assert.That(fields[0].GetValue(club)).IsEqualTo("ROTT01");
            await Assert.That(fields[2].GetValue(club)).IsEqualTo(3);
            await Assert.That(fields[1].Expression.Parameters[0].Type).IsEqualTo(assembly.GetType("Foo.IClubCode"));
        }

        [Test]
        public async Task ProjectionMakesTheRecord()
        {
            var assembly = Load([attributes, clubCode])[0];
            var record = assembly.GetType("Foo.ClubCode")!;
            var clubType = assembly.GetType("Foo.Club")!;
            var club = NewClub(assembly, "ROTT01", "Janssen", 3);

            var projection = (LambdaExpression)GetStatic(record, "Projection")!;
            var fromInterface = projection.Compile().DynamicInvoke(club)!;
            await Assert.That(Get(fromInterface, "Code")).IsEqualTo("ROTT01");
            await Assert.That(Get(fromInterface, "Name")).IsEqualTo("Janssen");
            await Assert.That(Get(fromInterface, "Rank")).IsEqualTo(3);

            //made to start from the entity's own type, as a query on it needs
            var fromEntity = RecordProjection.From(projection, clubType);
            await Assert.That(fromEntity.Parameters[0].Type).IsEqualTo(clubType);
            await Assert.That(fromEntity.Compile().DynamicInvoke(club)).IsEqualTo(fromInterface);
            await Assert.That(RecordProjection.From(projection, clubType)).IsSameReferenceAs(fromEntity);

            var generic = (LambdaExpression)record.GetMethod("ProjectionFrom")!.MakeGenericMethod(clubType).Invoke(null, null)!;
            await Assert.That(generic.Compile().DynamicInvoke(club)).IsEqualTo(fromInterface);

            var copy = record.GetMethod("From")!.Invoke(null, [club]);
            await Assert.That(copy).IsEqualTo(fromInterface);
        }

        [Test]
        public async Task ForgedRecordInterfaceIsImplemented()
        {
            var assembly = Load([attributes, clubCode])[0];
            var record = assembly.GetType("Foo.ClubCode")!;

            await Assert.That(record.GetInterfaces()).Contains(typeof(IForgedRecord<>).MakeGenericType(record));
            await Assert.That(CallForged(nameof(ForgedInterface), record)).IsEqualTo(assembly.GetType("Foo.IClubCode"));
            await Assert.That(((IReadOnlyList<RecordField>)CallForged(nameof(ForgedFields), record)!).Count).IsEqualTo(3);
            await Assert.That(CallForged(nameof(ForgedProjection), record)).IsSameReferenceAs(GetStatic(record, "Projection"));
        }

        const string shapes = """
            #nullable enable
            using System;
            using Subro.RecordForge;
            namespace Foo
            {
                public interface IKeyed { Guid Id { get; } }

                [GenerateRecord]
                public interface IClubCode : IKeyed
                {
                    [Mark("code")] string Code { get; }
                    string? Name { get; set; }
                }

                [GenerateRecord]
                public interface IClubInfo : IClubCode
                {
                    [Mark("city")] string? City { get; }
                    string? Website { get; set; }
                }

                public class Club : IClubInfo
                {
                    public Guid Id { get; set; }
                    public string Code { get; set; } = "";
                    public string? Name { get; set; }
                    public string? City { get; set; }
                    public string? Website { get; set; }
                }
            }
            """;

        [Test]
        public async Task RecordDerivesFromTheRecordOfItsBaseInterface()
        {
            var assembly = Load([attributes, shapes])[0];
            var code = assembly.GetType("Foo.ClubCode")!;
            var info = assembly.GetType("Foo.ClubInfo")!;

            await Assert.That(info.BaseType).IsEqualTo(code);

            //an inherited interface without a record of its own is part of the record
            await Assert.That(FieldsOf(code).Select(static f => f.Name).ToArray()).IsEquivalentTo(new[] { "Id", "Code", "Name" });
            await Assert.That(FieldsOf(info).Select(static f => f.Name).ToArray()).IsEquivalentTo(new[] { "Id", "Code", "Name", "City", "Website" });
            await Assert.That(FieldsOf(info)[0].DeclaringInterface).IsEqualTo(assembly.GetType("Foo.IKeyed"));
            await Assert.That(Get(FieldsOf(info)[3].Attributes.Single(), "Text")).IsEqualTo("city");

            var club = Activator.CreateInstance(assembly.GetType("Foo.Club")!)!;
            var id = Guid.NewGuid();
            foreach (var (property, value) in new (string, object?)[] { ("Id", id), ("Code", "ROTT01"), ("Name", "Janssen"), ("City", "Rotterdam"), ("Website", "j.nl") })
                club.GetType().GetProperty(property)!.SetValue(club, value);

            var made = ((LambdaExpression)GetStatic(info, "Projection")!).Compile().DynamicInvoke(club)!;
            await Assert.That(made.GetType()).IsEqualTo(info);
            await Assert.That(Get(made, "Id")).IsEqualTo(id);
            await Assert.That(Get(made, "Code")).IsEqualTo("ROTT01");
            await Assert.That(Get(made, "Name")).IsEqualTo("Janssen");
            await Assert.That(Get(made, "City")).IsEqualTo("Rotterdam");
            await Assert.That(Get(made, "Website")).IsEqualTo("j.nl");

            //the base record's projection still makes the base record from the same source
            var asCode = ((LambdaExpression)GetStatic(code, "Projection")!).Compile().DynamicInvoke(club)!;
            await Assert.That(asCode.GetType()).IsEqualTo(code);
            await Assert.That(CallForged(nameof(ForgedInterface), info)).IsEqualTo(assembly.GetType("Foo.IClubInfo"));
        }

        [Test]
        public async Task RecordDerivesFromABaseRecordInAnotherAssembly()
        {
            const string baseAssembly = """
                using System;
                using Subro.RecordForge;
                namespace Foo
                {
                    [GenerateRecord]
                    public interface IClubCode { string Code { get; } string? Name { get; set; } }
                }
                """;
            const string derivedAssembly = """
                using System;
                using Subro.RecordForge;
                namespace Bar
                {
                    [GenerateRecord]
                    public interface IClubInfo : Foo.IClubCode { string? City { get; } }

                    public static class Use
                    {
                        public static ClubInfo Make() => new ClubInfo("ROTT01", "Rotterdam") { Name = "Janssen" };
                    }
                }
                """;

            var loaded = Load([baseAssembly], [derivedAssembly]);
            var info = loaded[1].GetType("Bar.ClubInfo")!;

            await Assert.That(info.BaseType).IsEqualTo(loaded[0].GetType("Foo.ClubCode"));
            var made = loaded[1].GetType("Bar.Use")!.GetMethod("Make")!.Invoke(null, null)!;
            await Assert.That(Get(made, "Code")).IsEqualTo("ROTT01");
            await Assert.That(Get(made, "Name")).IsEqualTo("Janssen");
            await Assert.That(Get(made, "City")).IsEqualTo("Rotterdam");
        }

        [Test]
        public async Task MetadataCanBeTurnedOff()
        {
            var assembly = Load(["""
                using Subro.RecordForge;
                namespace Foo { [GenerateRecord(Metadata = false)] public interface IPlain { int Value { get; } } }
                """])[0];
            var record = assembly.GetType("Foo.Plain")!;

            await Assert.That(record.GetProperty("Fields", BindingFlags.Public | BindingFlags.Static)).IsNull();
            await Assert.That(record.GetInterfaces().Any(static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IForgedRecord<>))).IsFalse();
        }

        [Test]
        public async Task APropertyNamedLikeTheMetadataLeavesItOut()
        {
            var compilation = GetTestCompilation().Create("""
                using Subro.RecordForge;
                namespace Foo { [GenerateRecord] public interface IReport { string[] Fields { get; } } }
                """);

            await Assert.That(compilation.Diagnostics.Any(static d => d.Id == "AIRMETANAME")).IsTrue();
            await Assert.That(compilation.GetSymbol("Foo.Report")!.GetMembers("Fields").Length).IsEqualTo(1);
        }

        [Test]
        public async Task WithoutStaticAbstractMembersTheInterfaceIsLeftOut()
        {
            var compiler = GetTestCompilation();
            compiler.LanguageVersion = LanguageVersion.CSharp10;
            var compilation = compiler.Create("""
                using Subro.RecordForge;
                namespace Foo { [GenerateRecord] public interface IPlain { int Value { get; } } }
                """);
            var record = compilation.GetSymbol("Foo.Plain")!;

            await Assert.That(record.GetMembers("Fields").Length).IsEqualTo(1);
            await Assert.That(record.AllInterfaces.Any(static i => i.Name == "IForgedRecord")).IsFalse();
        }

        [Test]
        public async Task AnAbstractRecordHasNoProjection()
        {
            var assembly = Load(["""
                using Subro.RecordForge;
                namespace Foo { [GenerateRecord(AsAbstract = true)] public interface IShape { int Sides { get; } } }
                """])[0];
            var record = assembly.GetType("Foo.Shape")!;

            await Assert.That(FieldsOf(record).Count).IsEqualTo(1);
            await Assert.That(record.GetProperty("Projection", BindingFlags.Public | BindingFlags.Static)).IsNull();
            await Assert.That(CallForged(nameof(ForgedProjection), record)).IsNull();
        }

        [Test]
        public async Task SeveralConstructorsChainToThePrimaryOne()
        {
            var assembly = Load(["""
                using Subro.RecordForge;
                namespace Foo
                {
                    [GenerateRecord(ConstructorUsage = ConstructorUsage.ReadonlyProperties | ConstructorUsage.AllProperties)]
                    public interface IPair { int Left { get; } int Right { get; set; } }
                }
                """])[0];
            var record = assembly.GetType("Foo.Pair")!;

            var both = Activator.CreateInstance(record, 1, 2)!;
            await Assert.That(Get(both, "Left")).IsEqualTo(1);
            await Assert.That(Get(both, "Right")).IsEqualTo(2);
            await Assert.That(Get(Activator.CreateInstance(record, 3)!, "Left")).IsEqualTo(3);
        }
    }
}
