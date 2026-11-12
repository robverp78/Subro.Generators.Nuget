#if NET9_0_OR_GREATER
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Subro
{
    partial class Json
    {
        /// <summary>
        /// The JSON schema of <typeparamref name="T"/> as <see cref="Options"/> would serialize it, from the contract
        /// <see cref="TypeInfo{T}"/> gives.
        /// </summary>
        /// <remarks>
        /// Derived from the same type information as <see cref="Parse{T}(string)"/>, so a schema handed to another
        /// party (an LLM's structured output, a tool's parameters) describes exactly the JSON that is parsed back.
        /// <para>
        /// Built once per type and kept: it is asked for on every structured-output request and tool definition, and was
        /// built anew each time (about 90 µs and 29 KB). A cloned <see cref="JsonElement"/> only reads, so it is shared.
        /// </para>
        /// </remarks>
        public static JsonElement GetJsonSchema<T>()
            => (SchemaOf<T>.Value ??= new(BuildJsonSchema(TypeInfo<T>()))).Value;

        static JsonElement BuildJsonSchema(JsonTypeInfo info)
        {
            //references without nullable annotations (and the root) are non-null: a schema for another party should
            //say what is expected, and structured-output modes require an object at the root
            var schema = JsonSchemaExporter.GetJsonSchemaAsNode(info, new() { TreatNullObliviousAsNonNullable = true });
            using var document = JsonDocument.Parse(schema.ToJsonString());
            return document.RootElement.Clone();
        }

        /// <summary>
        /// The schema of <typeparamref name="T"/> once built. Boxed, so it is published whole: two threads may each build
        /// it the first time, never read half of one.
        /// </summary>
        static class SchemaOf<T>
        {
            public static System.Runtime.CompilerServices.StrongBox<JsonElement>? Value;
        }
    }
}
#endif
