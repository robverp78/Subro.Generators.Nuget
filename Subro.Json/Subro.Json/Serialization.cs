using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace Subro
{
    partial class Json
    {

        /// <summary>
        /// Parses the specified json into a value of type T, using one of the registered <see cref="JsonTypeInfo"/>s
        /// </summary>
        public static T? Parse<T>(string json) => Parse<T>(json, GetTypeInfo<T>());

        /// <summary>
        /// Parses the specified json into a value of type T, using the specified <see cref="JsonTypeInfo"/>
        /// </summary>
        public static T? Parse<T>(string json, JsonTypeInfo<T>? info)
        {
            if (info == null)
#pragma warning disable IL2026, IL3050 //Checks for AOT environment are in GetJsonTypeInfo
                return JsonSerializer.Deserialize<T>(json);
#pragma warning restore IL2026, IL3050
            else
                return JsonSerializer.Deserialize<T>(json, info);
        }

        /// <summary>
        /// Converts the value into a json string, using one of the registered <see cref="JsonTypeInfo"/>s
        /// </summary>
        public static string Stringify<T>(this T value) => Stringify(value, GetTypeInfo<T>());

        /// <summary>
        /// Converts the value into a json string, using the specified <see cref="JsonTypeInfo"/>
        /// </summary>
        public static string Stringify<T>(this T value, JsonTypeInfo<T>? info)
        {
            if (info == null)
#pragma warning disable IL2026, IL3050 //Checks for AOT environment are done in GetJsonTypeInfo
                return JsonSerializer.Serialize(value);
#pragma warning restore IL2026, IL3050
            else
                return JsonSerializer.Serialize(value, info);
            throw new NotImplementedException();
        }

    }
}
