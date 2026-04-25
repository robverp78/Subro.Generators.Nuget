using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Subro.JsonTypes
{
    /// <summary>
    /// Contains <see cref="JsonTypeInfo"/> for the most common types used in endpoints
    /// </summary>
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(int?))]
    [JsonSerializable(typeof(long))]
    [JsonSerializable(typeof(long?))]
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(double?))]
    [JsonSerializable(typeof(decimal))]
    [JsonSerializable(typeof(global::System.Guid))]
    [JsonSerializable(typeof(global::System.Guid?))]
    [JsonSerializable(typeof(global::System.DateTime))]
    [JsonSerializable(typeof(global::System.DateTime?))]
    [JsonSerializable(typeof(global::System.DateTimeOffset))]
    [JsonSerializable(typeof(global::System.DateTimeOffset?))]
    [JsonSerializable(typeof(global::System.DateOnly))]
    [JsonSerializable(typeof(global::System.DateOnly?))]
    [JsonSerializable(typeof(global::System.TimeOnly))]
    [JsonSerializable(typeof(global::System.TimeOnly?))]
    [JsonSerializable(typeof(byte[]))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(int[]))]
    [JsonSerializable(typeof(global::System.Memory<byte>))]
    public partial class CommonTypes : JsonSerializerContext { }



}

