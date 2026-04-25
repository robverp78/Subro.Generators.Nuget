using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

#nullable enable


namespace Subro.Generators
{

    public readonly struct EquatableArray<T>(ImmutableArray<T> values) : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
        where T : IEquatable<T>
    {
        private readonly ImmutableArray<T> values = values;

        public EquatableArray(IEnumerable<T> values) : this([..values])
        {
        }
        public T this[int index] => values[index];

        public int Length => values.Length;
        int IReadOnlyCollection<T>.Count => values.Length;

        public bool Equals(EquatableArray<T> other)
            => values.SequenceEqual(other.values); // values.AsSpan().SequenceEqual(other.values.AsSpan());

        public override bool Equals(object? obj) =>
            obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var item in values)
                hash.Add(item);
            return hash.ToHashCode();
        }

        public ImmutableArray<T>.Enumerator GetEnumerator() => values.GetEnumerator();

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)values).GetEnumerator();

        public static implicit operator EquatableArray<T>(ImmutableArray<T> values) => new(values);
        public static bool operator ==(EquatableArray<T> arr1, EquatableArray<T> arr2) => arr1.Equals(arr2);
        public static bool operator !=(EquatableArray<T> arr1, EquatableArray<T> arr2) => !arr1.Equals(arr2);
    }

    partial class GeneratorFunctions
    {
        public static EquatableArray<T> ToEquatable<T>(this ImmutableArray<T>.Builder builder)
            where T : IEquatable<T> => new(builder.ToImmutable());

        public static EquatableArray<T> ToEquatable<T>(this IEnumerable<T> values)
            where T : IEquatable<T> => new(values);
    }
}
