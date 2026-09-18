using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Viaduct.Generation
{
    /// <summary>
    /// The XML documentation of interfaces that live in another assembly.
    /// </summary>
    /// <remarks>
    /// A shared interface normally sits in a contracts project of its own, and that is exactly the case where
    /// the compiler hands a generator nothing: there is no syntax to read, and while csc writes an XML file
    /// beside each assembly, it does not give the documentation of a reference to the compilation. Reading
    /// those files here is not an option either — analyzers may not do file IO, for good reasons about
    /// determinism and caching.
    /// <para>
    /// So the build hands them over instead. The props shipped with Viaduct.Server and Viaduct.Client add each
    /// referenced assembly's XML file as an <c>AdditionalFiles</c> item, which the generator reads through
    /// Roslyn's own tracked, cached <see cref="AdditionalText"/> API.
    /// </para>
    /// </remarks>
    internal sealed class ReferenceDocumentation
    {
        /// <summary>Nothing was handed over, or none of it parsed.</summary>
        public static readonly ReferenceDocumentation Empty = new(ImmutableDictionary<string, string>.Empty);

        private readonly IReadOnlyDictionary<string, string> members;

        private ReferenceDocumentation(IReadOnlyDictionary<string, string> members) => this.members = members;

        /// <summary>The documentation for a member, or null when the file did not describe it.</summary>
        public string? this[string? documentationId]
            => documentationId is not null && members.TryGetValue(documentationId, out var member) ? member : null;

        public bool IsEmpty => members.Count == 0;

        /// <summary>
        /// Everything documented in the given files, by documentation comment id.
        /// </summary>
        /// <remarks>
        /// Ids are unique across assemblies — they are fully qualified — so one lookup covers every reference.
        /// A file that cannot be parsed is skipped: it costs the summaries of that assembly, not the build.
        /// </remarks>
        public static ReferenceDocumentation Create(ImmutableArray<AdditionalText> files, CancellationToken ct)
        {
            if (files.IsDefaultOrEmpty)
                return Empty;

            Dictionary<string, string>? members = null;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                if (file.GetText(ct)?.ToString() is not { Length: > 0 } content)
                    continue;

                try
                {
                    var elements = XDocument.Parse(content).Root?.Element("members")?.Elements("member");
                    if (elements is null)
                        continue;

                    foreach (var element in elements)
                    {
                        if (element.Attribute("name")?.Value is { Length: > 0 } name)
                            (members ??= new(StringComparer.Ordinal))[name] = element.ToString();
                    }
                }
                catch (XmlException)
                {
                    // Not a documentation file, or a broken one. Either way there is nothing to take from it.
                }
            }

            return members is null ? Empty : new ReferenceDocumentation(members);
        }
    }

    internal static class ReferenceDocumentationExtensions
    {
        /// <summary>
        /// The same interface, with any summary and description that were missing filled in from the
        /// documentation of the assembly it was declared in.
        /// </summary>
        /// <remarks>
        /// Applied here rather than while the metadata is built, because the documentation arrives through a
        /// provider of its own: keeping the transform independent of it is what lets Roslyn cache each half.
        /// A method that already has both — from its attributes, or from source it could read — is left alone.
        /// </remarks>
        public static InterfaceMetaData WithDocumentation(this InterfaceMetaData info, ReferenceDocumentation documentation)
        {
            if (documentation.IsEmpty || info.Methods.All(static m => m.DocumentationId is null))
                return info;

            var methods = info.Methods.Select(method =>
            {
                if (method.DocumentationId is null || documentation[method.DocumentationId] is not { } member)
                    return method;

                var (summary, remarks) = ViaductGeneratorFunctions.ParseDocumentation(member);

                return method with
                {
                    Summary = method.Summary ?? summary,
                    Description = method.Description ?? remarks,
                };
            });

            return info with { Methods = [.. methods] };
        }
    }
}
