using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Subro.Generators.Tests;

/// <summary><see cref="TransformResult{T}"/> on its own: what each way of making one holds.</summary>
public class TransformResultTests
{
    static readonly DiagnosticInfo Error = Diagnostics.Error("TR001", "Something is wrong").GetDescriptor();
    static readonly DiagnosticInfo Warning = Diagnostics.Warning("TR002", "Something is odd").GetDescriptor();

    [Test]
    public async Task Default_IsEmpty_WithAnEmptyArrayOfDiagnostics()
    {
        TransformResult<string> result = default;

        await Assert.That(result.IsEmpty).IsTrue();
        await Assert.That(result.HasDiagnostics).IsFalse();
        await Assert.That(result.Result).IsNull();
        await Assert.That(result.Diagnostics.IsDefault).IsFalse();
        await Assert.That(result.Diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AResult_HasNoDiagnostics()
    {
        TransformResult<string> result = "value";

        await Assert.That(result.Result).IsEqualTo("value");
        await Assert.That(result.IsEmpty).IsFalse();
        await Assert.That(result.HasDiagnostics).IsFalse();
        await Assert.That(new TransformResult<string>("value")).IsEqualTo(result);
    }

    [Test]
    public async Task ADiagnostic_HasNoResult()
    {
        TransformResult<string> result = Error;

        await Assert.That(result.Result).IsNull();
        await Assert.That(result.IsEmpty).IsFalse();
        await Assert.That(result.Diagnostics).IsEquivalentTo([Error]);
    }

    [Test]
    public async Task Diagnostics_FromAnArrayOrASequence()
    {
        TransformResult<string> fromArray = ImmutableArray.Create(Error, Warning);
        var fromSequence = new TransformResult<string>(new List<DiagnosticInfo> { Error, Warning });

        await Assert.That(fromArray.Diagnostics).IsEquivalentTo([Error, Warning]);
        await Assert.That(fromSequence.Diagnostics).IsEquivalentTo([Error, Warning]);
        await Assert.That(fromSequence.Result).IsNull();
    }

    [Test]
    public async Task ADefaultArray_ReadsAsEmpty()
    {
        ImmutableArray<DiagnosticInfo> none = default;
        TransformResult<string> converted = none;
        var constructed = new TransformResult<string>("value", none);

        await Assert.That(converted.Diagnostics.IsDefault).IsFalse();
        await Assert.That(converted.IsEmpty).IsTrue();
        await Assert.That(constructed.Diagnostics.IsDefault).IsFalse();
        await Assert.That(constructed.HasDiagnostics).IsFalse();
    }

    [Test]
    public async Task AResultWithAWarning_KeepsBoth()
    {
        var result = new TransformResult<string>("value", [Warning]);

        await Assert.That(result.Result).IsEqualTo("value");
        await Assert.That(result.Diagnostics).IsEquivalentTo([Warning]);
    }

    [Test]
    public async Task With_ReplacesTheDiagnostics()
    {
        TransformResult<string> result = "value";
        var warned = result with { Diagnostics = [Warning] };

        await Assert.That(warned.Result).IsEqualTo("value");
        await Assert.That(warned.Diagnostics).IsEquivalentTo([Warning]);
        await Assert.That(result.HasDiagnostics).IsFalse();
    }

    /// <summary>
    /// A metadata type that is a struct: <c>return default;</c> gives a default struct, which must be left out as a
    /// default class is, rather than generated from.
    /// </summary>
    [Test]
    public async Task Default_OfAStructResult_IsEmpty()
    {
        TransformResult<StructInfo> result = default;

        await Assert.That(result.IsEmpty).IsTrue();
        await Assert.That(result.HasResult).IsFalse();
    }

    [Test]
    public async Task ADiagnostic_OfAStructResult_HasNoResult()
    {
        TransformResult<StructInfo> one = Error;
        TransformResult<StructInfo> several = ImmutableArray.Create(Error, Warning);
        var sequence = new TransformResult<StructInfo>(new List<DiagnosticInfo> { Warning });

        await Assert.That(one.HasResult).IsFalse();
        await Assert.That(several.HasResult).IsFalse();
        await Assert.That(sequence.HasResult).IsFalse();
        await Assert.That(one.IsEmpty).IsFalse();
    }

    /// <summary>For a struct, a result that is given counts, also when it is the struct's default value.</summary>
    [Test]
    public async Task AGivenStructResult_Counts_EvenAtItsDefaultValue()
    {
        TransformResult<int> zero = 0;
        var withWarning = new TransformResult<StructInfo>(default(StructInfo), [Warning]);

        await Assert.That(zero.HasResult).IsTrue();
        await Assert.That(zero.IsEmpty).IsFalse();
        await Assert.That(withWarning.HasResult).IsTrue();
    }

    [Test]
    public async Task ANullClassResult_IsNone()
    {
        var result = new TransformResult<string>(null, [Warning]);

        await Assert.That(result.HasResult).IsFalse();
        await Assert.That(new TransformResult<string>((string?)null!).IsEmpty).IsTrue();
    }

    [Test]
    public async Task With_SetsOrClearsTheResult()
    {
        TransformResult<StructInfo> error = Error;
        TransformResult<string> value = "value";

        await Assert.That((error with { Result = new("set") }).HasResult).IsTrue();
        await Assert.That((value with { Result = null }).HasResult).IsFalse();
    }

    [Test]
    public async Task Builder_OfAStruct_HasAResult_OnlyOnceItIsSet()
    {
        var builder = Transform.Build<StructInfo>();
        builder.Diagnostics.Add(Warning);

        var without = builder.GetResult();
        builder.Result = new StructInfo("set");
        var with = builder.GetResult();

        await Assert.That(without.HasResult).IsFalse();
        await Assert.That(with.HasResult).IsTrue();
        await Assert.That(with.Result.Name).IsEqualTo("set");
        await Assert.That(builder.FatalError(Error).HasResult).IsFalse();
    }

    [Test]
    public async Task Builder_GivesTheResultAndItsDiagnostics()
    {
        var builder = Transform.Build<string>();
        builder.Result = "value";
        builder.Diagnostics.Add(Warning);

        TransformResult<string> result = builder;

        await Assert.That(result.Result).IsEqualTo("value");
        await Assert.That(result.Diagnostics).IsEquivalentTo([Warning]);
    }

    [Test]
    public async Task Builder_WithoutDiagnostics_GivesAnEmptyArray()
    {
        var builder = Transform.Build<string>();
        builder.Result = "value";

        var result = builder.GetResult();

        await Assert.That(result.Diagnostics.IsDefault).IsFalse();
        await Assert.That(result.HasDiagnostics).IsFalse();
    }

    [Test]
    public async Task Builder_FatalError_DropsTheResult_AndComesFirst()
    {
        var builder = Transform.Build<string>();
        builder.Result = "value";
        builder.Diagnostics.Add(Warning);

        var result = builder.FatalError(Error);

        await Assert.That(result.Result).IsNull();
        await Assert.That(result.Diagnostics).IsEquivalentTo([Error, Warning]);
        await Assert.That(result.Diagnostics[0]).IsEqualTo(Error);
    }

    public readonly record struct StructInfo(string Name);
}
