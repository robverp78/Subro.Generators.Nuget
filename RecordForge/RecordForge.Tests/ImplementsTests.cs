using Microsoft.CodeAnalysis;
using Subro.RecordForge;

using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Subro.Generators.Tests
{
    /// <summary>
    /// Tests for <see cref="InterfaceImplementer"/>: an existing partial type is completed against an
    /// interface, and whatever the type already has is left alone.
    /// </summary>
    public class TestImplementedInterfaces
    {
        static TestCompiler GetTestCompilation(bool checkErrors = true)
        {
            var res = new TestCompiler { CheckErrors = checkErrors };
            res.IncrementalGenerators.Add(new InterfaceImplementer());
            res.AdditionalReferences.Add(
                MetadataReference.CreateFromFile(typeof(ImplementsAttribute).Assembly.Location));
            return res;
        }

        const string auditInterface =
            "public interface IAudited { DateTime CreatedAt { get; init; } string? CreatedBy { get; set; } int Version { get; } }";

        static string InNamespace(string body)
            => $"using Subro.RecordForge; using System; namespace Foo {{ {auditInterface} {body} }}";

        static IPropertySymbol? GetProperty(TestCompilation comp, string typeName, string propertyName)
            => comp.GetSymbol(typeName)?.GetMembers(propertyName).OfType<IPropertySymbol>().FirstOrDefault();

        [Test]
        public async Task MissingPropertiesAreGenerated()
        {
            var comp = GetTestCompilation().Create(InNamespace(
                "[Implements(typeof(IAudited))] public partial class Session { }"));

            await Assert.That(GetProperty(comp, "Foo.Session", "CreatedAt")).IsNotNull();
            await Assert.That(GetProperty(comp, "Foo.Session", "CreatedBy")).IsNotNull();
            await Assert.That(GetProperty(comp, "Foo.Session", "Version")).IsNotNull();
        }

        [Test]
        public async Task InterfaceIsAddedToTheBaseList()
        {
            var comp = GetTestCompilation().Create(InNamespace(
                "[Implements(typeof(IAudited))] public partial class Session { }"));

            var session = comp.GetSymbol("Foo.Session");
            await Assert.That(session!.AllInterfaces.Any(static i => i.Name == "IAudited")).IsTrue();
        }

        [Test]
        public async Task InterfaceIsNotAddedWhenDeclareInterfaceIsFalse()
        {
            var comp = GetTestCompilation().Create(InNamespace(
                "[Implements(typeof(IAudited), DeclareInterface = false)] public partial class Session { }"));

            var session = comp.GetSymbol("Foo.Session");
            await Assert.That(session!.AllInterfaces.Any(static i => i.Name == "IAudited")).IsFalse();
            await Assert.That(GetProperty(comp, "Foo.Session", "CreatedAt")).IsNotNull();
        }

        [Test]
        public async Task ExistingMemberIsLeftAlone()
        {
            //the hand-written CreatedBy has a body; if it were generated as well the compilation would fail
            //with a duplicate member, so a clean compile plus a non-auto property proves it was skipped
            var comp = GetTestCompilation().Create(InNamespace(@"
                [Implements(typeof(IAudited))] public partial class Session
                {
                    private string? createdBy;
                    public string? CreatedBy { get => createdBy; set => createdBy = value; }
                }"));

            var prop = GetProperty(comp, "Foo.Session", "CreatedBy");
            await Assert.That(prop).IsNotNull();
            await Assert.That(prop!.GetMethod!.IsImplicitlyDeclared).IsFalse();
            await Assert.That(GetProperty(comp, "Foo.Session", "CreatedAt")).IsNotNull();
        }

        [Test]
        public async Task InheritedMemberIsLeftAlone()
        {
            var comp = GetTestCompilation().Create(InNamespace(@"
                public class EntityBase { public DateTime CreatedAt { get; init; } }
                [Implements(typeof(IAudited))] public partial class Session : EntityBase { }"));

            var session = comp.GetSymbol("Foo.Session");
            //declared on the base, so nothing of that name should be added to Session itself
            await Assert.That(session!.GetMembers("CreatedAt").Length).IsEqualTo(0);
            await Assert.That(GetProperty(comp, "Foo.Session", "CreatedBy")).IsNotNull();
        }

        [Test]
        public async Task AccessorsFollowTheInterface()
        {
            var comp = GetTestCompilation().Create(InNamespace(
                "[Implements(typeof(IAudited))] public partial class Session { }"));

            await Assert.That(GetProperty(comp, "Foo.Session", "CreatedAt")!.SetMethod!.IsInitOnly).IsTrue();
            await Assert.That(GetProperty(comp, "Foo.Session", "CreatedBy")!.SetMethod!.IsInitOnly).IsFalse();
            await Assert.That(GetProperty(comp, "Foo.Session", "Version")!.SetMethod).IsNull();
        }

        [Test]
        public async Task AlwaysCreateSettersAddsASetterToGetOnlyMembers()
        {
            var comp = GetTestCompilation().Create(InNamespace(
                "[Implements(typeof(IAudited), AlwaysCreateSetters = true)] public partial class Session { }"));

            await Assert.That(GetProperty(comp, "Foo.Session", "Version")!.SetMethod).IsNotNull();
        }

        [Test]
        public async Task InheritedInterfaceMembersAreGenerated()
        {
            var comp = GetTestCompilation().Create(InNamespace(@"
                public interface ITracked : IAudited { Guid TrackingId { get; } }
                [Implements(typeof(ITracked))] public partial class Session { }"));

            await Assert.That(GetProperty(comp, "Foo.Session", "TrackingId")).IsNotNull();
            await Assert.That(GetProperty(comp, "Foo.Session", "CreatedAt")).IsNotNull();
        }

        [Test]
        public async Task DefaultInterfaceImplementationsAreNotGenerated()
        {
            var comp = GetTestCompilation().Create(InNamespace(@"
                public interface IDefaulted { int Answer => 42; string Name { get; } }
                [Implements(typeof(IDefaulted))] public partial class Session { }"));

            var session = comp.GetSymbol("Foo.Session");
            await Assert.That(session!.GetMembers("Answer").Length).IsEqualTo(0);
            await Assert.That(GetProperty(comp, "Foo.Session", "Name")).IsNotNull();
        }

        [Test]
        public async Task TwoInterfacesSharingAMemberGenerateItOnce()
        {
            //without per-type de-duplication this produces a duplicate member and the compilation fails
            var comp = GetTestCompilation().Create(InNamespace(@"
                public interface IAlsoAudited { DateTime CreatedAt { get; init; } }
                [Implements(typeof(IAudited))]
                [Implements(typeof(IAlsoAudited))]
                public partial class Session { }"));

            await Assert.That(comp.GetSymbol("Foo.Session")!.GetMembers("CreatedAt").Length).IsEqualTo(1);
        }

        [Test]
        public async Task NestedAndGenericTypesAreSupported()
        {
            var comp = GetTestCompilation().Create(InNamespace(@"
                public partial class Outer<TKey> where TKey : notnull
                {
                    [Implements(typeof(IAudited))] public partial record Inner<TValue> { }
                }"));

            await Assert.That(GetProperty(comp, "Foo.Outer`1+Inner`1", "CreatedAt")).IsNotNull();
        }

        [Test]
        public async Task ReadonlyStructGetsInitSettersOnly()
        {
            var comp = GetTestCompilation().Create(InNamespace(
                "[Implements(typeof(IAudited))] public readonly partial struct Stamp { }"));

            //a readonly struct cannot carry a set accessor, so the interface's setter becomes an init
            await Assert.That(GetProperty(comp, "Foo.Stamp", "CreatedBy")!.SetMethod!.IsInitOnly).IsTrue();
        }

        [Test]
        public async Task NonPartialTypeReportsAnError()
        {
            var comp = GetTestCompilation(checkErrors: false).Create(InNamespace(
                "[Implements(typeof(IAudited))] public class Session { }"));

            await Assert.That(comp.Diagnostics.Any(static d => d.Id == "AIRIMPPART")).IsTrue();
        }

        [Test]
        public async Task NonInterfaceTypeReportsAnError()
        {
            var comp = GetTestCompilation(checkErrors: false).Create(InNamespace(
                "public class NotAnInterface { } [Implements(typeof(NotAnInterface))] public partial class Session { }"));

            await Assert.That(comp.Diagnostics.Any(static d => d.Id == "AIRIMPIFACE")).IsTrue();
        }

        [Test]
        public async Task MismatchedExistingMemberReportsAWarning()
        {
            var comp = GetTestCompilation(checkErrors: false).Create(InNamespace(@"
                [Implements(typeof(IAudited))] public partial class Session
                {
                    public int CreatedBy { get; set; }
                }"));

            await Assert.That(comp.Diagnostics.Any(static d => d.Id == "AIRIMPMISMATCH")).IsTrue();
        }

        [Test]
        public void GeneratedTypeIsUsable()
        {
            // Passes if the compilation does not throw.
            _ = GetTestCompilation().Create(InNamespace(@"
                [Implements(typeof(IAudited))] public partial class Session { }
                public class User { public void Use() { IAudited a = new Session { CreatedAt = DateTime.Now }; _ = a.Version; } }"));
        }
    }
}
