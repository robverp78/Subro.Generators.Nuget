using System;
using System.Collections.Generic;
using System.Text;

namespace Subro.JsonTypes
{
    /// <summary>
    /// Marks a type so a json serializer will be generated for it. For future use, does not work reliably with the current available .net generators
    /// </summary>
    /// <remarks>
    /// In the generated files, this adds a to a JsonSerializerContext attribute to an internal JsonSerializable class.
    /// As the current state of generators is, this is not a reliable way to create the System.Text.Json generators, but it
    /// is built in for future use.
    /// For now the main advantage would be an extra registration of the type to prevent it being trimmed by the tree shaking mechanics.
    /// </remarks>
    [AttributeUsage(AttributeTargets.ReturnValue | AttributeTargets.Class | AttributeTargets.Parameter | AttributeTargets.Enum)]

    public class MarkAsJsonSerializableAttribute : Attribute
    {
    }
}
