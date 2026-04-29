# Viaduct

**Viaduct** is a C# source generator that automatically bridges your dependency-injected service interfaces to both ASP.NET Core Minimal API endpoints (server side) and typed `HttpClient` proxies (client side) — with zero boilerplate and full AOT support.

Define an interface once, register it with a single call, and Viaduct generates all the wiring at compile time.

---

## How it works

You define a service interface in a shared project:

```csharp
public interface IUserService
{
    Task<User> GetUserAsync(int id, CancellationToken ct = default);
    Task CreateUserAsync(string name, DateTime dateOfBirth);
}
```

On the **server**, one call registers the implementation *and* maps every method as a Minimal API endpoint:

```csharp
// Program.cs (server)
builder.Services.AddScopedAndMap<IUserService, UserService>();

var app = builder.Build();
app.AddRegisteredEndpoints();   // maps all pending endpoints
```

On the **client**, one call creates a typed `HttpClient` proxy that implements the same interface:

```csharp
// Program.cs (client / consumer app)
builder.Services.CreateAndAddHttpClient<IUserService>(options =>
{
    options.BaseUrl = "https://api.example.com";
});
```

Viaduct generates all the server endpoint handlers and client HTTP calls at compile time — no reflection, no runtime code generation.

---

## Packages

> NuGet packages are planned. The split will be:

| Package | Purpose |
|---|---|
| `Viaduct` | Server-side endpoint mapping + the source generator |
| `Viaduct.Client` | Client-side HTTP proxy + the source generator |
| `Viaduct.Core` | Shared types (options, attributes, exceptions) |

Both `Viaduct` and `Viaduct.Client` include the generator. Reference whichever package(s) your project needs; the generator handles only the registrations it finds.

---

## Getting started

### 1 — Define the interface (shared project)

```csharp
using Viaduct;

[BasePath("/users")]                      // optional: override the default path
public interface IUserService
{
    Task<User> GetUserAsync(int id, CancellationToken ct = default);
    Task CreateUserAsync(string name, DateTime dateOfBirth);

    [ViaductIgnore]                       // skip entirely
    Task InternalMethodAsync();

    [ViaductIgnoreForServer]             // client proxy only, no server endpoint
    Task<User> LocalFallbackAsync(int id);
}
```

### 2 — Server registration

```csharp
// Register implementation + schedule endpoint mapping
builder.Services.AddScopedAndMap<IUserService, UserService>();
// or AddTransientAndMap / AddSingletonAndMap

// Apply all scheduled endpoint mappings
app.AddRegisteredEndpoints();
```

Alternatively, register service and endpoints separately:

```csharp
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.RegisterEndpoints<IUserService>();  // schedule mapping
// or
app.AddEndpoints<IUserService>();                    // map immediately
```

### 3 — Client registration

```csharp
builder.Services.CreateAndAddHttpClient<IUserService>(options =>
{
    options.BaseUrl = "https://api.example.com";
});
```

Inject the interface wherever you need it — the generated proxy handles all HTTP calls:

```csharp
public class MyController(IUserService users)
{
    public async Task<User> GetUser(int id) => await users.GetUserAsync(id);
}
```

---

## HTTP method conventions

Viaduct infers the HTTP method from the method name prefix. You can also specify it explicitly.

| Method name starts with | HTTP method |
|---|---|
| `Get`, `Find`, `Fetch`, `List`, `Search` | `GET` |
| `Create`, `Add`, `Insert`, `Post` | `POST` |
| `Update`, `Edit`, `Modify`, `Put` | `PUT` |
| `Delete`, `Remove` | `DELETE` |
| `Patch` | `PATCH` |
| anything else | `POST` |

Override with an attribute:

```csharp
[HttpMethodType("PUT")]
Task UpdateUserAsync(int id, string name);

// or standard ASP.NET Core attributes work too:
[HttpGet]
Task<User> GetUserAsync(int id);
```

---

## Routing

### Default path

By default the route is `/{MethodName}` under a group. The full URL depends on options (see below).

### Interface-level base path

```csharp
[BasePath("/users")]
public interface IUserService { ... }
// → /users/GetUserAsync, /users/CreateUserAsync, etc.
```

### Method-level custom route

```csharp
[CustomRoute("/by-email/{email}")]
Task<User> FindByEmailAsync(string email);
```

### Parameter binding

- **Simple types** (`int`, `string`, `bool`, `DateTime`, etc.) become **route parameters** by default when `UseRoutePathParameters` is `true` (the default). For `GET` they are appended to the path; for other verbs they are query parameters or route segments.
- **Complex types** (classes, records) are bound from the **request body** (`[FromBody]`).
- **`CancellationToken`** is always forwarded but never serialized.

