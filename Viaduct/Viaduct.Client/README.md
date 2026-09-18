# Viaduct.Client

> **Define your service interface once. Viaduct wires it to an ASP.NET Core server _and_ a typed HTTP client — automatically, at compile time, with no duplication.**

`Viaduct.Client` is the client-side package. It reads your interface at compile time and generates a typed `HttpClient` proxy — you inject the interface and call methods as if they were local. The server project uses the same interface to generate matching endpoints, so both sides stay in sync automatically.

---

## The single-source-of-truth model

```
┌─────────────────────────────────────────┐
│  Shared project (IUserService)          │
│  Define once. Change once. Done.        │
└────────────┬────────────────────────────┘
             │
             ▼
  ┌──────────────────────┐
  │  Your client project  │  ← references Viaduct.Client
  │  (this package)       │
  │                       │
  │  CreateAndAddHttpClient│  → GET https://api/.../GetUserAsync/{id}
  │  <IUserService>()     │     POST https://api/.../CreateUserAsync/...
  └──────────────────────┘
```

---

## Quick start

```csharp
// Program.cs (client app)
builder.Services.CreateAndAddHttpClient<IUserService>(options =>
{
    options.BaseUrl = "https://api.example.com";
});

// Inject and use anywhere — looks like a local call
public class Dashboard(IUserService users)
{
    public async Task<User> Load(int id) => await users.GetUserAsync(id);
}
```

---

## Configuration

```csharp
builder.Services.CreateAndAddHttpClient<IUserService>(options =>
{
    options.BaseUrl = "https://api.example.com";
    options.BasePath = "/api/v1";
    options.UseInterfaceNameForPath = true;
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler());
```

---

## Parameter binding

- **Simple types** (`int`, `string`, `bool`, `DateTime`, `Guid`, etc.) → route or query parameters
- **Complex types** (classes, records) → JSON request body
- **`CancellationToken`** → forwarded to the HTTP call, never serialized

---

## Authentication

A client that requires authorization sends an access token with every call:

```csharp
builder.Services.CreateAndAddHttpClient<IUserService>(options =>
{
    options.BaseUrl = "https://api.example.com";
    options.RequiresAuthorization = true;
    options.GetAccessToken = (request, ct) => new ValueTask<string?>(tokenStore.Current);
});
```

Where getting the token is itself a service call — a session that refreshes, in Blazor or MAUI — register an
`IViaductAccessTokenProvider` instead and leave the options alone. Either way it is asked per request, so a
refreshed token is used as soon as it exists. Returning `null` means nobody is signed in: the call goes out
unauthenticated and the server answers.

---

## Error handling

A failure status throws `ViaductHttpException`, carrying the `StatusCode`, the `ResponseBody` and the request
that was made:

```csharp
catch (ViaductHttpException ex) when (ex.StatusCode == 404)
{
    return null;
}
```

It derives from `ViaductException`, which is still what a request that never got an answer throws. A cancelled
call throws `OperationCanceledException`, unwrapped.

---

## Which package do I need?

| Your project | Package |
|---|---|
| Defines the shared interface | `Viaduct.Core` |
| ASP.NET Core server | `Viaduct.Server` |
| **Client / consumer app** | **`Viaduct.Client`** |
| Needs both (integration tests, monolith) | `Viaduct` |

---

## Requirements

- .NET 8, 9, or 10
- C# 14+ (uses `[InterceptsLocation]` for compile-time interception)

[Full documentation and examples →](https://github.com/robverp78/Subro.Generators.Nuget/blob/main/Viaduct/README.md)
