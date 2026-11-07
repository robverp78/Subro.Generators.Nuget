using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Subro.Generators
{
    /// <summary>
    /// The metadata a generated type carries about itself: <c>Fields</c>, the projection and the copy, and
    /// <c>IForgedRecord&lt;TSelf&gt;</c>.
    /// </summary>
    partial class RecordForger
    {
        const string runtime = "global::Subro.RecordForge.";

        /// <summary>Fully qualified, without a nullable annotation: for <c>typeof</c>, a base list and a generic constraint.</summary>
        internal static readonly SymbolDisplayFormat TypeOfFormat =
            SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

        /// <summary>
        /// Code creating each attribute again, in the order written. Left out: compiler attributes, RecordForge's own, and
        /// attributes the generated code could not create (inaccessible, or with an argument it cannot write).
        /// </summary>
        static EquatableArray<string> WriteAttributes(ImmutableArray<AttributeData> attributes, Compilation compilation, CancellationToken token)
        {
            if (attributes.IsEmpty) return GeneratorFunctions.EmptyStrings;

            var written = ImmutableArray.CreateBuilder<string>();
            foreach (var attribute in attributes)
            {
                token.ThrowIfCancellationRequested();
                if (TryWriteAttribute(attribute, compilation) is { } code)
                    written.Add(code);
            }
            return written.ToEquatable();
        }

        static string? TryWriteAttribute(AttributeData attribute, Compilation compilation)
        {
            if (attribute.AttributeClass is not { TypeKind: not TypeKind.Error } attributeClass
                || attribute.AttributeConstructor is not { } constructor)
                return null;

            var nameSpace = attributeClass.GetNamespace();
            if (nameSpace == attributeNamespace
                || nameSpace.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal)
                || nameSpace.StartsWith("System.Diagnostics.CodeAnalysis", StringComparison.Ordinal))
                return null;

            if (!compilation.IsSymbolAccessibleWithin(attributeClass, compilation.Assembly)
                || !compilation.IsSymbolAccessibleWithin(constructor, compilation.Assembly))
                return null;

            var sb = new StringBuilder("new ").Append(attributeClass.ToDisplayString(TypeOfFormat)).Append('(');
            for (int i = 0; i < attribute.ConstructorArguments.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                if (WriteConstant(attribute.ConstructorArguments[i]) is not { } argument) return null;
                sb.Append(argument);
            }
            sb.Append(')');

            if (attribute.NamedArguments.Length > 0)
            {
                sb.Append(" { ");
                for (int i = 0; i < attribute.NamedArguments.Length; i++)
                {
                    var named = attribute.NamedArguments[i];
                    if (WriteConstant(named.Value) is not { } value) return null;
                    if (i > 0) sb.Append(", ");
                    sb.Append(named.Key).Append(" = ").Append(value);
                }
                sb.Append(" }");
            }

            return sb.ToString();
        }

        /// <summary>
        /// An attribute argument as C#, typed by a cast so the same constructor is chosen as in the source. Null for an
        /// argument that cannot be written.
        /// </summary>
        static string? WriteConstant(TypedConstant constant)
        {
            if (constant.Kind == TypedConstantKind.Error) return null;

            if (constant.Kind == TypedConstantKind.Array)
            {
                if (constant.Type is not IArrayTypeSymbol arrayType) return null;
                if (constant.IsNull) return "default(" + arrayType.ToDisplayString(TypeOfFormat) + ")";

                var elements = new List<string>();
                foreach (var value in constant.Values)
                {
                    if (WriteConstant(value) is not { } element) return null;
                    elements.Add(element);
                }
                return "new " + arrayType.ElementType.ToDisplayString(TypeOfFormat) + "[] { " + string.Join(", ", elements) + " }";
            }

            var type = constant.Type?.ToDisplayString(TypeOfFormat) ?? "object";
            if (constant.IsNull) return "default(" + type + ")";

            switch (constant.Kind)
            {
                case TypedConstantKind.Type:
                    return constant.Value is ITypeSymbol typeArgument ? "typeof(" + typeArgument.ToDisplayString(TypeOfFormat) + ")" : null;

                case TypedConstantKind.Enum:
                    return "(" + type + ")(" + FormatNumber(constant.Value!) + ")";

                case TypedConstantKind.Primitive:
                    return constant.Value switch
                    {
                        string text => SymbolDisplay.FormatLiteral(text, quote: true),
                        char character => SymbolDisplay.FormatLiteral(character, quote: true),
                        bool flag => flag ? "true" : "false",
                        double d when double.IsNaN(d) => "double.NaN",
                        double d when double.IsPositiveInfinity(d) => "double.PositiveInfinity",
                        double d when double.IsNegativeInfinity(d) => "double.NegativeInfinity",
                        float f when float.IsNaN(f) => "float.NaN",
                        float f when float.IsPositiveInfinity(f) => "float.PositiveInfinity",
                        float f when float.IsNegativeInfinity(f) => "float.NegativeInfinity",
                        { } number => "(" + type + ")(" + FormatNumber(number) + ")",
                        _ => null,
                    };

                default:
                    return null;
            }
        }

        static string FormatNumber(object value) => value switch
        {
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        /// <summary>
        /// The static members describing the type: <c>Fields</c>; for a type that can be made, <c>Projection</c>,
        /// <c>ProjectionFrom&lt;T&gt;()</c> and <c>From</c>; and the explicit members of <c>IForgedRecord&lt;TSelf&gt;</c>.
        /// </summary>
        internal static void AppendMetadata(StringBuilder sb, CreationInfo info, ForgeCapabilities capabilities)
        {
            string hides = info.BaseHasMetadata ? "new " : "";
            string fieldList = "global::System.Collections.Generic.IReadOnlyList<" + runtime + "RecordField>";

            //the metadata's own annotations, whatever the nullable context of the project
            sb.Append(@"

#nullable enable");

            //Fields, made on first use
            sb.Append(@"

        private static ").Append(fieldList).Append(@"? __recordForgeFields;

        /// <summary>The properties of <see cref=""").Append(info.QualifiedInterface).Append(@"""/>, as it and the interfaces it inherits declare them.</summary>
        public static ").Append(hides).Append(fieldList).Append(@" Fields => __recordForgeFields ??= __RecordForgeCreateFields();

        private static ").Append(runtime).Append(@"RecordField[] __RecordForgeCreateFields()
        {");

            var interfaceIndex = new Dictionary<string, int>();
            for (int i = 0; i < info.InterfaceAttributes.Length; i++)
            {
                var declaring = info.InterfaceAttributes[i];
                interfaceIndex[declaring.Interface] = i;
                sb.Append(@"
            var interface").Append(i).Append(" = ").AppendAttributeArray(declaring.Attributes).Append(';');
            }

            sb.Append(@"
            return new ").Append(runtime).Append(@"RecordField[]
            {");
            foreach (var prop in info.Properties)
            {
                sb.Append(@"
                ").Append(runtime).Append("RecordField.Create<").Append(prop.DeclaringInterface).Append(", ").Append(prop.QualifiedType)
                    .Append(">(\"").Append(prop.Name).Append("\", x => x.").Append(prop.Name).Append(", x => x.").Append(prop.Name)
                    .Append(", ").Append(prop.AllowsNull ? "true" : "false")
                    .Append(", ").Append(prop.HasSetter ? "true" : "false")
                    .Append(", ").AppendAttributeArray(prop.Attributes)
                    .Append(", ").Append(interfaceIndex.TryGetValue(prop.DeclaringInterface, out var index) ? "interface" + index : "new global::System.Attribute[0]")
                    .Append("),");
            }
            sb.Append(@"
            };
        }");

            string projectionType = "global::System.Linq.Expressions.Expression<global::System.Func<" + info.QualifiedInterface + ", " + info.RecordName + ">>";

            if (info.CanCreate)
            {
                sb.Append(@"

        /// <summary>A new <see cref=""").Append(info.RecordName).Append(@"""/> from anything implementing <see cref=""").Append(info.QualifiedInterface).Append(@"""/>, for a query to select into.</summary>
        ").Append(info.InterfaceAccess).Append(" static ").Append(hides).Append(projectionType).Append(@" Projection { get; } = x => ").AppendCreation(info, "x").Append(@";

        /// <summary>A new <see cref=""").Append(info.RecordName).Append(@"""/> from a <typeparamref name=""T""/>, for a query on it to select into.</summary>
        ").Append(info.InterfaceAccess).Append(" static ").Append(hides).Append("global::System.Linq.Expressions.Expression<global::System.Func<T, ").Append(info.RecordName)
                    .Append(">> ProjectionFrom<T>() where T : ").Append(info.QualifiedInterface).Append(" => x => ").AppendCreation(info, "x").Append(@";

        /// <summary>A new <see cref=""").Append(info.RecordName).Append(@"""/> with the values of <paramref name=""source""/>.</summary>
        ").Append(info.InterfaceAccess).Append(" static ").Append(info.RecordName).Append(" From(").Append(info.QualifiedInterface).Append(" source) => ").AppendCreation(info, "source").Append(';');
            }

            if (capabilities.ForgedInterface)
            {
                string self = runtime + "IForgedRecord<" + info.RecordName + ">";
                sb.Append(@"

        static global::System.Type ").Append(self).Append(".Interface => typeof(").Append(info.QualifiedInterface).Append(@");
        static ").Append(fieldList).Append(' ').Append(self).Append(@".Fields => Fields;
        static global::System.Linq.Expressions.LambdaExpression? ").Append(self).Append(".Projection => ")
                    .Append(info.CanCreate ? "Projection" : "null").Append(';');
            }

            sb.Append(@"
#nullable restore");
        }

    }

    internal static class RecordForgerMetadataFunctions
    {
        public static StringBuilder AppendAttributeArray(this StringBuilder sb, EquatableArray<string> attributes)
        {
            if (attributes.Length == 0)
                return sb.Append("new global::System.Attribute[0]");

            sb.Append("new global::System.Attribute[] { ");
            for (int i = 0; i < attributes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(attributes[i]);
            }
            return sb.Append(" }");
        }

        /// <summary>
        /// <c>new Rec(x.A, x.B) { C = x.C }</c>: the constructor with the most parameters, then every other property that can
        /// be set after it. A property neither can set is left at its default.
        /// </summary>
        public static StringBuilder AppendCreation(this StringBuilder sb, CreationInfo info, string source)
        {
            IReadOnlyList<PropertyCreationInfo> ctor = [];
            foreach (var candidate in info.GetConstructors())
                if (candidate.Count > ctor.Count || ctor.Count == 0)
                    ctor = candidate;

            sb.Append("new ").Append(info.RecordName).Append('(');
            for (int i = 0; i < ctor.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(source).Append('.').Append(ctor[i].Name);
            }
            sb.Append(')');

            var inCtor = new HashSet<string>(ctor.Select(static p => p.Name));
            var rest = info.Properties
                .Where(p => !inCtor.Contains(p.Name) && (p.FromBase ? p.BaseSettable : info.CreatesSetter(p)))
                .ToList();

            if (rest.Count > 0)
            {
                sb.Append(" { ");
                for (int i = 0; i < rest.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(rest[i].Name).Append(" = ").Append(source).Append('.').Append(rest[i].Name);
                }
                sb.Append(" }");
            }
            return sb;
        }
    }

    partial class GeneratorFunctions
    {
        public static readonly EquatableArray<string> EmptyStrings = new(ImmutableArray<string>.Empty);
    }
}
