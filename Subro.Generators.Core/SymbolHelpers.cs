using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using System.Threading;

#nullable enable

namespace Subro.Generators
{
    /// <summary>
    /// What every generator needs of symbols and of the code it writes. A generator reaches the plain methods with
    /// <c>using static Subro.Generators.GeneratorFunctions;</c>, so that there is one version of each.
    /// </summary>
    internal static partial class GeneratorFunctions
    {
        /// <summary>A C# string literal for <paramref name="text"/>, quoted and escaped.</summary>
        public static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);

        /// <summary>The symbol as generated code names it from anywhere: fully qualified, with <c>global::</c>.</summary>
        public static string Display(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        /// <summary>
        /// A name as it can be written in code: <c>@class</c> for a keyword. Contextual keywords (<c>value</c>,
        /// <c>await</c>, <c>field</c>) are escaped too: where one of them is a keyword depends on the place the name
        /// ends up in, and an escaped name is the same name everywhere.
        /// </summary>
        public static string Identifier(string name)
            => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
                ? "@" + name
                : name;

        /// <summary>Whether one of the type's declarations says <c>partial</c>, so that generated code can add to it.</summary>
        public static bool IsPartial(INamedTypeSymbol type, CancellationToken token)
        {
            foreach (var reference in type.DeclaringSyntaxReferences)
                if (reference.GetSyntax(token) is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                    return true;

            return false;
        }

        /// <summary>
        /// Whether code elsewhere in its assembly can name it: no private or protected type on the way there, and no
        /// generic type around it.
        /// </summary>
        public static bool IsNameable(INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                    return false;

                if (!SymbolEqualityComparer.Default.Equals(current, type) && current.IsGenericType)
                    return false;
            }

            return true;
        }

        /// <summary>An instance property of the type, of a base type or of an interface of it.</summary>
        public static IPropertySymbol? FindProperty(ITypeSymbol type, string name)
        {
            for (var current = type; current is not null; current = current.BaseType)
                foreach (var member in current.GetMembers(name))
                    if (member is IPropertySymbol { IsStatic: false } property)
                        return property;

            return type.AllInterfaces.SelectMany(i => i.GetMembers(name)).OfType<IPropertySymbol>().FirstOrDefault(static p => !p.IsStatic);
        }

        extension(IncrementalGeneratorInitializationContext context)
        {
            /// <summary>
            /// The root namespace of the project being compiled, or its assembly name where the project does not pass
            /// one on. The project makes it visible with <c>&lt;CompilerVisibleProperty Include="RootNamespace" /&gt;</c>.
            /// </summary>
            public IncrementalValueProvider<string> RootNamespaceProvider()
                => context.AnalyzerConfigOptionsProvider
                    .Combine(context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName ?? ""))
                    .Select(static (pair, _) =>
                        pair.Left.GlobalOptions.TryGetValue("build_property.RootNamespace", out var root) && !string.IsNullOrWhiteSpace(root)
                            ? root.Trim()
                            : pair.Right);
        }
    }
}
