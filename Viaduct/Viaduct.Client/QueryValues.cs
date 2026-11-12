using System;
using System.Globalization;
using System.Text;

namespace Viaduct.Client
{
    /// <summary>
    /// How a generated client writes a query parameter's value: URL-escaped, and the same in every culture, as the server reads
    /// a value with the invariant culture. Written in the caller's culture, 3 November (03.11.2026, 03/11/2026) arrives as
    /// 11 March, and a Norwegian negative number (−5, with U+2212) does not arrive at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One overload per kind of value, so the compiler picks the writer from the parameter's type rather than the value being
    /// tested at run time: no boxing, no type tests, which the WebAssembly interpreter would otherwise run on every call.
    /// A value is formatted into a stack buffer and appended directly; it is escaped in place, and only a character that needs
    /// it costs anything. A null is the generated code's to leave out: these never see one, except through the
    /// <see cref="object"/> overload.
    /// </para>
    /// <para>
    /// Escaping follows <see cref="Uri.EscapeDataString(string)"/>: everything but RFC 3986's unreserved characters
    /// (letters, digits, <c>-._~</c>) is percent-encoded as UTF-8, upper-case hex.
    /// </para>
    /// </remarks>
    public static partial class ViaductClientFunctions
    {
        /// <summary>The longest value <see cref="ISpanFormattable"/> types are formatted into without allocating.</summary>
        const int StackBuffer = 64;

        extension(StringBuilder sb)
        {
            /// <summary>Appends text, escaped.</summary>
            public StringBuilder AppendQueryValue(string value) => sb.AppendEscaped(value.AsSpan());

            /// <summary>Appends <c>true</c> or <c>false</c>.</summary>
            public StringBuilder AppendQueryValue(bool value) => sb.Append(value ? "true" : "false");

            /// <summary>Appends a date as ISO 8601: <c>2026-11-03</c>.</summary>
            public StringBuilder AppendQueryValue(DateOnly value)
            {
                Span<char> buffer = stackalloc char[10];
                value.TryFormat(buffer, out var written, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                // Digits and hyphens only: nothing to escape.
                return sb.Append(buffer[..written]);
            }

            /// <summary>Appends a moment in UTC, as a route part has it: <c>2026-11-03T10:30:00Z</c>, escaped.</summary>
            public StringBuilder AppendQueryValue(DateTime value)
            {
                Span<char> buffer = stackalloc char[20];
                value.ToUniversalTime().TryFormat(buffer, out var written, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                return sb.AppendEscaped(buffer[..written]);
            }

            /// <summary>Appends a moment with its offset: <c>2026-11-03T10:30:00+01:00</c>, escaped (the <c>+</c> above all).</summary>
            public StringBuilder AppendQueryValue(DateTimeOffset value)
            {
                Span<char> buffer = stackalloc char[25];
                value.TryFormat(buffer, out var written, "yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
                return sb.AppendEscaped(buffer[..written]);
            }

            /// <summary>Appends a time of day: <c>10:30:00</c>, escaped.</summary>
            public StringBuilder AppendQueryValue(TimeOnly value)
            {
                Span<char> buffer = stackalloc char[8];
                value.TryFormat(buffer, out var written, "HH:mm:ss", CultureInfo.InvariantCulture);
                return sb.AppendEscaped(buffer[..written]);
            }

            /// <summary>Appends a duration as the server parses one: <c>1.02:00:00</c>, escaped.</summary>
            public StringBuilder AppendQueryValue(TimeSpan value)
            {
                Span<char> buffer = stackalloc char[26];
                value.TryFormat(buffer, out var written, "c", CultureInfo.InvariantCulture);
                return sb.AppendEscaped(buffer[..written]);
            }

            /// <summary>
            /// Appends any other formattable value type with the invariant culture, escaped: numbers, <see cref="Guid"/>,
            /// enums, <see cref="char"/>. A constrained call, so the value is not boxed.
            /// </summary>
            public StringBuilder AppendQueryValue<T>(T value) where T : struct, ISpanFormattable
            {
                Span<char> buffer = stackalloc char[StackBuffer];
                return value.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture)
                    ? sb.AppendEscaped(buffer[..written])
                    : sb.AppendEscaped(value.ToString(null, CultureInfo.InvariantCulture).AsSpan());
            }

            /// <summary>
            /// Appends a value of any other type: formatted with the invariant culture where it can be, else its
            /// <see cref="object.ToString"/>, escaped. Nothing for null. The one overload that boxes a value type.
            /// </summary>
            public StringBuilder AppendQueryValue(object? value) => value switch
            {
                null => sb,
                IFormattable formattable => sb.AppendEscaped(formattable.ToString(null, CultureInfo.InvariantCulture).AsSpan()),
                _ => value.ToString() is { } text ? sb.AppendEscaped(text.AsSpan()) : sb,
            };

            /// <summary>
            /// Appends <paramref name="value"/> percent-encoded where it must be: runs of unreserved characters in one
            /// append each, so a value that needs no escaping is a single append and allocates nothing.
            /// </summary>
            internal StringBuilder AppendEscaped(ReadOnlySpan<char> value)
            {
                Span<byte> bytes = stackalloc byte[4];
                var start = 0;
                var index = 0;
                while (index < value.Length)
                {
                    if (IsUnreserved(value[index]))
                    {
                        index++;
                        continue;
                    }

                    sb.Append(value[start..index]);

                    // A character outside ASCII may take two chars (a surrogate pair) and up to four UTF-8 bytes; an unpaired
                    // surrogate is written as U+FFFD, as UTF-8 cannot carry it.
                    System.Text.Rune.DecodeFromUtf16(value[index..], out var rune, out var consumed);
                    var count = rune.EncodeToUtf8(bytes);
                    for (var b = 0; b < count; b++)
                        sb.Append('%').Append(Hex[bytes[b] >> 4]).Append(Hex[bytes[b] & 0xF]);

                    index += consumed;
                    start = index;
                }

                return sb.Append(value[start..]);
            }
        }

        const string Hex = "0123456789ABCDEF";

        /// <summary>RFC 3986's unreserved characters: what is never escaped.</summary>
        static bool IsUnreserved(char c)
            => (uint)((c | 0x20) - 'a') <= 'z' - 'a'
               || (uint)(c - '0') <= 9
               || c is '-' or '.' or '_' or '~';
    }
}
