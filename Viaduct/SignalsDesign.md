Signals — design

How a client hears that something changed on the server — a new message, a notification, a permission, a record someone
else saved — without asking for it. Raised by Rob 2026-11-16 for Vesta, built as a general Viaduct feature: a contract
declared once in a shared project, generated on both sides, as Viaduct does for service interfaces.

**Status: all six steps of the build order built 2026-11-16 (uncommitted), Vesta's first signal included. See "Built".** Rob decided the points under
"Decided" on 2026-11-16 and left the rest of the mechanics to Claude, on condition that each part sits behind an
abstraction that can be swapped later and chosen per environment or application. What follows "Decided" is Claude's
design on those terms, revisable until built.

Replaces the open "realtime channel" in `Subro.Core/Notifications/Displaying/Displaying.md` (open 2) and "Real time" in
Vesta's `ChatDesign.md` (open 8), which both said "SignalR or server-sent events, decide later".

---

# Decided

By Rob, 2026-11-16:

1. **A signal says what changed; the client fetches the data** through the normal endpoints, where permissions are
   checked. A signal may carry summary data (a count, a sender, the kinds of information that arrived) and always
   carries a timestamp. The client decides when to fetch: at once, after a pause, in batches, or when the app is in view,
   for data plans or just efficiency.
2. **Topics both ways**: the server derives a person's base topics (their inbox, their rooms); a page subscribes to
   short-lived ones on top (a record being edited), each checked for access.
3. **The engine is separate from the content.** It moves envelopes and never looks inside. What a signal holds and how
   it is handled belongs to the contract and the handler. A push message is just another carrier.
4. **Client and server stay in step the way Viaduct does**: one shared interface, the same on both sides. The server
   calls a generated implementation of it; the client implements it as the handler.
5. **In Viaduct**, not a Subro project.
6. **Merging**: a payload that implements a merge interface decides how two of it merge; otherwise the latest wins.
7. **Retention built in**, 24 hours by default, configurable by the application.
8. **Every moving part behind an abstraction** with its implementations, chosen at registration, so development, tests
   and production can differ and any part can be replaced later.

---

# The layers

Three server layers and one client layer, each its own interface. None knows how the others are implemented.

| Layer | Job | Interface | Implementations |
|---|---|---|---|
| **Transport** | Server → client, while the app is open | `ISignalTransport` (server), `ISignalConnection` (client) | Server-sent events, polling, long polling. Later: SignalR, silent push as a wake-up |
| **Store** | Keeps signals for catch-up after a gap | `ISignalStore` | In-memory, Postgres |
| **Bus** | Server → server, when more than one instance runs | `ISignalBus` | In-process, Postgres `LISTEN/NOTIFY`. Later: Redis if ever needed |
| **Delivery** (client) | When and how a handler is called | `SignalDelivery`, `ISignalDeliveryPolicy` | Immediate, debounce, batch, when visible, manual |

```
server code ─► IChatSignals (generated) ─► store.Append ─► bus.Announce ─► every instance ─► transport ─► client
client: connection ─► cursor + delivery policy (wait, merge) ─► IChatSignals handler ─► fetch
```

Chosen per environment:

```csharp
// Development and tests: one process, nothing to install
services.AddSignals(s => s.UseInMemory().UseServerSentEvents().UsePolling());

// Production with several instances
services.AddSignals(s => s.UsePostgres(connectionString)     // registers the store and the bus, separately replaceable
                          .UseServerSentEvents().UsePolling());

app.MapSignals("/signals");
```

---

# Contracts

A contract is an interface in a shared project, like a Viaduct service:

```csharp
[Signals("chat")]                                    // stored key prefix: named explicitly, the keys are stored
public interface IChatSignals
{
    [SignalTopic("chat.room/{roomId}")]
    ValueTask MessagesArrivedAsync(Guid roomId, MessagesArrived summary, CancellationToken cancellationToken = default);

    [SignalTopic("chat.room/{roomId}"), Ephemeral]
    ValueTask TypingAsync(Guid roomId, PersonReference who, CancellationToken cancellationToken = default);
}

public sealed record MessagesArrived(int Count, DateTimeOffset Last, PersonReference? From)
    : ISignalMergeable<MessagesArrived>
{
    public MessagesArrived Merge(MessagesArrived newer) => newer with { Count = Count + newer.Count };
}
```

Rules, checked by the generator:

- A method returns `ValueTask` and ends with a `CancellationToken`.
- Its simple parameters fill the topic's holes, by name; a hole no parameter fills fails the build, as do two payloads.
- At most one complex parameter: the **payload**, written with Subro.Json like every Viaduct body. No payload is fine
  ("something changed in room 12").
