using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Viaduct.Signals
{
    /// <summary>
    /// A topic template such as <c>chat.room/{roomId}</c>: filled to a topic when publishing, matched against a topic to read
    /// the values back when a signal arrives.
    /// </summary>
    /// <remarks>
    /// Values are written by <see cref="SignalTopicValues"/>, which escapes text, so a filled hole holds no <c>/</c> and no
    /// braces. Matching reads up to the next literal part of the template.
    /// </remarks>
    public sealed class SignalTopicTemplate
    {
        readonly string[] literals;

        SignalTopicTemplate(string template, string[] literals, string[] parameters)
        {
            Template = template;
            this.literals = literals;
            ParameterNames = parameters;
        }

        /// <summary>The template as written.</summary>
        public string Template { get; }

        /// <summary>The names of the holes, in the order they appear.</summary>
        public IReadOnlyList<string> ParameterNames { get; }

        /// <summary>True for a template without holes, which is its own topic.</summary>
        public bool IsFixed => ParameterNames.Count == 0;

        /// <summary>Reads a template. Throws <see cref="FormatException"/> for an unclosed or empty hole.</summary>
        public static SignalTopicTemplate Parse(string template)
        {
            ArgumentNullException.ThrowIfNull(template);

            var literals = new List<string>();
            var parameters = new List<string>();
            var literal = new StringBuilder();

            for (var i = 0; i < template.Length; i++)
            {
                var c = template[i];
                if (c == '}')
                    throw new FormatException($"'{template}' has a '}}' that closes no hole.");
                if (c != '{')
                {
                    literal.Append(c);
                    continue;
                }

                var end = template.IndexOf('}', i + 1);
                var name = end < 0 ? "" : template.Substring(i + 1, end - i - 1);
                if (name.Length == 0 || name.IndexOf('{') >= 0)
                    throw new FormatException($"'{template}' has a hole that is not closed or has no name.");

                literals.Add(literal.ToString());
                literal.Clear();
                parameters.Add(name);
                i = end;
            }

            literals.Add(literal.ToString());
            return new SignalTopicTemplate(template, [.. literals], [.. parameters]);
        }

        /// <summary>
        /// The topic with each hole filled, in the order of <see cref="ParameterNames"/>. The values are written as
        /// <see cref="SignalTopicValues"/> writes them.
        /// </summary>
        public string Fill(params ReadOnlySpan<string> values)
        {
            if (values.Length != ParameterNames.Count)
                throw new ArgumentException($"'{Template}' has {ParameterNames.Count} holes, given {values.Length} values.", nameof(values));
            if (values.Length == 0)
                return Template;

            var topic = new StringBuilder(literals[0]);
            for (var i = 0; i < values.Length; i++)
                topic.Append(values[i]).Append(literals[i + 1]);
            return topic.ToString();
        }

        /// <summary>
        /// Whether <paramref name="topic"/> is this template filled in, and if so the values of its holes as written,
        /// for <see cref="SignalTopicValues"/> to read back.
        /// </summary>
        public bool TryMatch(string topic, out string[] values)
        {
            ArgumentNullException.ThrowIfNull(topic);
            values = new string[ParameterNames.Count];

            if (!topic.StartsWith(literals[0], StringComparison.Ordinal))
                return false;

            var position = literals[0].Length;
            for (var i = 0; i < values.Length; i++)
            {
                var next = literals[i + 1];
                int end;
                if (next.Length == 0)
                    end = i == values.Length - 1 ? topic.Length : -1;
                else
                    end = topic.IndexOf(next, position, StringComparison.Ordinal);

                // A hole directly followed by another cannot be told apart, and an empty value was never written.
                if (end <= position)
                    return false;

                values[i] = topic.Substring(position, end - position);
                position = end + next.Length;
            }

            return position == topic.Length;
        }

        /// <inheritdoc/>
        public override string ToString() => Template;
    }

    /// <summary>
    /// How a value is written into a topic and read back: text escaped, ids and numbers invariant. The generated code calls
    /// these for each hole.
    /// </summary>
    public static class SignalTopicValues
    {
        /// <summary>Text, escaped so it holds no <c>/</c> or braces.</summary>
        public static string Format(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0)
                throw new ArgumentException("A topic value cannot be empty.", nameof(value));
            return Uri.EscapeDataString(value);
        }

        /// <summary>An id in its <c>D</c> form.</summary>
        public static string Format(Guid value) => value.ToString("D");

        /// <summary>A number, invariant.</summary>
        public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>A number, invariant.</summary>
        public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>A number, invariant.</summary>
        public static string Format(short value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary><c>true</c> or <c>false</c>.</summary>
        public static string Format(bool value) => value ? "true" : "false";

        /// <summary>Text as <see cref="Format(string)"/> wrote it.</summary>
        public static string ParseString(string value) => Uri.UnescapeDataString(value);

        /// <summary>An id as <see cref="Format(Guid)"/> wrote it.</summary>
        public static Guid ParseGuid(string value) => Guid.ParseExact(value, "D");

        /// <summary>A number as <see cref="Format(int)"/> wrote it.</summary>
        public static int ParseInt32(string value) => int.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        /// <summary>A number as <see cref="Format(long)"/> wrote it.</summary>
        public static long ParseInt64(string value) => long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        /// <summary>A number as <see cref="Format(short)"/> wrote it.</summary>
        public static short ParseInt16(string value) => short.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        /// <summary><c>true</c> or <c>false</c>.</summary>
        public static bool ParseBoolean(string value) => value switch
        {
            "true" => true,
            "false" => false,
            _ => throw new FormatException($"'{value}' is not a topic's true or false."),
        };
    }
}
