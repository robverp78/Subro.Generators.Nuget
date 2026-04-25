# RecordForge

[![NuGet](https://img.shields.io/nuget/v/Subro.RecordForge.svg)](https://www.nuget.org/packages/Subro.RecordForge/)

**RecordForge** is a Roslyn incremental source generator that forges a concrete
`record`, `record struct`, `struct` or `class` (or their readonly versions) implementation from a
plain interface. Define the contract once – let the generator produce the boilerplate.

```csharp
using Subro.RecordForge;

[GenerateRecord]
public interface IPerson
{
    string Name { get; }
    DateOnly DateOfBirth { get; }
}

// --- generated (roughly) ---
// public partial record Person(string Name, System.DateOnly DateOfBirth):IPerson;
```

That is all if you want the defaults. No other code to write, no T4 templates, no reflection at runtime.
So AOT safe.

a test environment can be found [here](https://robverp78.github.io/RecordForge/)

---

## Contents

- [Why](#why)
- [Installation](#installation)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [Choosing the kind of type](#choosing-the-kind-of-type)
- [Naming and namespace overrides](#naming-and-namespace-overrides)
- [Controlling constructors](#controlling-constructors)
- [Setters, abstract and partial](#setters-abstract-and-partial)
- [Assembly level generation](#assembly-level-generation)
- [Cross-assembly generation](#cross-assembly-generation)
- [FAQ](#faq)

---

## Why

Interface-first design is a natural fit for many .NET projects — it keeps your domain contracts 
clean, supports dependency injection, enables mocking in tests, and gives you a stable serialization surface. 
The friction starts when you need a concrete implementation: even with record types and IntelliSense, 
it is still a another thing to write and a another thing to keep in sync.

RecordForge removes that overhead. You own the interface — and with it the full freedom to use it for DI, 
mocking, multiple implementations, or serialization contracts. The generator simply gives you one default 
implementation for free, so you can stop writing boilerplate and focus on the parts that actually differ.

By default the types are made partial so they can be easily extended to in case it is needed.

---

## Extensibility & Roadmap

Because generated types are `partial` by default, you can extend them in your own files without touching the 
generated output. A practical example is JSON serialization: you can designate the generated record as the 
default (de)serializer for its interface today simply by adding a [JsonConverter] attribute or a custom 
converter in a separate partial file.

This is also the direction RecordForge is heading. A planned companion library will combine record generation 
with JSON type coupling, so the serialization wiring is handled automatically alongside the implementation. 
Roslyn does not allow source generators to build on each other's output directly, which is why this will ship as 
an integrated library rather than a stacked generator — but the partial design means you are not blocked in the meantime.

---

## Installation

```
dotnet add package Subro.RecordForge
```

The package contains both the runtime attributes and the source generator. There is
nothing else to wire up.

---

## Requirements

- Roslyn 4.x / Visual Studio 2022 or .NET SDK 6+ on the compiler side (required by any
  incremental source generator).
- Language version: C# 9 or newer in the consuming project so that `record` types are available. If you
  only target `class` / `struct` you can use older C# language versions, but `record`
  and `record struct` require C# 9 and 10 respectively. (language version, not .net version)

The generator itself targets `netstandard2.0`, so it runs in every modern build
environment.

---

## Quick start

Annotate an interface with `[GenerateRecord]`:

```csharp
using Subro.RecordForge;

namespace MyApp.Domain;

[GenerateRecord]
public interface IPerson
{
    string Name { get; }
    DateOnly DateOfBirth { get; }
    int LoginCount { get; set; } 
}
```

The generator produces a `Person` record in the same namespace:

```csharp
// auto-generated
namespace MyApp.Domain
{
    public partial record Person(string Name, System.DateOnly DateOfBirth):IPerson
    {
		public int LoginCount{get;set;}
    }
}
```

Note that the default implementation creates a property for interface properties that
are not readonly, but does not add it in the constructor.
That behaviour can be overridden though (see [constructors](#controlling-constructors) ))

Consume the generated type like any other record:

```csharp
IPerson p = new Person("Ada", 36);
```

By default:

- The generated type is a `record`.
- The generated name is the interface name **without** a leading `I` (so `IPerson` becomes `Person`).
- The generated type lives in the interface's namespace.
- The generated type is `partial`, so you can add your own members in another file.
- A property is only explicitly created when needed.


---

## Choosing the kind of type

Pass a `RecordKind` to pick what should be generated:

```csharp
[GenerateRecord(RecordKind.RecordStruct)]
public interface IPoint { double X { get; } double Y { get; } }

[GenerateRecord(RecordKind.ReadOnlyRecordStruct)]
public interface IVector { double X { get; } double Y { get; } }

[GenerateRecord(RecordKind.Struct)]
public interface ISize { double Width { get; } double Height { get; } }

[GenerateRecord(RecordKind.ReadonlyStruct)]
public interface IRange { int Min { get; } int Max { get; } }

[GenerateRecord(RecordKind.Class)]
public interface IAccount { string Id { get; } decimal Balance { get; set; } }
```

The available kinds are:

- `RecordKind.Record` *(default)* &mdash; `public record`.
- `RecordKind.RecordStruct` &mdash; `public record struct`.
- `RecordKind.ReadOnlyRecordStruct` &mdash; `public readonly record struct`.
- `RecordKind.Struct` &mdash; plain `public struct`.
- `RecordKind.ReadonlyStruct` &mdash; `public readonly struct`.
- `RecordKind.Class` &mdash; plain `public class`.

For readonly kinds, every property becomes `init` (or throws `NotSupportedException`
through an explicit interface implementation if the interface insists on a setter).

---

## Naming and namespace overrides

By default the generated name is the interface name without its leading `I`, in the
same namespace. Both can be overridden:

```csharp
[GenerateRecord(RecordName = "PersonDto", NameSpace = "MyApp.Contracts")]
public interface IPerson
{
    string Name { get; }
    DateOnly DateOfBirth { get; }
}
```

This generates `MyApp.Contracts.PersonDto`, still implementing `IPerson`.

---

## Controlling constructors

Use `ConstructorUsage` to decide which constructors are generated. For flags with
multiple bits, combine them with `|`.

```csharp
[GenerateRecord(
    RecordKind.Class,
    ConstructorUsage = ConstructorUsage.Empty | ConstructorUsage.AllProperties)]
public interface IOrder
{
    Guid    Id       { get; }
    string  Customer { get; }
    decimal Total    { get; set; }
}
```

Generates:

```csharp
    public partial class Order:IOrder
    {
        public Order()
        {
        }

        public Order(System.Guid Id, string Customer, decimal Total)
        {
            this.Id = Id;
            this.Customer = Customer;
            this.Total = Total;
        }

		public System.Guid Id{get;}
		public string Customer{get;}
		public decimal Total{get;set;}
    }
```

Values:

- `ConstructorUsage.Automatic` *(default)* &mdash; pick a sensible default based on the
  `RecordKind`:
  - Readonly kinds: all properties go through the constructor.
  - Records / record structs: only readonly properties.
  - Plain class / struct: parameterless constructor.
- `ConstructorUsage.Empty` &mdash; parameterless constructor.
- `ConstructorUsage.ReadonlyProperties` &mdash; constructor for `{ get; }` properties only.
- `ConstructorUsage.ReadonlyAndInitProperties` &mdash; constructor for `{ get; }` and
  `{ get; init; }` properties (setters are property-only).
- `ConstructorUsage.AllProperties` &mdash; constructor for every property on the interface.

When more than one value is requested, RecordForge emits a primary constructor (for
record kinds) plus the additional constructors, all chaining to the smallest one.
For non record type, normal (not a primary) constructors are used, so that the option
exist to use those in older frameworks.

---

## Setters, abstract and partial

The `GenerateRecord` attribute exposes a few extra knobs:

```csharp
[GenerateRecord(
    RecordKind.Record,
    AsPartial            = true,   // default: true – allows you to extend the generated type
    AsAbstract           = false,  // default: false
    AlwaysCreateSetters  = true)]  // turn every interface `get` into `get; set;` / `get; init;`
public interface IUser
{
    string Id       { get; }
    string Username { get; }
}
```

- `AsPartial` &mdash; emits the type as `partial` so you can add members in another file.
- `AsAbstract` &mdash; emits the type as `abstract`. Useful for shared bases.
- `AlwaysCreateSetters` &mdash; promote every `get`-only property to a settable one. On
  readonly kinds the setter becomes `init` to stay valid.

---

## Assembly level generation

Sometimes you cannot (or do not want to) put the attribute on the interface itself,
for instance because the interface lives in a library you do not own, or because you
want to keep the implementation in a different project. Use
`[assembly: GenerateRecordFromInterface(...)]` instead:

```csharp
using Subro.RecordForge;

// One interface
[assembly: GenerateRecordFromInterface(typeof(MyLib.IPerson))]

// Same interface but a different generated name
[assembly: GenerateRecordFromInterface(typeof(MyLib.IPerson), RecordName = "PersonDto")]

// Several interfaces at once
[assembly: GenerateRecordFromInterface(new[] { typeof(MyLib.IPerson), typeof(MyLib.IAddress) })]

// Pick a specific kind
[assembly: GenerateRecordFromInterface(typeof(MyLib.IPoint), RecordKind.ReadOnlyRecordStruct)]
```

All the same options (`RecordName`, `NameSpace`, `AsPartial`, `AsAbstract`,
`AlwaysCreateSetters`, `ConstructorUsage`) are available.

---

## Cross-assembly generation

Because `GenerateRecordFromInterface` can be placed on the assembly, the interface and
the generated implementation can live in **different** assemblies:

```csharp
// In Contracts.dll – just the interface, no generator dependency needed here.
namespace Contracts;
public interface IPerson { string Name { get; } int Age { get; } }
```

```csharp
// In MyApp.dll – references Contracts.dll and the Subro.RecordForge NuGet.
using Subro.RecordForge;

[assembly: GenerateRecordFromInterface(typeof(Contracts.IPerson))]
```

The generator emits `Contracts.Person` (or wherever you point `NameSpace`) inside
`MyApp.dll`.

---

## FAQ


### What happens if my interface has methods, not just properties?

Methods (and properties without a getter) are ignored by the generator. Because the
generated type is `partial` by default, you are expected to provide the method
implementation yourself in a separate file.

### Does it support nullable annotations, custom types, collections, …?

Yes. The generator copies the property type verbatim from the interface, including
nullable annotations, generics and `ref`-like types where they are valid on the
chosen `RecordKind`.

### Does the NuGet package add a runtime dependency?

No. `Subro.RecordForge` only adds compile-time metadata (the attributes) and the
generator DLL loaded by Roslyn. Your published binaries do not carry any extra runtime
dependency.

### Can I use this from a library that targets `netstandard2.0`?

Yes for `class` / `struct` output. For `record` / `record struct` output your project
needs a C# language version that supports those constructs (C# 9 / 10). A common
pattern on `netstandard2.0` is to add an `IsExternalInit` polyfill, which is exactly
what this package does for its own attributes.

---

## License

MIT &mdash; see `LICENSE` in the repository.

## External reference

Built on top of [Subro.Generators.TransformResult](https://www.nuget.org/packages/Subro.Generators.TransformResult/)
for clean transform pipelines and diagnostics.