- The **type key** is the prefix plus the method name without `Async` (`chat.MessagesArrived`); `[SignalName]` overrides.
- `[Ephemeral]`: not stored, not replayed, only sent live (typing, presence).

What is generated:

- **Server**: an implementation of the interface that turns each call into an envelope and publishes it.
  `services.AddSignalPublisher<IChatSignals>()`; code injects `IChatSignals` and calls it after saving.
- **Client, app-wide**: the app implements the interface. `services.AddSignalHandler<IChatSignals, ChatSignalHandler>()`.
- **Client, per page**: typed subscriptions that end with the component,
  `signals.ChatSignals().OnMessagesArrived(roomId, (summary, ct) => ...)` returning an `IDisposable`.
- **Metadata**: type keys, topic templates and the JSON type information, so client and server cannot drift.

A handler that needs more than the payload (the signal's id, time, topic, or how many were merged) reads `SignalInfo.Current`,
which the dispatcher sets around the call. A handler that implements `ISignalResettable` is told when the client lost
track (see "Catching up"); a per-page subscription takes an `onReset` callback. Without either, a reset does nothing for
that handler.

An unknown type key is ignored by the client, and an unknown payload property is skipped, so an older app (a cached WASM
build, an un-updated phone) keeps working while the server adds signals.

---

# The envelope

What the engine moves, in `Viaduct.Core`:

| Field | |
|---|---|
| `Id` | The cursor: increasing, global. 0 for ephemeral signals |
| `Type` | The type key (`chat.MessagesArrived`) |
| `Topic` | The filled topic (`chat.room/0193…`) |
| `At` | When it was published |
| `Data` | The payload as UTF-8 JSON, opaque to the engine; may be empty |
| `Ephemeral` | Not stored, not replayed |

Payloads are small by design; `SignalOptions.MaxPayloadBytes` (default 4 KB) refuses larger ones when publishing, as a
bug in the calling code (`ArgumentException`).

---

# Topics

A topic is a string with dot-separated levels and `/` before an id: `notifications`, `user/{id}`, `chat.room/{id}`,
`record/{type}/{id}`. Templates come from the contracts.

- **Base topics** are the server's: `ISignalTopicResolver.GetTopicsAsync(user, cancellationToken)` when a connection
  opens. The default gives `user/{NameIdentifier}`; an application adds its own (Vesta: the person, their rooms, their
  clubs). When they change, the application publishes the built-in `viaduct.TopicsChanged` to the user's topic, and the
  connection resolves them again without reconnecting.
- **Page topics** are the client's, sent with the connection request (`?topic=…`, repeated). Each is checked by the
  `ISignalTopicAccess` rules registered for its template; a topic that is no base topic and that no rule allows is refused.
  A refused topic is reported in the stream's first event (`viaduct.connected`) or the poll's `refused`, not by ending the
  stream. A changed set of page topics (a page opened or closed) reconnects with the new list after a short pause (200 ms)
  to gather changes; the cursor makes that lossless.
- A topic that is added starts from now: whoever shows it loads it fresh.

---

# Transports

## Server-sent events (the default)

```
GET /signals?topic=…          Accept: text/event-stream     Last-Event-ID: 1234

id: 1235
event: chat.MessagesArrived
data: {"topic":"chat.room/…","at":"…","data":{"count":2,"from":{…}}}

: ping
```

- On connect: what is stored after `Last-Event-ID` for the connection's topics, then live.
- A comment line every 25 s (`HeartbeatInterval`) so proxies do not close an idle stream.
- The stream ends when the caller's access token expires (when it has one), so the client reconnects with a fresh one and
  the topics are checked again. With Vesta's BFF that means at most every 15 minutes.
- Written and read by `SignalWire` in Viaduct.Core, the same code on both sides (`Utf8JsonWriter`/`JsonDocument`, no
  package); the client reads the stream over `HttpClient`, which works the same in WASM and MAUI.

## Polling and long polling

`GET /signals?after=1235&topic=…` answers at once with `{ "signals": [...], "cursor": 1240, "reset": false }`;
`&wait=30` holds the request until something arrives or 30 seconds pass. Same cursor, same topics, so a handler cannot
tell how a signal came.

## On the client

`AddSignalClient(o => o.Connections(SignalConnections.ServerSentEvents, SignalConnections.Polling))`: an order of
connections to try. When SSE does not get through (a buffering proxy, a strict network), it falls back to the next and
tries the first again later. An app can choose differently: polling only when saving data, none at all in the background
on MAUI, where push takes over. `ISignalClient.State` (connected, reconnecting, offline, paused) and its event are what a
connection indicator shows.

## Later

- **SignalR** as another transport, for heavy two-way use (live scoring, a shared timer screen). Needs WebSockets through
  the BFF (YARP) or a direct connection.
- **Silent push** (FCM/APNs data messages, carrying only the id and type) to wake a MAUI app, which then catches up from
  its cursor. Visible pushes stay notifications through `Subro.Notifications`.
- **One connection per browser** instead of per tab (`BroadcastChannel` leader), if HTTP/1.1's six connections per
  origin ever bite. HTTP/2 makes it unnecessary.

---

# Store and bus

## Catching up

The client keeps the last id it handled. Reconnecting or polling asks for what came after it. When that id is older than
the oldest signal still stored, the server sends `reset` instead, and handlers reload what they show. Since the data is
fetched anyway, catching up and reloading end in the same state; catching up is only cheaper.

## Retention

Built in: a hosted service in `Viaduct.Server` removes what is older than `SignalOptions.Retention` (default 24 hours),
read through `IOptionsMonitor` at every run, so a change applies without a restart. An application binds it from
configuration (`Signals:Retention`) or feeds it from its own settings with an `IConfigureOptions<SignalOptions>`; Viaduct
does not depend on Subro's settings.

## In-memory

A ring buffer per process and an in-process bus. For development, tests and a single instance. Lost on restart, which a
client sees as a `reset`.

## Postgres (`Viaduct.Signals.Postgres`, Npgsql only)

- **Store**: a `viaduct_signals` table (`id bigserial`, `type`, `topic`, `at`, `data jsonb`, `xid xid8`), indexed on `id`
  and `topic`. Created by the package's own script or an EF migration the application adds; no EF dependency.
- **Ids commit out of order**: transaction 1 takes id 100, transaction 2 takes 101 and commits first. A reader must not
  move a cursor past 100 while it is still open. The store only hands out rows whose transaction is older than the oldest
  one still running (`xid < pg_snapshot_xmin(pg_current_snapshot())`), the usual outbox technique.
- **Bus**: after appending, `NOTIFY viaduct_signals`. Each instance keeps one dedicated connection on `LISTEN`, and on a
  notification reads what is new in one query and hands it to its own connections. An ephemeral signal travels in the
  `NOTIFY` itself (up to 8000 bytes) and touches no table.
- `LISTEN` needs a direct connection, not one through a pooler in transaction mode.

Store and bus are registered separately: Postgres as the store with another bus is possible.

## Publishing after the change

A signal must not be seen before the change it reports is saved, or the fetch finds the old data. Published after
`SaveChangesAsync`, that holds, but a crash between the save and the publish loses the signal (the client notices at its
next reload), and a save inside a longer transaction would send it before the commit. On PostgreSQL both are closed by
writing the signal in the save's transaction: `PostgresSignalDatabase.InTransaction` (built in the review, see "Built").

---

# Delivery on the client

Between the connection and the handlers sits the dispatcher.

- **`SignalDelivery`** per contract, type or topic: `Immediate`, `Debounce(time)`, `Batch(window)`, `WhenVisible`
  (held while the app is hidden, delivered when it shows), `Manual` (held until the page asks).
- **Defaults** as everywhere: an app-wide default (`Immediate`), a hint on the contract (`[SignalDelivery]`), an override at
  registration or subscription.
- **Conditions**: an `ISignalDeliveryPolicy` can choose differently, given an `ISignalClientEnvironment` the app feeds
  (visible, metered connection, data saving). Viaduct has no JavaScript; the browser one comes from Subro.RazorBase, the
  MAUI one from the app.
- **Merging while held**: signals with the same type and topic become one. A payload implementing
  `ISignalMergeable<T>` merges itself; otherwise the latest wins. `SignalInfo.Current.Count` says how many arrived.

---

# Packages

| Package | Gets |
|---|---|
| `Viaduct.Core` | Attributes (`[Signals]`, `[SignalTopic]`, `[Ephemeral]`, `[SignalName]`, `[SignalDelivery]`), the envelope, `ISignalMergeable<T>`, `ISignalResettable`, `SignalInfo`, the generator |
| `Viaduct.Server` | `AddSignals`, `MapSignals`, publisher, `ISignalStore`, `ISignalBus`, `ISignalTransport`, topic resolver and access, retention, in-memory store and bus, SSE and polling |
| `Viaduct.Client` | `AddSignalClient`, `ISignalClient`, `ISignalConnection` (SSE, polling), dispatcher, delivery, handlers and subscriptions |
| `Viaduct.Signals.Postgres` | Store and bus on Npgsql |
| Subro.RazorBase (not Viaduct) | Subscriptions tied to a component's life, the browser's `ISignalClientEnvironment`, a connection indicator |

Signals on a hosted server need `RequireAuthorization` by default, like the rest of an API; an application can open a
public topic deliberately.

---

# Build order

1. **Contracts and the engine without HTTP**: attributes, generator (publisher, handler wiring, subscriptions, metadata,
   diagnostics), envelope, in-memory store and bus, dispatcher with delivery and merging. Tested in one process.
2. **Server endpoint**: SSE, polling, long polling, topic resolver and access, retention.
3. **Client connections**: SSE with fallback to polling, cursor, reset, state.
4. **Postgres** store and bus, tested against a real database.
5. **Subro.RazorBase** helpers.
6. **Vesta**: the pass-through checks below, a resolver (person, rooms, clubs), a first contract: the notification count
   and "permissions changed".

# Built

## Step 1, 2026-11-16: contracts and the engine without HTTP

- **Viaduct.Core** (`Viaduct.Core/Signals`, namespace `Viaduct.Signals` like everything below): the attributes,
  `SignalEnvelope` (with `SignalEnvelope.Reset(cursor)`, type key `viaduct.reset`), `SignalDelivery`, `SignalTopicTemplate`
  and `SignalTopicValues`, `SignalContract`/`SignalContract<T>`/`SignalTypeInfo`, the registry `SignalContracts`,
  `ISignalPublisher`, `ISignalSubscriptions`, `ISignalMergeable<T>`, `ISignalResettable`, `SignalInfo`.
- **The generator** (`Viaduct.Generator/SignalsGenerator.cs`) writes, next to the interface:
  - `{Name}Contract` (`ITestChatSignals` → `TestChatSignalsContract`), with `Instance`, a `SignalTypeInfo` property per
    signal (`TestChatSignalsContract.Instance.MessagesArrived`), decoding, merging, calling a handler, and the publishing
    implementation. It registers itself from a module initializer; `SignalContracts.Get<T>()` runs that first, as Subro.Json
    does for its contexts.
  - `{Name}Subscriptions` and an extension `{Name}()` on `ISignalSubscriptions`:
    `subscriptions.TestChatSignals().OnMessagesArrived(roomId, (summary, ct) => …, onReset)`.
  - Diagnostics `VIADUCT500`–`513`: not a plain interface, bad prefix, a member that is not a method, not `ValueTask`, generic
    or `ref` methods, a bad topic, a hole no parameter fills, a simple parameter not in the topic, a hole of another type than
    `string`, `Guid`, `int`, `long`, `short` or `bool`, two payloads, a `CancellationToken` that is not last, two methods with
    one key, a debounce or batch without time; a missing `CancellationToken` is a warning.
- **Viaduct.Server** (`Viaduct/Signals`): `SignalPublisher`, `ISignalStore` + `SignalBatch`, `ISignalBus` +
  `ISignalBusSubscriber`, `InMemorySignalStore` (ids start from the time it was made, so a cursor from before a restart gets a
  reset), `InProcessSignalBus`, `SignalFanOut` with a `SignalListener` per client (catch-up after the cursor, then live, each
  once and in order; closes with `Overflowed` past `ListenerCapacity`, the cursor only moving past what was written),
  `SignalRetentionService` (`CleanAsync`, every 24th of the retention between a minute and an hour), `SignalOptions`,
  `AddSignals(s => s.UseInMemory()/UseStore<T>()/UseBus<T>()/Configure(...))`, `AddSignalPublisher<T>()`.
- **Viaduct.Client** (`Viaduct.Client/Signals`): `SignalDispatcher` (scoped; `ReceiveAsync` from a connection, `Cursor`,
  dropping what it had, holding and merging per kind and topic, timers on `TimeProvider`, `FlushAsync`, `HeldCount`,
  subscriptions with `SubscribedTopics`/`TopicsChanged`, reset to handlers and subscriptions, a failing handler logged),
  `SignalClientOptions` (`DefaultDelivery`, `Deliver<TContract>`, `Deliver(type)`), `SignalClientEnvironment` (set by the app),
  `ISignalDeliveryPolicy`, `AddSignalClient`, `AddSignalHandler<TContract, THandler>`.
- **Tests**: `Viaduct.Tests/SignalTests.cs` (22: contract, publisher, topics, merging, server engine, retention, overflow,
  client delivery kinds, subscriptions, reset) and `Viaduct.GeneratorTests/SignalsGeneratorTests.cs` (14, the diagnostics).
  All Viaduct suites pass (124 runtime, 90 generator on net8.0 and net10.0).

## Step 2, 2026-11-16: the endpoint and topics

- **`app.MapSignals("/signals")`** (`Viaduct/Signals/SignalEndpoints.cs`): one GET, a `RequestDelegate` (no reflection).
  With `Accept: text/event-stream` a stream: `viaduct.connected` first (`SignalConnected`: cursor and refused topics, its
  `id` the cursor), then each signal as an event (`id` = cursor, none for an ephemeral one; `event` = type key; `data` =
  the envelope), a reset as `viaduct.reset`, `: ping` every `HeartbeatInterval`; buffering off, `X-Accel-Buffering: no`.
  Without it a poll (`SignalPoll` JSON): no `after` gives the cursor to start from; `wait=` seconds (at most `MaxWait`)
  holds it until something arrives. The cursor comes from `Last-Event-ID`, else `?after=`; more than `MaxTopics` or a bad
  cursor is a 400. `RequireAuthorization()` unless `SignalOptions.RequireAuthorization` is off. The stream ends at the
  `exp` claim (`EndAtTokenExpiry`).
- **`SignalWire`** (Viaduct.Core): the envelope, poll and start as JSON both ways, and an event's text.
- **Topics**: `ISignalTopicResolver` (scoped, all add up; `UserSignalTopicResolver` gives `user/{id}` from
  `UserIdClaimTypes`), `ISignalTopicAccess` per template; `AddTopicResolver<T>()`, `AddTopicAccess<T>()`,
  `AllowTopic(template, (user, topic, ct) => …)`. Resolved in a scope of their own, as a stream outlives its request.
- **`IViaductSignals.TopicsChangedAsync(userId)`** (Viaduct.Core, generated by the same generator, registered by
  `AddSignals`) and `SignalTopics.User`/`ForUser`: a stream that gets it works its topics out again.
- **Found while testing**: a signal sent right after `TopicsChanged` was filtered with the old topics before the stream had
  the new ones. A listener opened with `holdAfterTopicsChanged` (the endpoint's) now holds what follows until
  `SetTopics`, and while catching up re-reads the store from the `TopicsChanged` on.
- **Tests**: `Viaduct.Tests/SignalEndpointTests.cs` (14: wire round trips, polls, long polls and their wait, streams with
  catch-up, reset, refused topics, `TopicsChanged`, heartbeat, token expiry, bad requests, authorization by default) and one
  more listener test. All Viaduct suites pass (139 runtime, 90 generator on net8.0 and net10.0); Vesta.Api builds against it.

## Step 3, 2026-11-16: the client's connections

- **`services.AddSignalConnection(http => http.BaseAddress = …, o => …)`** (`Viaduct.Client/Signals`): adds the client's
  side and the connection, and returns the `HttpClient`'s builder (named `Viaduct.Signals`, timeout off) for handlers such
  as `AddViaductAuthorization`. The app calls `ISignalClient.Start()` once it runs.
- **`ISignalConnection`** reports `SignalConnectionOpened` once (cursor, refused topics), then `SignalConnectionReceived`s:
  - `ServerSentEventsSignalConnection`: `Last-Event-ID`, the browser's streaming option for WebAssembly, read with
    `SignalWire.ReadEventsAsync`. Not getting through (`SignalConnectionException.Unsupported`): 404/405/406/415/501,
    another content type, or nothing within `ConnectTimeout` (10 s, a buffering proxy).
  - `PollingSignalConnection`: the first poll answers at once, then long polls (`PollWait`, 25 s); `PollWait` zero polls
    every `PollInterval`.
- **`SignalClient`** (`ISignalClient`: `State`, `ConnectionName`, `LastError`, `RefusedTopics`, `StateChanged`, `Start`,
  `StopAsync`, `Pause`, `Resume`):
  - starts from the dispatcher's cursor (`SignalDispatcher.StartAt` adopts where a connection starts);
  - a way that does not get through moves on at once; `FallbackAfterFailures` (2) other failures do too, except a 401 or
    403, which another way would get as well; the first way is tried again after `RetryPreferredAfter` (5 min);
  - waits double from a second up to `MaxBackoff` (60 s), ±`BackoffJitter`; a stream the server ends reconnects at once,
    unless it ended within two seconds;
  - page topic changes reconnect after `TopicsChangeDelay` (200 ms); `PauseWhenHidden` follows the environment's
    visibility; `MeteredConnections` is the order while metered.
- **Server**: a request whose token has expired already (let through by clock skew) now gets a 401 rather than a stream
  that ends at once, so the client gets a fresh token instead of reconnecting in a loop.
- **Tests**: `Viaduct.Tests/SignalConnectionTests.cs` (9, client against a test server: streams, page topics, refused
  streams and silent streams falling back to polling, a refused caller retried without moving on, reconnecting from the
  cursor after the server ends a stream, pause and resume with catch-up, pausing while hidden, stopping). All Viaduct suites
  pass (148 runtime, 90 generator on net8.0 and net10.0), stable over repeated runs; Vesta.Api and Vesta.Client.Shared build.
- **Not run**: in a browser (WASM streaming through `fetch`) or MAUI; only against ASP.NET Core's test server.

## Step 4, 2026-11-16: PostgreSQL

- **`Viaduct.Signals.Postgres`** (Npgsql 8.0.5, server-only, in `Viaduct.slnx`):
  `signals.UsePostgres(connectionString | dataSource, o => …)` registers `PostgresSignalStore` and `PostgresSignalBus`
  separately on a shared `PostgresSignalDatabase`.
- **Tables**: `viaduct_signals` (`id` identity, `xid bigint` defaulting to `pg_current_xact_id()`, `type`, `topic`, `at`,
  `data json` so the payload comes back as written) and `viaduct_signals_floor` (one row: the highest cursor removed).
  Created when missing at first use (`CreateSchema`, default on), or by the application from
  `PostgresSignalDatabase.CreateScript(options)`. Schema, table and channel names are options. Needs PostgreSQL 13+.
- **The cursor is not the row id.** As designed, ids commit out of order; the fix turned out to need the cursor itself to
  follow commit order: it is the writing transaction's id shifted left 20 bits, plus the row's place in its transaction
  (at most 1,048,575 signals per transaction). Reads only take transactions older than the oldest still running
  (`pg_snapshot_xmin`), ordered by transaction and id; the current cursor is "everything before that". A cursor below the
  floor, or from a transaction that has not started yet (another database), gets a reset. Removing takes whole
  transactions, so no cursor is left inside one half gone.
- **The bus**: `pg_notify` on the channel, empty for stored signals, the envelope for an ephemeral one (at most 7,999
  bytes). Each server keeps one connection on `LISTEN`, reads the store when notified, every `SafetyPollInterval` (2 s)
  regardless (for signals held back by an open transaction, and notifications missed), and after listening again; a lost
  connection is retried after `ReconnectDelay` (5 s).
- **Found while testing**: transaction ids are the server's, so an open transaction in *another database* on the same
  PostgreSQL server holds back signals too, until it ends. Correct, but signals want a server of the application's own
  and short transactions; noted on `PostgresSignalDatabase`. The tests run one at a time for it.
- **Tests**: `Viaduct.Tests/PostgresSignalTests.cs` (6, each on a database of its own, dropped after; skipped without a
  server; `VIADUCT_TEST_POSTGRES` for another than `postgres`/`postgres` on localhost): reading after a cursor by topic and
  page, a newer transaction's signal waiting for an older open one, one transaction's signals in order, retention with
  reset and a foreign cursor, the bus between two servers, and two full servers on one database hearing each other. All
  Viaduct suites pass (154 runtime, 90 generator on net8.0 and net10.0); Vesta.Api builds.
- **Not done**: the publish workflow (`.github/workflows/Viaduct.yml`) does not pack the new package yet; publishing it
  also needs a trusted publisher for its id on nuget.org.

## Step 5, 2026-11-16: components (Subro.Base)

- **`Subro.RazorBase.Signals`**, a project of its own beside Subro.RazorBase (as DevTools is), so the component library
  does not depend on Viaduct; in `Subro.Base.sln`.
  - **`SignalScope`**: a component's subscriptions. Each call goes through the component's `InvokeAsync` and is followed by
    `StateHasChanged`; `Clear()` to subscribe again after a parameter change, `Dispose()` with the component.
    `SignalScope.For(services, InvokeAsync, StateHasChanged)` subscribes to nothing in an application without signals or
    while prerendering on a server without them. The generated subscriptions work on it:
    `signals.ChatSignals().OnMessagesArrived(roomId, …)`. No base class, so a component keeps its own.
  - **`SSignalHost`**, once in the layout: on the first interactive render it tells `SignalClientEnvironment` whether the
    page is in view and whether data costs (`visibilitychange`, `navigator.connection`, `subro.signals.js`), then starts
    `ISignalClient`. `WatchBrowser` and `StartClient` turn either off.
  - **`SSignalStatus`**: a dot and the state's text (`SignalText`, `[LocalizationGroup("components.signals")]`), only
    while not connected unless `Show` or the setting `SignalStatusDefaults.Show` (`components.signals`, app level) says
    always; `DotOnly`, `Text` per state. Styles in `_content/Subro.RazorBase.Signals/subro.signals.css`, theme colours.
  - `AddSubroSignals()` registers the settings group for a settings page; the components work without it.
- **Viaduct.Client**: `SignalClientEnvironment` is now scoped like the dispatcher, so on Blazor Server each circuit has its
  own visibility.
- **Tests**: `Subro.RazorBase.Tests/SignalComponentsTests.cs` (9, bUnit: the scope through the renderer, dispose, clear,
  none without signals; the indicator by state and setting; the host with the browser module and starting the client).
  RazorBase suite 1,156 green; Viaduct 154 green.
- **Run in the browser**: the playground's `/signals` page (Blazor Server; its client connects to the same server's
  `/api/signals` over HTTP, so a real stream through Kestrel). Seen in the browser pane: connected over `sse`; a post
  arrives; five posts within the 400 ms debounce arrive as one call counting 5; another room's posts are not heard; an
  ephemeral typing signal arrives; Pause shows "Paused" and a post sent meanwhile arrives on Resume; switching rooms
  reconnects with the new page topic. No console or server errors. Not seen: a WebAssembly client in a browser (step 6,
  Vesta), MAUI.

## Step 6, 2026-11-16: Vesta

- **Contract**: `IVestaSignals` in `Vesta.Core.Services` (`[Signals("vesta")]`), today one signal:
  `PermissionsChangedAsync(Guid personId)` on `person/{personId}` (`VestaSignalTopics`). The project references
  Viaduct's generator as an analyzer, which a project reference does not bring by itself. *(Built in
  `Vesta.Contracts.Services`; moved with the rest of Vesta.Contracts into Vesta.Core on 2026-11-17.)*
