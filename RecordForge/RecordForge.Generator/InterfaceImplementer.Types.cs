using Microsoft.CodeAnalysis;
using System.Collections.Generic;

namespace Subro.Generators
{
    /// <summary>
    /// How the setter of a generated property is written.
    /// </summary>
    internal enum SetterKind
    {
        /// <summary>No setter at all: { get; }</summary>
        None = 0,
        /// <summary>{ get; set; }</summary>
        Set = 1,
        /// <summary>{ get; init; }</summary>
        Init = 2,
    }

    /// <summary>
    /// A single property to be added to the partial type. Types are pre-rendered (fully qualified, with
    /// nullable annotations) so the model stays a plain value — no symbols are kept alive between
    /// incremental steps.
    /// </summary>
    /// <param name="DeclaringInterface">The interface the member is declared on, which is not necessarily the
    /// interface named in the attribute: it may be one that interface inherits.</param>
    /// <param name="NeedsReadOnlySetterShim">The interface wants a <c>set</c> but the target is a readonly
    /// struct, which can only carry an <c>init</c>. See <see cref="InterfaceImplementer.BuildCode"/>.</param>
    internal readonly record struct ImplementedPropertyInfo(
        string TypeName,
        string Name,
        SetterKind Setter,
        string DeclaringInterface,
        bool NeedsReadOnlySetterShim);

    /// <summary>
    /// Everything needed to emit one generated partial part for one annotated type. All members are value
    /// types or equatable arrays, so the incremental pipeline can compare and cache instances.
    /// </summary>
    internal sealed record ImplementInfo(
        string NameSpace,
        // Headers of the containing types, outermost first, e.g. "public partial class Outer<T>".
        EquatableArray<string> ContainingTypeHeaders,
        // Header of the annotated type itself, without the base list.
        string TypeHeader,
        // Fully qualified interfaces to add to the base list (those the type does not already implement).
        EquatableArray<string> InterfacesToDeclare,
        EquatableArray<ImplementedPropertyInfo> Properties,
        string HintName);

    partial class InterfaceImplementer
    {
        /// <summary>
        /// Type names are rendered global::-qualified and with nullable reference annotations, because the
        /// members come from arbitrary interfaces in arbitrary assemblies: there is no namespace context in
        /// the generated file to shorten them against.
        /// </summary>
        internal static readonly SymbolDisplayFormat TypeFormat =
            SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        internal static string Keyword(INamedTypeSymbol type) =>
            type.TypeKind == TypeKind.Struct
                ? type.IsRecord ? "record struct" : "struct"
                : type.IsRecord ? "record" : "class";

        internal static string AccessibilityKeyword(Accessibility accessibility) => accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Private => "private",
            _ => "internal",
        };

        /// <summary>
        /// Builds the declaration header for a partial part of <paramref name="type"/>, e.g.
        /// <c>public readonly partial record struct Foo&lt;T&gt;</c>.
        /// </summary>
        /// <remarks>
        /// Type parameters are repeated (required for a partial part) but their constraints are not: a part
        /// without constraint clauses inherits them from the part that declares them, which keeps this from
        /// having to render — and keep in sync with — every constraint form.
        /// </remarks>
        internal static string BuildHeader(INamedTypeSymbol type)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(AccessibilityKeyword(type.DeclaredAccessibility)).Append(' ');
            if (type.IsReadOnly && type.TypeKind == TypeKind.Struct) sb.Append("readonly ");
            sb.Append("partial ").Append(Keyword(type)).Append(' ').Append(type.Name);
            AppendTypeParameters(sb, type);
            return sb.ToString();
        }

        static void AppendTypeParameters(System.Text.StringBuilder sb, INamedTypeSymbol type)
        {
            if (type.TypeParameters.Length == 0) return;
            sb.Append('<');
            for (int i = 0; i < type.TypeParameters.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(type.TypeParameters[i].Name);
            }
            sb.Append('>');
        }

        /// <summary>
        /// A file-name-safe identity for the type: namespace, containing types and arity, so that two types
        /// with the same name in different scopes cannot produce the same hint name.
        /// </summary>
        internal static string BuildHintName(INamedTypeSymbol type)
        {
            var parts = new List<string>();
            for (var t = type; t is not null; t = t.ContainingType)
                parts.Insert(0, t.Arity > 0 ? t.Name + "`" + t.Arity : t.Name);

            var ns = type.GetNamespace();
            var sb = new System.Text.StringBuilder();
            if (ns.Length > 0) sb.Append(ns).Append('.');
            sb.Append(string.Join(".", parts)).Append(".Implements.g.cs");
            return sb.Replace('`', '_').Replace('<', '_').Replace('>', '_').Replace(',', '_').ToString();
        }
    }
}
