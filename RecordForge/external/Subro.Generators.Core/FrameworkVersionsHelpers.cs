using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;


#if !NET5_0_OR_GREATER 
namespace System.Runtime.CompilerServices
{
    internal sealed class IsExternalInit { }
}
#endif

#if !NET7_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis
{
    // Required for [StringSyntax("Route")] etc.
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Field | AttributeTargets.Property)]
    internal sealed class StringSyntaxAttribute : Attribute
    {
        public StringSyntaxAttribute(string syntax) => Syntax = syntax;
        public StringSyntaxAttribute(string syntax, params object?[] arguments)
        {
            Syntax = syntax;
            Arguments = arguments;
        }
        public string Syntax { get; }
        public object?[] Arguments { get; } = [];
        public const string CompositeFormat = "CompositeFormat";
        public const string DateOnlyFormat = "DateOnlyFormat";
        public const string DateTimeFormat = "DateTimeFormat";
        public const string EnumFormat = "EnumFormat";
        public const string GuidFormat = "GuidFormat";
        public const string Json = "Json";
        public const string NumericFormat = "NumericFormat";
        public const string Regex = "Regex";
        public const string Route = "Route";
        public const string TimeOnlyFormat = "TimeOnlyFormat";
        public const string TimeSpanFormat = "TimeSpanFormat";
        public const string Uri = "Uri";
        public const string Xml = "Xml";
    }
}
#endif

#if !NET6_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    // Required for CallerArgumentExpression
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName)
            => ParameterName = parameterName;
        public string ParameterName { get; }
    }
}
#endif

#if !NET7_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    // Required for static abstract interface members
    internal static class RequiresStaticAttribute { }

    // Required for 'required' keyword on properties
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct |
                    AttributeTargets.Field | AttributeTargets.Property)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName)
            => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    // Required for 'required' keyword
    [AttributeUsage(AttributeTargets.Constructor)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
#endif

#if !NET8_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = false)]
    internal sealed class ExperimentalAttribute : Attribute
    {
        public ExperimentalAttribute(string diagnosticId)
        {
            DiagnosticId = diagnosticId;
        }

        public string DiagnosticId { get; }
        public string? UrlFormat { get; set; }
    }
}
#endif

#if NETSTANDARD2_0
internal struct HashCode
{
    private int _hash;
    public HashCode() { _hash = 17; }
    public void Add<T>(T value)
    {
        unchecked { _hash = _hash * 31 + (value?.GetHashCode() ?? 0); }
    }
    public readonly int ToHashCode() => _hash;
}
#endif