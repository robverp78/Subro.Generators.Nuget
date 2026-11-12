# Subro.Json

One place for JSON type information: the settings, converters and source-generated contracts every part of an
application reads and writes JSON with, so that an API, its clients and its tests cannot drift apart. Viaduct reads and
writes through it on both sides.

## Use

```csharp
// Read and write: the registered contract for T, else one built by reflection with the same settings and converters.
var text = Json.Stringify(value);
var back = Json.Parse<Order>(text);

// Or hand the contract to System.Text.Json yourself.
JsonTypeInfo<Order> info = Json.TypeInfo<Order>();
```

`Json.TypeInfo<T>()` never returns null. Where there is no reflection (a trimmed or AOT app, such as a client on a
phone) and no registered context has the type, it throws an error that names the type, rather than whatever
System.Text.Json fails with later. `Json.GetTypeInfo<T>()` is the registered contract only, and null without one.

## Register

Converters and contexts are added to registries, so they can be added at any time, also after the options were used:

```csharp
[JsonSerializable(typeof(Order))]
public partial class OrderContext : JsonSerializerContext
{
    [ModuleInitializer]
    internal static void Register() => Json.AddContextToDefault(Default);
}

Json.RegisterConverter(new MoneyConverter());
```

A context registered in a type's own assembly is found before that type is first looked up: the assembly's module
initializers are run first. A type looked up before a context elsewhere registered it keeps what it got.

For another `JsonSerializerOptions`, such as ASP.NET Core's: `options.AddRegisteredConverters()`, and
`options.TypeInfoResolverChain.Insert(0, new Json.GlobalJsonTypeInfoForwarder())` for the registered contexts.

`Json.Options` (camelCase) is made read-only on the first lookup: only then does it keep the contracts it builds.

## Schemas (.NET 9 and later)

`Json.GetJsonSchema<T>()` is the JSON schema of `T` from the same contract, for an LLM's structured output or a tool's
parameters, so the schema describes exactly the JSON that is parsed back. Built once per type.

## AOT

The library is checked for trimming and AOT. For an AOT app, every type that is read or written needs a registered
context: System.Text.Json's generator cannot see types or attributes that another generator produced, so the context
is written by hand, in a project downstream of any generated types it lists.

The generator (`[MarkAsJsonSerializable]`) is for future use: its `[JsonSerializable]` attributes do not reach
System.Text.Json's generator, for the same reason.
