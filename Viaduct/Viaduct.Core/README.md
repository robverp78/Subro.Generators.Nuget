# Viaduct.Core

> **Define your service interface once. Viaduct wires it to an ASP.NET Core server _and_ a typed HTTP client — automatically, at compile time, with no duplication.**

`Viaduct.Core` is the foundation package. It contains the attributes and shared types that belong on your **interface definition project** — the shared contract that both the server and the client depend on.

It has no dependency on ASP.NET Core or `HttpClient`, keeping your contract project lean.

---

## The single-source-of-truth model

```
┌─────────────────────────────────────────┐
│  Shared project (references Viaduct.Core)│
│                                         │
│  [BasePath("/users")]                   │
│  public interface IUserService          │
│  {                                      │
│      Task<User> GetUserAsync(int id);   │
│      Task CreateUserAsync(string name); │
│  }                                      │
└────────────┬────────────────────────────┘
             │ same interface
     ┌───────┴───────┐
     ▼               ▼
  Server project   Client project
  Viaduct.Server   Viaduct.Client
  (maps endpoints) (generates proxy)
```

Define the interface **once** in a shared project. `Viaduct.Server` reads it and maps Minimal API endpoints. `Viaduct.Client` reads it and generates a typed `HttpClient` proxy. Both stay in sync automatically — change the interface and both sides are updated at the next compile.

---

## What's included

- **Attributes**: `[BasePath]`, `[CustomRoute]`, `[HttpMethodType]`, `[ViaductIgnore]`, `[ViaductIgnoreForServer]`
- **Options**: `ViaductOptions` base record
- **Exceptions**: `ViaductException`, `MissingRouteParameterException`

---

## Example

```csharp
using Viaduct;

[BasePath("/users")]
public interface IUserService
{
    Task<User> GetUserAsync(int id, CancellationToken ct = default);
    Task CreateUserAsync(string name, DateTime dateOfBirth);

    [ViaductIgnore]           // excluded from both server and client
    void InternalMethod();

    [ViaductIgnoreForServer]  // client proxy only, no server endpoint
    Task<User> LocalFallbackAsync(int id);
}
```

The server and client projects reference this interface and add `Viaduct.Server` / `Viaduct.Client` to do the actual wiring — no changes to the interface needed.

---

## Which package do I need?

| Your project | Package |
|---|---|
| **Defines the shared interface** | **`Viaduct.Core`** |
| ASP.NET Core server | `Viaduct.Server` |
| Client / consumer app | `Viaduct.Client` |
| Needs both (integration tests, monolith) | `Viaduct` |

---

## Requirements

- .NET 8, 9, or 10

[Full documentation and examples →](https://github.com/robverp78/Subro.Generators.Nuget/blob/main/Viaduct/README.md)
