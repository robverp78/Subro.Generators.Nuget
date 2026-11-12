using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

#nullable enable


namespace Subro.Generators
{

    /// <summary>
    /// An immutable array that compares by its items, so a generator's model that holds one caches: an
    /// <see cref="ImmutableArray{T}"/> or <c>T[]</c> compares by reference, which makes every run look changed.
    /// </summary>
    /// <remarks>Built from a collection expression too (<c>[a, b]</c>); a default value is empty.</remarks>
    [System.Runtime.CompilerServices.CollectionBuilder(typeof(EquatableArray), nameof(EquatableArray.Create))]
    public readonly struct EquatableArray<T>(ImmutableArray<T> values) : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
        where T : IEquatable<T>
    {


        // A default EquatableArray holds a default ImmutableArray, which throws when enumerated: read as empty.
        private ImmutableArray<T> Values => values.IsDefault ? ImmutableArray<T>.Empty : values;

        public EquatableArray(IEnumerable<T> values) : this([..values])
        {
        }
        public T this[int index] => Values[index];

        public int Length => Values.Length;
        int IReadOnlyCollection<T>.Count => Values.Length;

        public bool Equals(EquatableArray<T> other)
            => Values.SequenceEqual(other.Values);

        public override bool Equals(object? obj) =>
            obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var item in Values)
                hash.Add(item);
            return hash.ToHashCode();
        }

        public ImmutableArray<T>.Enumerator GetEnumerator() => Values.GetEnumerator();

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Values).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Values).GetEnumerator();

        public static implicit operator EquatableArray<T>(ImmutableArray<T> values) => new(values);
        public static bool operator ==(EquatableArray<T> arr1, EquatableArray<T> arr2) => arr1.Equals(arr2);
        public static bool operator !=(EquatableArray<T> arr1, EquatableArray<T> arr2) => !arr1.Equals(arr2);
    }

    /// <summary>Builds an <see cref="EquatableArray{T}"/> from a collection expression.</summary>
    public static class EquatableArray
    {
        public static EquatableArray<T> Create<T>(ReadOnlySpan<T> values) where T : IEquatable<T>
            => new(ImmutableArray.Create(values.ToArray()));
    }

    partial class GeneratorFunctions
    {
        public static EquatableArray<T> ToEquatable<T>(this ImmutableArray<T>.Builder builder)
            where T : IEquatable<T> => new(builder.ToImmutable());

        public static EquatableArray<T> ToEquatable<T>(this IEnumerable<T> values)
            where T : IEquatable<T> => new(values);
    }
}
