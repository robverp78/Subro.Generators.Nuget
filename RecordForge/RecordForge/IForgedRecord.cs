namespace Subro.RecordForge;

#if NET7_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

/// <summary>
/// A type generated from an interface by <see cref="GenerateRecordAttribute"/>, whose metadata generic code can read from
/// the type itself: <c>TRecord.Fields</c> where <c>TRecord : IForgedRecord&lt;TRecord&gt;</c>.
/// </summary>
/// <remarks>
/// Implemented by the generated type when the consuming project targets .NET 7 or later with C# 11 or later, which
/// <c>static abstract</c> members need. Elsewhere the generated type has the same public static members, without this
/// interface.
/// </remarks>
public interface IForgedRecord<TSelf> where TSelf : IForgedRecord<TSelf>
{
    /// <summary>The interface the type was generated from.</summary>
    static abstract Type Interface { get; }

    /// <summary>The type's properties, as its interfaces declare them; a base interface's first.</summary>
    static abstract IReadOnlyList<RecordField> Fields { get; }

    /// <summary>
    /// A new instance made from anything implementing <see cref="Interface"/>, as an expression with a parameter of that
    /// interface, for a query to select into; null for an abstract type. <see cref="RecordProjection"/> gives it for a
    /// parameter of another type.
    /// </summary>
    static abstract LambdaExpression? Projection { get; }
}
#endif
