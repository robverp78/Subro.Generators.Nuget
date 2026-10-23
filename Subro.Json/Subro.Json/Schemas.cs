#if NET9_0_OR_GREATER
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Subro
{
    partial class Json
    {
        /// <summary>
        /// The JSON schema of <typeparamref name="T"/> as <see cref="Options"/> would serialize it, using one of the
        /// registered <see cref="JsonTypeInfo"/>s or, without one, reflection with the settings of <see cref="Options"/>.
        /// </summary>
        /// <remarks>
        /// Derived from the same type information as <see cref="Parse{T}(string)"/>, so a schema handed to another
        /// party (an LLM's structured output, a tool's parameters) describes exactly the JSON that is parsed back.
        /// </remarks>
        public static JsonElement GetJsonSchema<T>()
        {
#pragma warning disable IL2026, IL3050 //the reflection fallback, as in Parse
            JsonTypeInfo info = GetTypeInfo<T>() ?? ReflectionOptions.GetTypeInfo(typeof(T));
#pragma warning restore IL2026, IL3050
            //references without nullable annotations (and the root) are non-null: a schema for another party should
            //say what is expected, and structured-output modes require an object at the root
            var schema = JsonSchemaExporter.GetJsonSchemaAsNode(info, new() { TreatNullObliviousAsNonNullable = true });
            using var document = JsonDocument.Parse(schema.ToJsonString());
            return document.RootElement.Clone();
        }
    }
}
#endif
