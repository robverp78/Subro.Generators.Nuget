using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Subro.Generators;
using System.Reflection.Metadata;

namespace RecordForge.WebTest.Services;

public record GenerationResult(string? Code, IReadOnlyList<string> Diagnostics)
{
    public bool HasErrors   => Diagnostics.Any(d => d.StartsWith("[Error]"));
    public bool HasCode     => Code != null;
}

/// <summary>
/// Runs the <see cref="RecordForger"/> source generator against user-supplied C# source
/// without touching the file system.  Assembly metadata is obtained via
/// <see cref="System.Reflection.Assembly.TryGetRawMetadata"/> so this works in WASM.
/// References are built once and cached for the lifetime of the service.
/// </summary>
public class RecordGenerationService
{
    IReadOnlyList<MetadataReference>? _references;

    public GenerationResult Generate(string source, string? extraSource = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp12);

        // The primary source (usings + namespace + attribute + interface) is always
        // parsed; the extra source, when provided, is compiled as its own tree so
        // it stays logically separate from the interface file.
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(source, parseOptions)
        };

        if (!string.IsNullOrWhiteSpace(extraSource))
        {
            trees.Add(CSharpSyntaxTree.ParseText(extraSource, parseOptions));
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: "RecordForgePreview",
            syntaxTrees: trees,
            references: GetReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Report errors in the user's source before even running the generator
        var sourceErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"[Error] {d.GetMessage()}")
            .ToList();

        if (sourceErrors.Count > 0)
            return new(null, sourceErrors);

        // Run the generator
        var driver = CSharpGeneratorDriver
            .Create(new RecordForger().AsSourceGenerator())
            .WithUpdatedParseOptions(parseOptions);

        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out _);

        var result = driver.GetRunResult();

        // Collect generator diagnostics (errors + warnings)
        var generatorDiagnostics = result.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => $"[{d.Severity}] {d.GetMessage()}")
            .ToList();

        if (result.GeneratedTrees.Length == 0)
            return new(null, generatorDiagnostics.Count > 0
                ? generatorDiagnostics
                : ["[Warning] No output was generated. Make sure the interface has at least one property."]);

        var code = string.Join("\n\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        return new(code, generatorDiagnostics);
    }

    // ── Reference building ────────────────────────────────────────────────────

    IReadOnlyList<MetadataReference> GetReferences()
        => _references ??= BuildReferences();

    unsafe IReadOnlyList<MetadataReference> BuildReferences()
    {
        var refs = new List<MetadataReference>();

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            if (asm.FullName?.Contains("Generator") == true) continue;
            if (!asm.TryGetRawMetadata(out byte* blob, out int length)) continue;

            try
            {
                var module = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
                var metadata = AssemblyMetadata.Create(module);
                refs.Add(metadata.GetReference());
            }
            catch
            {
                // Skip assemblies whose PE image cannot be read
            }
        }

        return refs;
    }
}
