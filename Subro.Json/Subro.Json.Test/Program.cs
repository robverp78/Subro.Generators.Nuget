using Subro.JsonTypes;

// Builds with Subro.Json's generator: [MarkAsJsonSerializable] on keyword, nullable, generic and type-parameter types below
// has to give attributes that compile (it gave typeof(global::int) and typeof(global::int?) before 2026-11-12).
Console.WriteLine(Subro.Json.Options.TypeInfoResolverChain.Count.ToString());
Console.WriteLine(Subro.Json.Stringify(new Sample("Ada")));

[MarkAsJsonSerializable]
public record Sample(string Name);

public static class Marked
{
    [return: MarkAsJsonSerializable]
    public static int Count() => 0;

    public static void Take(
        [MarkAsJsonSerializable] int? number,
        [MarkAsJsonSerializable] string? text,
        [MarkAsJsonSerializable] List<Sample> samples,
        [MarkAsJsonSerializable] Sample[] array,
        [MarkAsJsonSerializable] Dictionary<string, int?> map)
    {
    }

    // A type parameter has no contract until it is known: left out, not written as typeof(global::T).
    public static T Echo<T>([MarkAsJsonSerializable] T value, [MarkAsJsonSerializable] List<T> values) => value;
}
