using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>The names of the connections Viaduct has, for <see cref="SignalClientOptions.Connections"/>.</summary>
    public static class SignalConnectionNames
    {
        /// <summary>Server-sent events: one long request the server writes to.</summary>
        public const string ServerSentEvents = "sse";

        /// <summary>Polling, long polling by default: repeated requests.</summary>
        public const string Polling = "polling";
    }

    /// <summary>What a connection asks the server for: where to continue, and the page topics besides the base topics.</summary>
    /// <param name="After">The cursor to continue after; null to start from now.</param>
    /// <param name="Topics">The page topics.</param>
    public sealed record SignalConnectRequest(long? After, IReadOnlyCollection<string> Topics);

    // .NET 11: a closed union of Opened and Received.
    /// <summary>What a connection reports: <see cref="SignalConnectionOpened"/> once, then <see cref="SignalConnectionReceived"/>s.</summary>
    public abstract record SignalConnectionEvent;

    /// <summary>The connection got through: where it starts, and which page topics were refused.</summary>
    public sealed record SignalConnectionOpened(long Cursor, IReadOnlyList<string> Refused) : SignalConnectionEvent;

    /// <summary>A signal arrived.</summary>
    public sealed record SignalConnectionReceived(SignalEnvelope Signal) : SignalConnectionEvent;

    /// <summary>
    /// One way for a client to hear signals: server-sent events, polling, later others. Runs until the server ends it or it
    /// fails; the <see cref="SignalClient"/> reconnects, and moves on to the next way when one does not get through.
    /// </summary>
    public interface ISignalConnection
    {
        /// <summary>Its name in <see cref="SignalClientOptions.Connections"/>.</summary>
        string Name { get; }

        /// <summary>
        /// Connects and reports what arrives. Ends when the server ends it; throws when it fails, with a
        /// <see cref="SignalConnectionException"/> whose <see cref="SignalConnectionException.Unsupported"/> says this way
        /// does not get through at all.
        /// </summary>
        IAsyncEnumerable<SignalConnectionEvent> RunAsync(SignalConnectRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>A connection failed. <see cref="Unsupported"/>: this way does not get through, try the next.</summary>
    public sealed class SignalConnectionException(string message, int? statusCode = null, bool unsupported = false, Exception? inner = null)
        : ViaductException(message, inner!)
    {
        /// <summary>The HTTP status the server answered with, when it answered.</summary>
        public int? StatusCode { get; } = statusCode;

        /// <summary>This way of connecting does not get through: the next in the order should be tried.</summary>
        public bool Unsupported { get; } = unsupported;
    }

    /// <summary>
    /// Server-sent events over <see cref="HttpClient"/>: one request, read as the server writes. Counts as not getting
    /// through when it is answered with something else, or says nothing within <see cref="SignalClientOptions.ConnectTimeout"/>
    /// (a proxy that buffers).
    /// </summary>
    public sealed class ServerSentEventsSignalConnection(IHttpClientFactory clients, IOptionsMonitor<SignalClientOptions> options, TimeProvider time)
        : ISignalConnection
    {
        /// <summary>The option that makes a browser's <c>fetch</c> stream the response, which Blazor WebAssembly reads.</summary>
        static readonly HttpRequestOptionsKey<bool> BrowserStreaming = new("WebAssemblyEnableStreamingResponse");

        /// <inheritdoc/>
        public string Name => SignalConnectionNames.ServerSentEvents;

        /// <inheritdoc/>
        public async IAsyncEnumerable<SignalConnectionEvent> RunAsync(SignalConnectRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var settings = options.CurrentValue;

            using var connecting = new CancellationTokenSource(settings.ConnectTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connecting.Token);
            var token = linked.Token;

            using var message = new HttpRequestMessage(HttpMethod.Get, SignalUrls.Build(settings.Path, request.Topics, null, null));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SignalWire.EventStreamType));
            message.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            if (request.After is long after)
                message.Headers.TryAddWithoutValidation(SignalWire.LastEventIdHeader, after.ToString(CultureInfo.InvariantCulture));
            message.Options.Set(BrowserStreaming, true);

            using var response = await SendAsync(message, token, connecting, cancellationToken).ConfigureAwait(false);
            await SignalUrls.EnsureSuccessAsync(response, token).ConfigureAwait(false);
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, SignalWire.EventStreamType, StringComparison.OrdinalIgnoreCase))
                throw new SignalConnectionException($"The signal endpoint answered with '{response.Content.Headers.ContentType?.MediaType}', not a stream.", (int)response.StatusCode, unsupported: true);

            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var events = SignalWire.ReadEventsAsync(reader, token).GetAsyncEnumerator(token);
            try
            {
                var opened = false;
                while (true)
                {
                    SignalWireEvent? next;
                    try
                    {
                        next = await events.MoveNextAsync().ConfigureAwait(false) ? events.Current : null;
                    }
                    catch (Exception exception) when (!opened && connecting.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                        && exception is OperationCanceledException or IOException or HttpRequestException)
                    {
                        // However the waiting ended (cancelled, or the connection dropped with it), nothing came in time.
                        throw NothingArrived(settings);
                    }

                    if (next is null)
                    {
                        if (opened)
                            yield break;

                        // Ended without saying it was connected: after the wait, as said nothing; before, as a failed answer.
                        if (connecting.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                            throw NothingArrived(settings);
                        throw new SignalConnectionException("The signal stream ended before it started.", (int)response.StatusCode);
                    }

                    if (!opened)
                    {
                        if (next.Type != SignalWire.ConnectedEvent)
                            throw new SignalConnectionException($"A signal stream must start with '{SignalWire.ConnectedEvent}', not '{next.Type}'.", unsupported: true);
                        opened = true;
                        connecting.CancelAfter(Timeout.InfiniteTimeSpan);
                        var connected = SignalWire.ConnectedFromJson(next.Data);
                        yield return new SignalConnectionOpened(connected.Cursor, connected.Refused);
                        continue;
                    }

                    if (next.Type != SignalWire.ConnectedEvent)
                        yield return new SignalConnectionReceived(SignalWire.FromJson(next.Data));
                }
            }
            finally
            {
                await events.DisposeAsync().ConfigureAwait(false);
            }
        }

        async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token, CancellationTokenSource connecting, CancellationToken cancellationToken)
        {
            try
            {
                return await clients.CreateClient(SignalClientRegistration.HttpClientName)
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connecting.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw NothingArrived(options.CurrentValue);
            }
            catch (HttpRequestException exception)
            {
                throw new SignalConnectionException("The signal endpoint could not be reached.", (int?)exception.StatusCode, inner: exception);
            }
        }

        static SignalConnectionException NothingArrived(SignalClientOptions settings)
            => new($"The signal stream said nothing within {settings.ConnectTimeout}: something in between holds it back.", unsupported: true);
    }

    /// <summary>
    /// Polling: repeated requests, each answering what was stored after the cursor. Long polling by default
    /// (<see cref="SignalClientOptions.PollWait"/>), else every <see cref="SignalClientOptions.PollInterval"/>.
    /// </summary>
    public sealed class PollingSignalConnection(IHttpClientFactory clients, IOptionsMonitor<SignalClientOptions> options, TimeProvider time)
        : ISignalConnection
    {
        /// <summary>How long a poll may take beyond the server's wait before it counts as failed.</summary>
        static readonly TimeSpan Slack = TimeSpan.FromSeconds(30);

        /// <inheritdoc/>
        public string Name => SignalConnectionNames.Polling;

        /// <inheritdoc/>
        public async IAsyncEnumerable<SignalConnectionEvent> RunAsync(SignalConnectRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var cursor = request.After;
            var opened = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var settings = options.CurrentValue;

                // The first poll answers at once, so the connection is known to work; then it waits on the server.
                var wait = opened && cursor is not null ? settings.PollWait : TimeSpan.Zero;
                var poll = await PollAsync(SignalUrls.Build(settings.Path, request.Topics, cursor, wait), wait + Slack, cancellationToken).ConfigureAwait(false);

                if (!opened)
                {
                    opened = true;
                    yield return new SignalConnectionOpened(request.After ?? poll.Cursor, poll.Refused);
                }

                if (poll.Reset)
                    yield return new SignalConnectionReceived(SignalEnvelope.Reset(poll.Cursor));
                else
                    foreach (var signal in poll.Signals)
                        yield return new SignalConnectionReceived(signal);

                cursor = poll.Cursor;
                if (poll.More)
                    continue;
                if (settings.PollWait <= TimeSpan.Zero)
                    await Task.Delay(settings.PollInterval, time, cancellationToken).ConfigureAwait(false);
            }
        }

        async Task<SignalPoll> PollAsync(string url, TimeSpan limit, CancellationToken cancellationToken)
        {
            using var timeout = new CancellationTokenSource(limit, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                using var response = await clients.CreateClient(SignalClientRegistration.HttpClientName)
                    .GetAsync(url, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
                await SignalUrls.EnsureSuccessAsync(response, linked.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    return SignalWire.PollFromJson(text);
                }
                catch (FormatException exception)
                {
                    throw new SignalConnectionException("The signal endpoint did not answer with a poll.", (int)response.StatusCode, unsupported: true, exception);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new SignalConnectionException($"A poll got no answer within {limit}.");
            }
            catch (HttpRequestException exception)
            {
                throw new SignalConnectionException("The signal endpoint could not be reached.", (int?)exception.StatusCode, inner: exception);
            }
        }
    }

    /// <summary>The endpoint's address and answers, the same for every connection.</summary>
    static class SignalUrls
    {
        /// <summary>Statuses that mean the way of connecting is not served here, rather than that something went wrong.</summary>
        static readonly HashSet<HttpStatusCode> Unsupported =
            [HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotAcceptable, HttpStatusCode.UnsupportedMediaType, HttpStatusCode.NotImplemented];

        public static string Build(string path, IReadOnlyCollection<string> topics, long? after, TimeSpan? wait)
        {
            var url = new StringBuilder(path);
            var separator = path.Contains('?') ? '&' : '?';
            void Add(string name, string value)
            {
                url.Append(separator).Append(name).Append('=').Append(Uri.EscapeDataString(value));
                separator = '&';
            }

            foreach (var topic in topics.OrderBy(static t => t, StringComparer.Ordinal))
                Add("topic", topic);
            if (after is long cursor)
                Add("after", cursor.ToString(CultureInfo.InvariantCulture));
            if (wait is { } time && time > TimeSpan.Zero)
                Add("wait", ((int)Math.Ceiling(time.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
            return url.ToString();
        }

        public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;

            string? body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Length > 500)
                    body = body.Substring(0, 500);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
            }

            throw new SignalConnectionException(
                $"The signal endpoint answered {(int)response.StatusCode} {response.ReasonPhrase}{(string.IsNullOrEmpty(body) ? "" : ": " + body)}",
                (int)response.StatusCode, Unsupported.Contains(response.StatusCode));
        }
    }
}