---

## Configuration

All options are shared between server and client via `ViaductOptions`, with server- and client-specific subclasses for additional settings.

### Common options (`ViaductOptions`)

| Property | Default | Description |
|---|---|---|
| `BasePath` | `null` | Prefix prepended to all routes |
| `InterfaceBasePath` | `null` | Overrides the `[BasePath]` attribute on the interface |
| `UseInterfaceNameForPath` | `false` | Auto-derive base path from the interface name (`IUserService` → `/UserService`) |
| `UseRoutePathParameters` | `true` | Append simple parameters as route segments |
| `ForceLowerCaseRouteParameters` | `true` | Normalize parameter names to lowercase in URLs |
| `RequiresAuthorization` | `false` | Call `RequireAuthorization()` on the endpoint group |

### Server options

```csharp
builder.Services.AddScopedAndMap<IUserService, UserService>(options =>
{
    options.BasePath = "/api/v1";
    options.UseInterfaceNameForPath = true;
    options.RequiresAuthorization = true;
});
```

### Client options

```csharp
builder.Services.CreateAndAddHttpClient<IUserService>(options =>
{
    options.BaseUrl = "https://api.example.com";
    options.BasePath = "/api/v1";
    options.UseInterfaceNameForPath = true;
});
```

---

## Attributes reference

| Attribute | Target | Description |
|---|---|---|
| `[BasePath(string path)]` | Interface | Sets the base route for all methods on the interface |
| `[CustomRoute(string path)]` | Method | Overrides the default route for that method |
| `[HttpMethodType(string method)]` | Method | Explicitly sets the HTTP method |
| `[ViaductIgnore]` | Method | Skips the method for both server and client generation |
| `[ViaductIgnoreForServer]` | Method | Generates a client proxy for the method but no server endpoint |

---

## Example: real-world client

```csharp
// shared interface
public interface IJsonPlaceholderService
{
    Task<IReadOnlyList<Post>> GetPostsByUserAsync(int userId, CancellationToken ct = default);
    Task<IReadOnlyList<Todo>> GetTodosByUserAsync(int userId, bool? completed = null, CancellationToken ct = default);
}

// client registration
builder.Services.CreateAndAddHttpClient<IJsonPlaceholderService>(options =>
{
    options.BaseUrl = "https://jsonplaceholder.typicode.com";
});

// usage
var posts = await api.GetPostsByUserAsync(userId: 1);
var todos = await api.GetTodosByUserAsync(userId: 1, completed: true);
```

---

## Diagnostics

The generator emits errors and warnings at compile time:

| Code | Level | Meaning |
|---|---|---|
| `VE001` | Error | Could not create metadata |
| `VE002` | Error | Could not extract interface data |
| `VE003` | Error | Could not extract method data |
| `VE200` | Error | More than one body parameter on a method |
| `VE201` | Error | More than one `CancellationToken` parameter |
| `VE300` | Warning | Method is not async — sync methods work but can deadlock |
| `VE301` | Warning | Parameter is in the route template but cannot be bound from a string |
| `VE302` | Warning | Route template references a parameter not in the method signature |
| `VE303` | Warning | No methods found on the interface |

---

## Design notes

- **Interceptors**: Viaduct uses the Roslyn `[InterceptsLocation]` feature (C# 14) to replace your registration calls with generated ones at compile time. No new syntax is required.
- **Two-phase registration**: Service registration (`AddScopedAndMap`) and endpoint mapping (`AddRegisteredEndpoints`) are separated so you can control exactly when endpoints are wired up.
- **AOT-first**: All JSON serialization uses `System.Text.Json` source-generated `JsonSerializerContext`, so the generated code is fully compatible with Native AOT and trimming.
- **No runtime reflection**: URL construction uses generated closures; type resolution uses compile-time information only.

---

## Project structure

```
Viaduct.Core/           Shared types: ViaductOptions, attributes, exceptions
Viaduct.Shared/         Projitems: shared partials for options, attributes, helpers
Viaduct.Generator/      Roslyn incremental source generator
Viaduct.Server/         Server-side runtime: registration helpers, PendingMappers
Viaduct.Client/         Client-side runtime: ViaductClientBase, URL building
Example/
  SharedInterfaces/     Example interface and implementation
  Server/               Minimal API server example
  ClientConsoleExample/ Console app consuming the API via generated proxy
Viaduct.GeneratorTests/ Unit tests for the source generator
Viaduct.Tests/          Integration tests using ASP.NET Core TestHost
```

---

## Requirements

- .NET 8, 9, or 10
- C# 14+ (for the `[InterceptsLocation]` feature used by the generator)
- ASP.NET Core (server side only)
