using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
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
    /// So the build hands them over instead. The props shipped with Viaduct.Server and Viaduct.Client add the XML
    /// file of each referenced project (and of the assemblies named in <c>ViaductDocumentationAssemblies</c>) as an
    /// <c>AdditionalFiles</c> item, which the generator reads through Roslyn's own tracked, cached
    /// <see cref="AdditionalText"/> API.
    /// </para>
    /// <para>
    /// Only what an interface needs is read: the file of the assembly each undocumented method was declared in,
    /// streamed until its members are found. Parsing every file whole cost a project with many package references
    /// seconds and gigabytes on every build (Vesta.Api: 315 files, 75 MB).
    /// </para>
    /// </remarks>
    internal static class ReferenceDocumentation
    {
        /// <summary>
        /// The same interface, with any summary and description that were missing filled in from the
        /// documentation file of the assembly each method was declared in.
        /// </summary>
        /// <remarks>
        /// Applied when the source is produced rather than while the metadata is built, because the files arrive
        /// through a provider of their own: keeping the transform independent of them is what lets Roslyn cache
        /// each half. A method that already has both — from its attributes, or from source it could read — is left
        /// alone, and no file is opened when no method needs one.
        /// </remarks>
        public static InterfaceMetaData WithDocumentation(
            this InterfaceMetaData info, ImmutableArray<AdditionalText> files, CancellationToken ct)
        {
            if (files.IsDefaultOrEmpty)
                return info;

            var wanted = info.Methods
                .Where(static m => m.DocumentationId is not null && m.DocumentationAssembly is not null)
                .GroupBy(static m => m.DocumentationAssembly!, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (wanted.Count == 0)
                return info;

            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var assembly in wanted)
            {
                ct.ThrowIfCancellationRequested();
                if (FileOf(files, assembly.Key) is { } file)
                    Read(file, new HashSet<string>(assembly.Select(static m => m.DocumentationId!), StringComparer.Ordinal), found, ct);
            }

            if (found.Count == 0)
                return info;

            return info with
            {
                Methods = [.. info.Methods.Select(method =>
                {
                    if (method.DocumentationId is null || !found.TryGetValue(method.DocumentationId, out var member))
                        return method;

                    var (summary, remarks) = ViaductGeneratorFunctions.ParseDocumentation(member);
                    return method with
                    {
                        Summary = method.Summary ?? summary,
                        Description = method.Description ?? remarks,
                    };
                })],
            };
        }

        /// <summary>The documentation file of <paramref name="assembly"/>: the build names each after its assembly.</summary>
        static AdditionalText? FileOf(ImmutableArray<AdditionalText> files, string assembly)
        {
            foreach (var file in files)
                if (string.Equals(Path.GetFileNameWithoutExtension(file.Path), assembly, StringComparison.OrdinalIgnoreCase))
                    return file;
            return null;
        }

        /// <summary>
        /// Adds to <paramref name="found"/> the <c>&lt;member&gt;</c> elements of <paramref name="file"/> named in
        /// <paramref name="ids"/>, streaming, and stops once all are found. A file that cannot be read is skipped: it
        /// costs the summaries of that assembly, not the build.
        /// </summary>
        static void Read(AdditionalText file, HashSet<string> ids, Dictionary<string, string> found, CancellationToken ct)
        {
            if (file.GetText(ct)?.ToString() is not { Length: > 0 } content)
                return;

            try
            {
                using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                });

                while (ids.Count > 0 && reader.ReadToFollowing("member"))
                {
                    if (reader.GetAttribute("name") is { } name && ids.Remove(name))
                        found[name] = XNode.ReadFrom(reader).ToString();
                }
            }
            catch (XmlException)
            {
                // Not a documentation file, or a broken one. Either way there is nothing more to take from it.
            }
        }
    }
}
