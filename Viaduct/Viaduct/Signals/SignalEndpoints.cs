using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>Mapping the signal endpoint.</summary>
    public static class SignalEndpoints
    {
        /// <summary>
        /// Maps the signal endpoint, one GET for every transport:
        /// <list type="bullet">
        /// <item>with <c>Accept: text/event-stream</c>, a stream of server-sent events, starting after <c>Last-Event-ID</c>
        /// (or <c>?after=</c>), live until the client leaves or its token expires;</item>
        /// <item>otherwise a poll: <c>?after=</c> answers what was stored after it as JSON (<see cref="SignalPoll"/>), and
        /// <c>&amp;wait=</c> seconds holds it until something arrives. Without <c>after</c>, the cursor to start from.</item>
        /// </list>
        /// <c>?topic=</c>, repeated, asks for page topics besides the user's base topics.
        /// Requires authorization unless <see cref="SignalOptions.RequireAuthorization"/> is off.
        /// </summary>
        public static IEndpointConventionBuilder MapSignals(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string pattern = "/signals")
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            var builder = endpoints.MapGet(pattern, Handle);
            if (endpoints.ServiceProvider.GetRequiredService<IOptions<SignalOptions>>().Value.RequireAuthorization)
                builder.RequireAuthorization();
            return builder;
        }

        static Task Handle(HttpContext context) => context.RequestServices.GetRequiredService<SignalEndpointHandler>().HandleAsync(context);
    }

    /// <summary>
    /// Serves the signal endpoint: works out the caller's topics, then streams or answers a poll. See
    /// <see cref="SignalEndpoints.MapSignals"/>.
    /// </summary>
    public sealed class SignalEndpointHandler(SignalFanOut fanOut, ISignalStore store, IServiceScopeFactory scopes,
        IOptionsMonitor<SignalOptions> options, TimeProvider time, ILogger<SignalEndpointHandler>? logger = null)
    {
        readonly ILogger logger = logger ?? NullLogger<SignalEndpointHandler>.Instance;

        /// <summary>Answers one request.</summary>
        public async Task HandleAsync(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var settings = options.CurrentValue;
            var request = context.Request;

            var requested = request.Query["topic"].Where(static t => !string.IsNullOrEmpty(t)).Select(static t => t!)
                .Distinct(StringComparer.Ordinal).ToList();
            if (requested.Count > settings.MaxTopics)
            {
                await BadRequest(context, $"At most {settings.MaxTopics} topics can be asked for.");
                return;
            }

            long? after = null;
            var cursorText = request.Headers[SignalWire.LastEventIdHeader].ToString();
            if (cursorText.Length == 0)
                cursorText = request.Query["after"].ToString();
            if (cursorText.Length > 0)
            {
                if (!long.TryParse(cursorText, NumberStyles.None, CultureInfo.InvariantCulture, out var cursor))
                {
                    await BadRequest(context, "The cursor is not a number.");
                    return;
                }
                after = cursor;
            }

            // A token past its expiry that authentication still let through (clock skew) would end a stream at once and
            // have the client reconnect in a loop: a 401 has it get a fresh one first.
            if (settings.EndAtTokenExpiry && ExpiresAt(context.User) is { } expires && expires <= time.GetUtcNow())
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var streaming = request.Headers.Accept.Any(static accept => accept?.Contains(SignalWire.EventStreamType, StringComparison.OrdinalIgnoreCase) == true);
            if (streaming)
                await StreamAsync(context, after, requested, settings);
            else
                await PollAsync(context, after, requested, settings);
        }

        async Task StreamAsync(HttpContext context, long? after, List<string> requested, SignalOptions settings)
        {
            var response = context.Response;
            using var expiry = ExpiryOf(context.User, settings);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, expiry?.Token ?? CancellationToken.None);
            var token = lifetime.Token;

            try
            {
                var (topics, refused) = await ResolveAsync(context.User, requested, token);

                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = SignalWire.EventStreamType;
                response.Headers.CacheControl = "no-cache";
                response.Headers["X-Accel-Buffering"] = "no";
                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

                var resolveAgainOn = (IReadOnlySet<string>)settings.ResolveTopicsAgainOn;
                using var listener = await fanOut.ListenAsync(after, topics, resolveAgainOn, token);
                using var writing = new SemaphoreSlim(1, 1);

                async Task Send(string text, CancellationToken cancellationToken)
                {
                    await writing.WaitAsync(cancellationToken);
                    try
                    {
                        await response.WriteAsync(text, cancellationToken);
                        await response.Body.FlushAsync(cancellationToken);
                    }
                    finally
                    {
                        writing.Release();
                    }
                }

                await Send(SignalWire.FormatEvent(SignalWire.ConnectedEvent, SignalWire.ToJson(new SignalConnected(listener.Cursor, refused)), listener.Cursor), token);

                using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var heartbeat = HeartbeatAsync(Send, settings.HeartbeatInterval, heartbeatStop.Token);
                try
                {
                    await foreach (var signal in listener.ReadAllAsync(token))
                    {
                        if (resolveAgainOn.Contains(signal.Type))
                            listener.SetTopics((await ResolveAsync(context.User, requested, token)).Topics);

                        await Send(SignalWire.FormatEvent(signal), token);
                    }
                }
                finally
                {
                    heartbeatStop.Cancel();
                    try
                    {
                        await heartbeat;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                if (listener.Overflowed)
                    logger.LogInformation("A signal stream fell behind and was closed at {Cursor}; its client reconnects.", listener.Cursor);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The client left, or its token expired: it reconnects with its cursor.
            }
            catch (IOException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
        }

        async Task HeartbeatAsync(Func<string, CancellationToken, Task> send, TimeSpan interval, CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(interval, time);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await send(": ping\n\n", cancellationToken);
        }

        async Task PollAsync(HttpContext context, long? after, List<string> requested, SignalOptions settings)
        {
            var aborted = context.RequestAborted;
            var (topics, refused) = await ResolveAsync(context.User, requested, aborted);

            if (after is not long from)
            {
                await Answer(context, new SignalPoll(await store.GetCursorAsync(aborted), false, false, [], refused));
                return;
            }

            var batch = await store.ReadAsync(from, topics, SignalFanOut.PageSize, aborted);
            if (batch.Reset)
            {
                await Answer(context, new SignalPoll(batch.Cursor, true, false, [], refused));
                return;
            }

            var signals = batch.Signals.ToList();
            var cursor = batch.Cursor;
            var more = batch.More;

            var wait = Wait(context.Request, settings);
            if (signals.Count == 0 && wait > TimeSpan.Zero)
            {
                SignalEnvelope? arrived = null;
                using (var waiting = new CancellationTokenSource(wait, time))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, waiting.Token))
                using (var listener = await fanOut.ListenAsync(cursor, topics, aborted))
                {
                    try
                    {
                        await foreach (var signal in listener.ReadAllAsync(linked.Token))
                        {
                            arrived = signal;
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                    {
                        // Nothing arrived in time.
                    }
                }

                if (arrived is not null)
                {
                    if (arrived.IsReset)
                    {
                        await Answer(context, new SignalPoll(arrived.Id, true, false, [], refused));
                        return;
                    }
                    if (arrived.Ephemeral)
                        signals.Add(arrived);

                    // What arrived, and whatever came with it, from the store.
                    var again = await store.ReadAsync(cursor, topics, SignalFanOut.PageSize, aborted);
                    if (again.Reset)
                    {
                        await Answer(context, new SignalPoll(again.Cursor, true, false, [], refused));
                        return;
                    }
                    signals.AddRange(again.Signals);
                    cursor = again.Cursor;
                    more = again.More;
                }
            }

            await Answer(context, new SignalPoll(cursor, false, more, signals, refused));
        }

        static TimeSpan Wait(HttpRequest request, SignalOptions settings)
        {
            if (!int.TryParse(request.Query["wait"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                return TimeSpan.Zero;
            var wait = TimeSpan.FromSeconds(seconds);
            return wait > settings.MaxWait ? settings.MaxWait : wait;
        }

        /// <summary>
        /// The caller's topics: the base topics of every resolver, and each topic asked for that is one of them or that an
        /// access rule allows. Resolved in a scope of its own, as a stream outlives its request's services.
        /// </summary>
        async ValueTask<(HashSet<string> Topics, List<string> Refused)> ResolveAsync(ClaimsPrincipal user, List<string> requested, CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;

            var topics = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resolver in services.GetServices<ISignalTopicResolver>())
                topics.UnionWith(await resolver.GetTopicsAsync(user, cancellationToken));

            var refused = new List<string>();
            if (requested.Count == 0)
                return (topics, refused);

            var rules = services.GetServices<ISignalTopicAccess>().ToList();
            foreach (var topic in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (topics.Contains(topic))
                    continue;

                var allowed = false;
                foreach (var rule in rules)
                {
                    if (rule.Template.TryMatch(topic, out _) && await rule.CanListenAsync(user, topic, cancellationToken))
                    {
                        allowed = true;
                        break;
                    }
                }

                if (allowed)
                    topics.Add(topic);
                else
                    refused.Add(topic);
            }
            return (topics, refused);
        }

        /// <summary>Ends a stream when the caller's token expires: a token source that cancels then, or null without one.</summary>
        CancellationTokenSource? ExpiryOf(ClaimsPrincipal user, SignalOptions settings)
        {
            if (!settings.EndAtTokenExpiry || ExpiresAt(user) is not { } expires)
                return null;

            var left = expires - time.GetUtcNow();
            return new CancellationTokenSource(left > TimeSpan.Zero ? left : TimeSpan.Zero, time);
        }

        /// <summary>When the caller's access token expires, from its <c>exp</c> claim; null without one.</summary>
        static DateTimeOffset? ExpiresAt(ClaimsPrincipal user)
            => user.FindFirst("exp")?.Value is { } exp && long.TryParse(exp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

        static async Task Answer(HttpContext context, SignalPoll poll)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(SignalWire.ToJson(poll), context.RequestAborted);
        }

        static async Task BadRequest(HttpContext context, string message)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(message, context.RequestAborted);
        }
    }
}
