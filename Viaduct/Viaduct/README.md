# Viaduct.Server

> **Define your service interface once. Viaduct wires it to an ASP.NET Core server _and_ a typed HTTP client — automatically, at compile time, with no duplication.**

`Viaduct.Server` is the server-side package. It reads your interface at compile time and generates Minimal API endpoint handlers — no manual `app.MapGet(...)`, no controllers, no boilerplate. The client project uses the same interface to generate a matching HTTP proxy, so both sides stay in sync automatically.

---

## The single-source-of-truth model

```
┌─────────────────────────────────────────┐
│  Shared project (IUserService)          │
│  Define once. Change once. Done.        │
└────────────┬────────────────────────────┘
             │
             ▼
  ┌─────────────────────┐
  │  Your server project │  ← references Viaduct.Server
  │  (this package)      │
  │                      │
  │  AddScopedAndMap     │  → generates GET /users/GetUserAsync/{id}
  │  <IUserService,      │     generates POST /users/CreateUserAsync/...
  │   UserService>()     │
  └─────────────────────┘
```

---

## Quick start

```csharp
// Program.cs
builder.Services.AddScopedAndMap<IUserService, UserService>();
// or AddTransientAndMap / AddSingletonAndMap

var app = builder.Build();
app.AddRegisteredEndpoints();   // maps all pending endpoints
app.Run();
```

Alternatively, register the service yourself and map endpoints separately:

```csharp
builder.Services.AddScoped<IUserService, UserService>();
app.AddEndpoints<IUserService>();
```

---

## Configuration

```csharp
builder.Services.AddScopedAndMap<IUserService, UserService>(options =>
{
    options.BasePath = "/api/v1";
    options.UseInterfaceNameForPath = true;   // IUserService → /UserService/...
    options.RequiresAuthorization = true;
});
```

---

## Endpoints document themselves

A method's XML comment becomes its endpoint's summary and description, so the OpenAPI document says what the
C# says:

```csharp
/// <summary>The user with this id.</summary>
/// <remarks>Answers 404 when no such user exists.</remarks>
Task<User> GetUserAsync(int id, CancellationToken ct = default);
```

Each endpoint is also tagged with the interface name and given an operationId of `{Interface}_{Method}` —
`UserService_GetUser` — which is what client generators build their method names from. Override either with
`[ViaductSummary]`, `[ViaductDescription]` and `[ViaductEndpointName]`, or turn them off per registration with
`IncludeEndpointMetadata` and `GenerateEndpointNames`.

---

## HTTP method inference

| Method name starts with | HTTP method |
|---|---|
| `Get`, `Find`, `Fetch`, `List`, `Search` | `GET` |
| `Create`, `Add`, `Insert`, `Post` | `POST` |
| `Update`, `Edit`, `Modify`, `Put` | `PUT` |
| `Delete`, `Remove` | `DELETE` |
| `Patch` | `PATCH` |
| anything else | `POST` |

Override with `[HttpGet]`, `[HttpPost]`, or `[HttpMethodType("PUT")]` on the method.

---

## Which package do I need?

| Your project | Package |
|---|---|
| Defines the shared interface | `Viaduct.Core` |
| **ASP.NET Core server** | **`Viaduct.Server`** |
| Client / consumer app | `Viaduct.Client` |
| Needs both (integration tests, monolith) | `Viaduct` |

---

## Requirements

- .NET 8, 9, or 10
- C# 14+ (uses `[InterceptsLocation]` for compile-time interception)
- ASP.NET Core

[Full documentation and examples →](https://github.com/robverp78/Subro.Generators.Nuget/blob/main/Viaduct/README.md)
