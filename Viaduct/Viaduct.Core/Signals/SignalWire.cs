using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Viaduct.Signals
{
    /// <summary>What a polling request answers: signals after the cursor asked for, or a reset.</summary>
    /// <param name="Cursor">Where to poll on from.</param>
    /// <param name="Reset">What came after the cursor asked for is gone: load again, and continue from <paramref name="Cursor"/>.</param>
    /// <param name="More">There is more: poll again at once.</param>
    /// <param name="Signals">The signals, oldest first.</param>
    /// <param name="Refused">Topics asked for that this user may not hear.</param>
    public sealed record SignalPoll(long Cursor, bool Reset, bool More, IReadOnlyList<SignalEnvelope> Signals, IReadOnlyList<string> Refused);

    /// <summary>What a server-sent events stream says first: where it starts, and which topics asked for were refused.</summary>
    public sealed record SignalConnected(long Cursor, IReadOnlyList<string> Refused);

    /// <summary>One server-sent event as read: its id when it had one, its type, and its data.</summary>
    public sealed record SignalWireEvent(string? Id, string Type, string Data);

    /// <summary>
    /// How signals travel between server and client, written and read by the same code on both sides. JSON in camelCase;
    /// a signal's payload is embedded as it is.
    /// </summary>
    /// <remarks>
    /// Server-sent events: each signal is an event whose <c>id</c> is its cursor (none for an ephemeral one), whose
    /// <c>event</c> is its type key, and whose <c>data</c> is <see cref="ToJson(SignalEnvelope)"/>. The stream starts with a
    /// <see cref="ConnectedEvent"/>, and a reset is the event <see cref="SignalEnvelope.ResetType"/>.
    /// </remarks>
    public static class SignalWire
    {
        /// <summary>The event that starts a stream: <see cref="SignalConnected"/>.</summary>
        public const string ConnectedEvent = "viaduct.connected";

        /// <summary>The media type of a server-sent events stream.</summary>
        public const string EventStreamType = "text/event-stream";

        /// <summary>The header a reconnecting client sends its cursor in.</summary>
        public const string LastEventIdHeader = "Last-Event-ID";

        /// <summary>Writes a signal as a JSON object.</summary>
        public static void Write(Utf8JsonWriter writer, SignalEnvelope signal)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(signal);

            writer.WriteStartObject();
            writer.WriteNumber("id", signal.Id);
            writer.WriteString("type", signal.Type);
            writer.WriteString("topic", signal.Topic);
            if (signal.At != default)
                writer.WriteString("at", signal.At);
            if (signal.Data is not null)
            {
                writer.WritePropertyName("data");
                writer.WriteRawValue(signal.Data, skipInputValidation: false);
            }
            if (signal.Ephemeral)
                writer.WriteBoolean("ephemeral", true);
            writer.WriteEndObject();
        }

        /// <summary>A signal as JSON.</summary>
        public static string ToJson(SignalEnvelope signal) => Json(writer => Write(writer, signal));

        /// <summary>A signal from its JSON.</summary>
        /// <exception cref="FormatException">The text is no signal.</exception>
        public static SignalEnvelope FromJson(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            try
            {
                using var document = JsonDocument.Parse(json);
                return Read(document.RootElement);
            }
            catch (JsonException exception)
            {
                throw new FormatException("This is not a signal.", exception);
            }
        }

        /// <summary>A signal from a parsed JSON object.</summary>
        /// <exception cref="FormatException">The element is no signal.</exception>
        public static SignalEnvelope Read(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !element.TryGetProperty("topic", out var topic) || topic.ValueKind != JsonValueKind.String)
                throw new FormatException("A signal needs a type and a topic.");

            return new SignalEnvelope(type.GetString()!, topic.GetString()!,
                element.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null ? data.GetRawText() : null)
            {
                Id = element.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : 0,
                At = element.TryGetProperty("at", out var at) && at.TryGetDateTimeOffset(out var time) ? time : default,
                Ephemeral = element.TryGetProperty("ephemeral", out var ephemeral) && ephemeral.ValueKind == JsonValueKind.True,
            };
        }

        /// <summary>A polling answer as JSON.</summary>
        public static string ToJson(SignalPoll poll)
        {
            ArgumentNullException.ThrowIfNull(poll);
            return Json(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("cursor", poll.Cursor);
                if (poll.Reset)
                    writer.WriteBoolean("reset", true);
                if (poll.More)
                    writer.WriteBoolean("more", true);
                writer.WriteStartArray("signals");
                foreach (var signal in poll.Signals)
                    Write(writer, signal);
                writer.WriteEndArray();
                WriteRefused(writer, poll.Refused);
                writer.WriteEndObject();
            });
        }

        /// <summary>A polling answer from its JSON.</summary>
        /// <exception cref="FormatException">The text is no polling answer.</exception>
        public static SignalPoll PollFromJson(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var signals = new List<SignalEnvelope>();
                if (root.TryGetProperty("signals", out var array) && array.ValueKind == JsonValueKind.Array)
                    foreach (var element in array.EnumerateArray())
                        signals.Add(Read(element));

                return new SignalPoll(Cursor(root), Flag(root, "reset"), Flag(root, "more"), signals, Refused(root));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new FormatException("This is not a polling answer.", exception);
            }
        }

        /// <summary>The start of a stream as JSON.</summary>
        public static string ToJson(SignalConnected connected)
        {
            ArgumentNullException.ThrowIfNull(connected);
            return Json(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("cursor", connected.Cursor);
                WriteRefused(writer, connected.Refused);
                writer.WriteEndObject();
            });
        }

        /// <summary>The start of a stream from its JSON.</summary>
        /// <exception cref="FormatException">The text is no start of a stream.</exception>
        public static SignalConnected ConnectedFromJson(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            try
            {
                using var document = JsonDocument.Parse(json);
                return new SignalConnected(Cursor(document.RootElement), Refused(document.RootElement));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new FormatException("This is not the start of a signal stream.", exception);
            }
        }

        /// <summary>
        /// One server-sent event: its <c>id</c> when given, its <c>event</c>, and its <c>data</c> (one line per line of it),
        /// ending with the blank line that closes it.
        /// </summary>
        public static string FormatEvent(string eventType, string data, long? id = null)
        {
            ArgumentNullException.ThrowIfNull(eventType);
            ArgumentNullException.ThrowIfNull(data);

            var text = new StringBuilder();
            if (id is long value)
                text.Append("id: ").Append(value).Append('\n');
            text.Append("event: ").Append(eventType).Append('\n');
            foreach (var line in data.Split('\n'))
                text.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
            return text.Append('\n').ToString();
        }

        /// <summary>A signal as a server-sent event.</summary>
        public static string FormatEvent(SignalEnvelope signal)
        {
            ArgumentNullException.ThrowIfNull(signal);
            return FormatEvent(signal.Type, ToJson(signal), signal.IsReset || !signal.Ephemeral ? signal.Id : null);
        }

        /// <summary>
        /// The server-sent events in a stream as they arrive, until it ends. Comments (heartbeats) are skipped; an event's
        /// data lines are joined with a line break.
        /// </summary>
        public static async IAsyncEnumerable<SignalWireEvent> ReadEventsAsync(TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(reader);

            string? id = null, type = null;
            var data = new StringBuilder();
            var hasData = false;

            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    yield break;

                if (line.Length == 0)
                {
                    if (hasData || type is not null)
                        yield return new SignalWireEvent(id, type ?? "message", data.ToString());
                    id = type = null;
                    data.Clear();
                    hasData = false;
                    continue;
                }

                if (line[0] == ':')
                    continue;

                var colon = line.IndexOf(':');
                var field = colon < 0 ? line : line.Substring(0, colon);
                var value = colon < 0 ? "" : line.Substring(colon + 1);
                if (value.StartsWith(' '))
                    value = value.Substring(1);

                switch (field)
                {
                    case "id":
                        id = value;
                        break;
                    case "event":
                        type = value;
                        break;
                    case "data":
                        if (hasData)
                            data.Append('\n');
                        data.Append(value);
                        hasData = true;
                        break;
                }
            }
        }

        static long Cursor(JsonElement root) => root.GetProperty("cursor").GetInt64();

        static bool Flag(JsonElement root, string name) => root.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;

        static IReadOnlyList<string> Refused(JsonElement root)
        {
            var refused = new List<string>();
            if (root.TryGetProperty("refused", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var topic in array.EnumerateArray())
                    refused.Add(topic.GetString() ?? "");
            return refused;
        }

        static void WriteRefused(Utf8JsonWriter writer, IReadOnlyList<string> refused)
        {
            if (refused.Count == 0)
                return;
            writer.WriteStartArray("refused");
            foreach (var topic in refused)
                writer.WriteStringValue(topic);
            writer.WriteEndArray();
        }

        static string Json(Action<Utf8JsonWriter> write)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                write(writer);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }
}
