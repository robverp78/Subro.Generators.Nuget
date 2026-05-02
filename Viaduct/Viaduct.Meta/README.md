# Viaduct

> **Define your service interface once. Viaduct wires it to an ASP.NET Core server _and_ a typed HTTP client — automatically, at compile time, with no duplication.**

`Viaduct` is a convenience meta-package that includes both `Viaduct.Server` and `Viaduct.Client`. Use it when a **single project needs both sides** — for example, integration test projects or monolith applications that act as both server and consumer.

For most projects, reference `Viaduct.Server` or `Viaduct.Client` directly to keep dependencies minimal.

---

## The single-source-of-truth model

```
┌─────────────────────────────────────────┐
│  Shared project (IUserService)          │
│  Define once. Change once. Done.        │
└────────────┬────────────────────────────┘
             │ same interface
     ┌───────┴───────┐
     ▼               ▼
  Server side      Client side
  maps endpoints   generates proxy
     └───────┬───────┘
             │
  ┌──────────┴──────────┐
  │  This project        │  ← references Viaduct (this package)
  │  (e.g. test project) │
  └─────────────────────┘
```

---

## Quick start

```csharp
// Integration test / monolith Program.cs
builder.Services.AddScopedAndMap<IUserService, UserService>();  // server side

builder.Services.CreateAndAddHttpClient<IUserService>(options =>
{
    options.BaseUrl = "https://localhost:5001";
});                                                              // client side

var app = builder.Build();
app.AddRegisteredEndpoints();
```

---

## Which package do I need?

| Your project | Package |
|---|---|
| Defines the shared interface | `Viaduct.Core` |
| ASP.NET Core server | `Viaduct.Server` |
| Client / consumer app | `Viaduct.Client` |
| **Needs both** (integration tests, monolith) | **`Viaduct`** |

---

## What's included

Referencing this package is equivalent to:

```xml
<PackageReference Include="Viaduct.Server" Version="..." />
<PackageReference Include="Viaduct.Client" Version="..." />
```

The `Viaduct.Generator` source generator is a shared transitive dependency — it runs exactly once even when both are referenced.

---

## Requirements

- .NET 8, 9, or 10
- C# 14+ (uses `[InterceptsLocation]` for compile-time interception)
- ASP.NET Core

[Full documentation and examples →](https://github.com/robverp78/Subro.Generators.Nuget/blob/main/Viaduct/README.md)
