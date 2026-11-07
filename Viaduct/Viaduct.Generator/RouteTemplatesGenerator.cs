using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Subro.Generators;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Viaduct.Generation
{
    /// <summary>
    /// For a static partial class marked <c>[RouteTemplates]</c>, a <c>{Name}Link(...)</c> method per <c>const string</c>
    /// route template in it, which fills in the template's parameters. See <c>RouteTemplatesAttribute</c>.
    /// </summary>
    /// <remarks>
    /// The generated code uses nothing but the framework: the class is meant for a project that only shares definitions
    /// between a server and its clients, which references Viaduct.Core and no client or server package.
    /// </remarks>
    [Generator]
    public sealed class RouteTemplatesGenerator : IIncrementalGenerator
    {
        const string AttributeName = "Viaduct.RouteTemplatesAttribute";
        const string LinkSuffix = "Link";

        /// <summary>A template parameter: <c>{id}</c>, <c>{id:guid}</c>, <c>{page?}</c>, <c>{page=1}</c>, <c>{*path}</c>.</summary>
        static readonly Regex Parameter = new Regex(
            @"\{(?<catchAll>\*{1,2})?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<constraints>(:[^{}=?]+)*)(?<default>=[^{}]*)?(?<optional>\?)?\}",
            RegexOptions.CultureInvariant);

        /// <summary>One template parameter and how its value is written.</summary>
        internal sealed record ParameterInfo(string Name, string Type, bool Optional, bool CatchAll);

        /// <summary>One route constant: its name, and its template as literal parts and parameters in turn.</summary>
        internal sealed record TemplateInfo(string Field, EquatableArray<string> Literals, EquatableArray<ParameterInfo> Parameters);

        internal sealed record TemplatesInfo(string Namespace, string ClassName, string Accessibility, EquatableArray<TemplateInfo> Templates);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var classes = context.ValuesForAttributeWithMetadataName(AttributeName,
                static (node, _) => node is ClassDeclarationSyntax, GetInfo);

            context.RegisterSourceOutput(classes, static (spc, info) =>
            {
                var file = info.Namespace.Length == 0 ? info.ClassName : $"{info.Namespace}.{info.ClassName}";
                spc.AddSource($"{file}.RouteLinks.g.cs", BuildCode(info));
            });
        }

        static TransformResult<TemplatesInfo> GetInfo(GeneratorAttributeSyntaxContext context, CancellationToken token)
        {
            if (context.TargetSymbol is not INamedTypeSymbol owner)
                return default;

            var location = LocationInfo.From(owner);
            bool isPartial = owner.DeclaringSyntaxReferences.Any(r =>
                r.GetSyntax(token) is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword));

            if (!owner.IsStatic || !isPartial || owner.ContainingType is not null || owner.IsGenericType)
                return ViaductErrors.RouteTemplatesNotStaticPartial.CreateError(location,
                    "'{0}' is marked [RouteTemplates] and must be a top-level static partial class, which gets the link methods", owner.Name);

            var result = Transform.Build<TemplatesInfo>();
            var memberNames = new HashSet<string>(owner.GetMembers().Select(static member => member.Name), StringComparer.Ordinal);
            var templates = new List<TemplateInfo>();

            foreach (var field in owner.GetMembers().OfType<IFieldSymbol>())
            {
                token.ThrowIfCancellationRequested();

                if (!field.IsConst || field.ConstantValue is not string template
                    || field.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                    continue;

                var linkName = field.Name + LinkSuffix;
                if (memberNames.Contains(linkName))
                {
                    result.Diagnostics.Add(ViaductErrors.RouteLinkNameTaken.CreateWarning(LocationInfo.From(field),
                        "No link is generated for '{0}': '{1}' is already a member of '{2}'", field.Name, linkName, owner.Name));
                    continue;
                }

                templates.Add(Parse(field.Name, template));
            }

            var ns = owner.ContainingNamespace;
            result.Result = new TemplatesInfo(
                ns.IsGlobalNamespace ? "" : ns.ToDisplayString(),
                owner.Name,
                owner.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
                new EquatableArray<TemplateInfo>(templates.ToImmutableArray()));
            return result;
        }

        /// <summary>The template as literal text and parameters in turn, starting and ending with a literal (maybe empty).</summary>
        internal static TemplateInfo Parse(string field, string template)
        {
            var literals = new List<string>();
            var parameters = new List<ParameterInfo>();
            var position = 0;

            foreach (Match match in Parameter.Matches(template))
            {
                literals.Add(Unescape(template.Substring(position, match.Index - position)));
                parameters.Add(new ParameterInfo(
                    match.Groups["name"].Value,
                    TypeOf(match.Groups["constraints"].Value),
                    match.Groups["optional"].Success || match.Groups["default"].Success,
                    match.Groups["catchAll"].Success));
                position = match.Index + match.Length;
            }

            literals.Add(Unescape(template.Substring(position)));

            return new TemplateInfo(field, new EquatableArray<string>(literals.ToImmutableArray()), new EquatableArray<ParameterInfo>(parameters.ToImmutableArray()));
        }

        static string Unescape(string literal) => literal.Replace("{{", "{").Replace("}}", "}");

        /// <summary>The C# type of a parameter from its first type constraint; a string without one.</summary>
        static string TypeOf(string constraints)
        {
            foreach (var constraint in constraints.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (constraint.Trim().ToLowerInvariant())
                {
                    case "guid": return "global::System.Guid";
                    case "int": return "int";
                    case "long": return "long";
                    case "bool": return "bool";
                    case "decimal": return "decimal";
                    case "double": return "double";
                    case "float": return "float";
                    case "datetime": return "global::System.DateTime";
                }
            }

            return "string";
        }

        internal static string BuildCode(TemplatesInfo info)
        {
            var sb = new StringBuilder(@"// <auto-generated/>
#nullable enable
");
            if (info.Namespace.Length > 0)
                sb.Append("\nnamespace ").Append(info.Namespace).Append(";\n");

            sb.Append('\n').Append(info.Accessibility).Append(" static partial class ").Append(info.ClassName).Append(@"
{");

            foreach (var template in info.Templates)
            {
                var parameters = template.Parameters;
                var declared = string.Join(", ", parameters.Select(static p =>
                    (p.Optional ? NullableOf(p.Type) + " " : p.Type + " ") + Identifier(p.Name) + (p.Optional ? " = null" : "")));

                sb.Append(@"

    /// <summary>The route <see cref=""").Append(template.Field).Append(@"""/> with its parameters filled in. Generated by Viaduct from [RouteTemplates].</summary>
    public static string ").Append(template.Field).Append(LinkSuffix).Append('(').Append(declared).Append(@")
    {
        var link = new global::System.Text.StringBuilder();");

                for (var i = 0; i < parameters.Length; i++)
                {
                    AppendLiteral(sb, template.Literals[i]);

                    var parameter = parameters[i];
                    var name = Identifier(parameter.Name);
                    var value = parameter.Optional && parameter.Type != "string" ? name + ".Value" : name;

                    sb.Append(@"
        ");
                    if (parameter.Optional)
                        sb.Append("if (").Append(name).Append(" is not null) ");

                    sb.Append("link.Append(").Append(Format(value, parameter)).Append(");");
                }

                AppendLiteral(sb, template.Literals[parameters.Length]);

                // An optional parameter left out at the end leaves the slash before it, which the route does not need.
                var last = parameters.Length > 0 ? parameters[parameters.Length - 1] : null;
                if (last is { Optional: true } && template.Literals[parameters.Length].Length == 0)
                    sb.Append(@"
        if (").Append(Identifier(last.Name)).Append(@" is null && link.Length > 1 && link[link.Length - 1] == '/')
            link.Length--;");

                sb.Append(@"
        return link.ToString();
    }");
            }

            sb.Append(@"
}
");
            return sb.ToString();
        }

        static void AppendLiteral(StringBuilder sb, string literal)
        {
            if (literal.Length > 0)
                sb.Append(@"
        link.Append(").Append(SymbolDisplay.FormatLiteral(literal, quote: true)).Append(");");
        }

        /// <summary>How a value is written into a route: as Viaduct's client writes route values, and escaped.</summary>
        static string Format(string value, ParameterInfo parameter) => parameter.Type switch
        {
            "global::System.Guid" => value + ".ToString(\"D\")",
            "bool" => "(" + value + " ? \"true\" : \"false\")",
            "global::System.DateTime" => "global::System.Uri.EscapeDataString(" + value + ".ToUniversalTime().ToString(\"yyyy-MM-ddTHH:mm:ssZ\", global::System.Globalization.CultureInfo.InvariantCulture))",
            "string" when parameter.CatchAll => "string.Join(\"/\", global::System.Linq.Enumerable.Select(" + value + ".Split('/'), global::System.Uri.EscapeDataString))",
            "string" => "global::System.Uri.EscapeDataString(" + value + ")",
            _ => value + ".ToString(global::System.Globalization.CultureInfo.InvariantCulture)",
        };

        static string NullableOf(string type) => type + "?";

        static string Identifier(string name) =>
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
                ? "@" + name
                : name;
    }
}