- **API** (`Vesta.Api/Signals/VestaSignals.cs`): `AddVestaSignals()` (Postgres on Vesta's own connection, schema `signals`,
  created at first use; `CreateScript` now creates a schema other than `public` too), `MapVestaSignals()` at `/signals`
  behind a valid token. `VestaSignalTopicResolver` gives a signed-in caller `user/{vesta user id}` and
  `person/{person id}` (the default token-subject topic is off: a subject is the login provider's, not Vesta's user).
  `PermissionChangeSignals`, an EF interceptor added by `UseVestaSaveHooks` after the save hooks on every context that maps
  role assignments, sends `PermissionsChanged` for each person whose assignments a save added, changed or removed, once the
  save is done (the role that comes with a membership included); a failure to send is logged, the save stands.
- **Web host pass-through** (`ApiPassThrough`): passes `Last-Event-ID` on, and passes a `text/event-stream` answer on as
  it arrives, flushing each piece, buffering off, `X-Accel-Buffering: no`; the app leaving or the API going away ends it
  quietly.
- **App**: `AddVestaSignals(hostAddress, …)` in Vesta.Client.Shared (registered by Vesta.Wasm with the app's header):
  the connection to `/api/signals`, and `VestaSignalHandler`, which reloads the permissions. The layout has
  `<SSignalHost StartClient="@signedIn" />` (connects only for a signed-in user) and the header `<SSignalStatus DotOnly />`
  (only while not connected); the stylesheet is in Vesta.Web's `App.razor`. Vesta.Web's own prerendering registers no
  signals; MAUI has no API access yet.
- **`IPermissionBuffer.RefreshAsync`** (Vesta.Core): loads the permissions again keeping the held ones until the new ones
  are there, so nothing reads "not known" in between (`Invalidate` would close the page in view for a moment). Default
  interface implementation for other buffers.
- **Viaduct, from running it**: a server out of reach (no answer) no longer counts towards moving on from server-sent
  events to polling; only an error answer or a stream that stays silent does (`FallbackAfterFailures`). Seen when the
  servers were restarted: the app had moved to polling after two refused connections.
- **Tests**: `Vesta.Api.Tests/PermissionChangeSignalsTests.cs` (2, on the test database), `PermissionBufferTests` (+2),
  `SignalConnectionTests` (+2: errors move on, out of reach is waited out). Vesta.Api.Tests 446, Vesta.Client.Tests 196,
  Vesta.Web.Tests 22 green; Viaduct all green.
- **Run in the browser** (vesta-api + vesta-web, the WebAssembly app, signed in as `groups-check@vesta.test`): the app
  opens `GET /api/signals` through the web host (200, held open); giving the user a role on their own member page in Oslo
  BK (test) is followed by `GET /api/me/permissions` without anything else asking for it, and so is ending it again (the
  test data is as it was). While the servers were down the app kept trying, and after the restart (development keys, signed
  out) it got 401s and did not move on. A stream held open for over three minutes through the web host stayed the one
  connection (the pass-through's 100-second `HttpClient` timeout does not reach a body read after its headers) and still
  delivered the next role change.
- **Seen, not caused by signals, and fixed**: a call that returns nothing (`DELETE …/roles/{id}`) showed
  `net::ERR_ABORTED` with its 200 in the browser: Viaduct's generated client disposed the response without reading its body,
  which aborts the browser's streamed fetch. The generated client now reads such an answer to its end first
  (`ViaductClientBase.DrainResponse`; test `Client_ReadsTheAnswerToItsEnd_ForACallThatReturnsNothing`), and the browser shows
  a plain 200.

## Review, 2026-11-16

A review pass over the engine, the client and Vesta's part, after all six steps; what it found is fixed and tested:

- **A fan-out whose store was out of reach at its start stayed broken**: the failed start was kept. It is now tried again
  by the next listener or announcement, and subscribes to the bus once (`AFanOutWhoseStoreWasOutOfReach_StartsWhenItIsBack`).
- **A reconnect cancelled a handler halfway**: signals were handed on with the connection's token, so a page opening
  another room while permissions were being reloaded cancelled the reload. Handlers now get the client's lifetime
  (`AReconnect_DoesNotCancelAHandlerHalfway`).
- **A silent stream could leave the client waiting for ever**: when the connect timeout cut it, the server could end the
  response first, and an SSE stream that ended before saying it was connected counted as an ordinary end. It now counts as
  saying nothing (after the wait) or as a failed answer (before), so the client moves on to polling. Found as a test that
  failed one run in three.
- **Vesta's interceptor kept its state per instance**, shared by two contexts of one type in a scope; now per context.
- Test waits that could mistake the connect timeout's timer for the wait between attempts.

And the three points open after step 6:

- **Signals in the save's transaction**: `PostgresSignalDatabase.InTransaction(connection, transaction, options)` gives a
  publisher (`PostgresTransactionSignals`) that writes the row and the `NOTIFY` in an application's transaction, so they
  go out on commit and not at all on rollback; `SignalPublisher.Prepare` checks and stamps for it. Vesta's
  `PermissionChangeSignals` uses it: in the caller's transaction when there is one, else in one it begins for the save,
  under a savepoint so a failure to signal never undoes the save; publishing after the save is left for another store, an
  ambient `TransactionScope` or a retrying execution strategy. Tests: rollback takes the signal along, commit carries it, a
  broken signal table leaves the save standing.
- **What someone may hear follows their roles**: `SignalOptions.ResolveTopicsAgainOn` (Viaduct's `TopicsChanged` and
  whatever an application adds, `signals.ResolveTopicsAgainOn(type)`) replaces the fixed `TopicsChanged`; Vesta adds
  `PermissionsChanged`, so a stream works its topics out again, and checks its page topics again, when the user's roles
  change. Test: `AnApplicationsOwnSignal_CanHaveTheTopicsWorkedOutAgain`.
- **JSON for signal payloads**: `VestaJsonContractsTests.EverySignalPayload_IsRegistered` checks every `[Signals]`
  contract's payload types against the registered contracts, as for the services.

## Clever Cloud, still to confirm

Public documentation does not settle these; ask Clever Cloud before going live:

- **Sōzu** (their reverse proxy): idle timeout for a long response and whether it buffers one. The 25-second heartbeat
  keeps a stream from being idle; the client falls back to polling if events do not arrive (a buffering proxy).
- **PostgreSQL add-on**: whether its connection is direct or through a proxy or pooler that drops `LISTEN` (transaction
  pooling does). If it does, the bus needs a direct connection string of its own; the safety poll (2 s) keeps signals
  arriving meanwhile, later.

Not done, or different from the design above:

- No startup list of templates without an access rule: a page topic no rule allows is simply refused.
- Polling answers stored signals; an ephemeral one only when it arrives during a long poll's wait.

Left for later steps, or noticed while building:

- Payload types are not added to a JSON context by the generator. An AOT client needs them in its own context, as with
  every Viaduct body.
- The dispatcher calls handlers after `ConfigureAwait(false)`: a Blazor Server component must use `InvokeAsync`. The
  RazorBase helper (step 5) will do that.
- A handler must not wait for another signal to be delivered: delivery is one at a time.

# To check before Vesta uses it

- Clever Cloud's proxy: does it stream without buffering, what is its idle timeout, does it serve HTTP/2.
- Vesta.Web's `/api` pass-through on a stream: no buffering, and whether the `HttpClient`'s 100-second timeout cuts it off.
- WASM response streaming (on by default from .NET 10, to confirm) so SSE arrives event by event.
- Whether Clever Cloud's PostgreSQL gives a direct connection for `LISTEN`.

# Later

- Counters for listeners, overflows and lag, once it runs for real.
- SignalR transport, silent push carriers, a Redis bus, one connection per browser.
- Admin view of the store (what was sent, to which topics) for support.
