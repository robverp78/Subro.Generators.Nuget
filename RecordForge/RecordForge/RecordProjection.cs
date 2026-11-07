namespace Subro.RecordForge;

using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;

/// <summary>
/// A generated type's projection (<c>Projection</c>: <c>(IClubCode x) =&gt; new ClubCode(x.Id, …)</c>) made to start from
/// another type that implements the interface, such as an entity: <c>(Club x) =&gt; new ClubCode(((IClubCode)x).Id, …)</c>.
/// </summary>
/// <remarks>
/// That is the expression the compiler itself writes for a generic method with <c>where T : IClubCode</c>, which query
/// providers such as Entity Framework translate to the entity's own columns. Made once per pair of types.
/// </remarks>
public static class RecordProjection
{
    private static readonly ConcurrentDictionary<(LambdaExpression, Type), LambdaExpression> cache = new();

    /// <summary>
    /// <paramref name="projection"/>, which has one parameter of an interface type, with a parameter of
    /// <paramref name="sourceType"/> instead, which implements that interface.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="sourceType"/> does not implement the projection's parameter type.</exception>
    public static LambdaExpression From(LambdaExpression projection, Type sourceType)
    {
        if (projection is null) throw new ArgumentNullException(nameof(projection));
        if (sourceType is null) throw new ArgumentNullException(nameof(sourceType));
        if (projection.Parameters.Count != 1)
            throw new ArgumentException("A projection has exactly one parameter.", nameof(projection));

        var parameter = projection.Parameters[0];
        if (parameter.Type == sourceType)
            return projection;

        if (!parameter.Type.IsAssignableFrom(sourceType))
            throw new ArgumentException($"{sourceType.Name} does not implement {parameter.Type.Name}.", nameof(sourceType));

        return cache.GetOrAdd((projection, sourceType), static key =>
        {
            var (lambda, source) = key;
            var old = lambda.Parameters[0];
            var replacement = Expression.Parameter(source, old.Name);
            var body = new ParameterReplacer(old, Expression.Convert(replacement, old.Type)).Visit(lambda.Body);
            return Expression.Lambda(body, replacement);
        });
    }

    /// <summary>
    /// <paramref name="projection"/> for a parameter of <typeparamref name="TSource"/>: <c>(Club x) =&gt; new ClubCode(…)</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><typeparamref name="TSource"/> does not implement the projection's parameter type.</exception>
    public static Expression<Func<TSource, TResult>> From<TSource, TResult>(LambdaExpression projection)
    {
        if (projection is null) throw new ArgumentNullException(nameof(projection));
        if (projection.ReturnType != typeof(TResult))
            throw new ArgumentException($"The projection makes {projection.ReturnType.Name}, not {typeof(TResult).Name}.", nameof(projection));

        return (Expression<Func<TSource, TResult>>)From(projection, typeof(TSource));
    }

#if NET7_0_OR_GREATER
    /// <summary>
    /// The projection of <typeparamref name="TRecord"/> for a parameter of <typeparamref name="TSource"/>, an entity that
    /// implements the interface the record was generated from: <c>RecordProjection.For&lt;Club, ClubCode&gt;()</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="TRecord"/> is abstract and has no projection.</exception>
    public static Expression<Func<TSource, TRecord>> For<TSource, TRecord>() where TRecord : IForgedRecord<TRecord>
        => From<TSource, TRecord>(TRecord.Projection
            ?? throw new InvalidOperationException($"{typeof(TRecord).Name} is abstract and cannot be projected into."));
#endif

    private sealed class ParameterReplacer(ParameterExpression old, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == old ? replacement : node;
    }
}
