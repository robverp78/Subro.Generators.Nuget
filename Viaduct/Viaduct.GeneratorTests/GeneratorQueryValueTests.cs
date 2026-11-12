using Microsoft.CodeAnalysis;

namespace Viaduct.Tests
{
    /// <summary>
    /// How the generated client writes each query parameter: through the <c>AppendQueryValue</c> overload for its type,
    /// tested for null only when it can be null, and without boxing it.
    /// </summary>
    public class GeneratorQueryValueTests : GeneratorTestsBase
    {
        const string Source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Viaduct;
            using Viaduct.Client;
            using Viaduct.Server;
            using Microsoft.Extensions.DependencyInjection;

            namespace TestApp
            {
                public enum Kind { First, Second }

                [BasePath("/find")]
                public interface IFindApi
                {
                    [CustomRoute("/items")]
                    Task<string> FindAsync(int count, DateOnly? on, string? text, Kind kind, Guid? owner, CancellationToken cancellationToken = default);
                }

                class Startup
                {
                    void Configure(IServiceCollection services)
                    {
                        services.CreateAndAddHttpClient<IFindApi>();
                        ServerMappings.RegisterEndpoints<IFindApi>();
                    }
                }
            }
            """;

        const string Write = "global::Viaduct.Client.ViaductClientFunctions.AppendQueryValue(__sb, ";

        [Test]
        public async Task EachQueryValue_IsTestedForNull_OnlyAsItsTypeAllows_AndCompiles()
        {
            var compilation = RunFullPipeline(Source);

            var errors = compilation.Compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error && !d.ToString().Contains("Viaduct_AotTypes"))
                .ToArray();
            await Assert.That(errors).IsEmpty();

            var client = string.Join("\n", compilation.Compilation.SyntaxTrees
                .Select(tree => tree.ToString())
                .Where(text => text.Contains("FindAsync") && text.Contains("AppendQueryValue")));

            // A value type that cannot be null: written as it is, nothing tested, nothing boxed.
            await Assert.That(client).Contains(Write + "count)");
            await Assert.That(client).Contains(Write + "kind)");
            // Nullable<T>: unwrapped, so that the overload for T is the one called.
            await Assert.That(client).Contains("on is { } __onValue ? " + Write + "__onValue) : null");
            await Assert.That(client).Contains("owner is { } __ownerValue ? " + Write + "__ownerValue) : null");
            // A reference type: a reference comparison.
            await Assert.That(client).Contains("text is null ? null : " + Write + "text)");
            // Nothing boxed to test for null.
            await Assert.That(client).DoesNotContain("(object?)");
            // The address is written by a method of its own when the options allow it, made once per registration.
            await Assert.That(client).Contains("string FindAsync_Url_0(int count, System.DateOnly? on, string? text, TestApp.Kind kind, System.Guid? owner)");
            await Assert.That(client).Contains("FindAsync_UrlCreator_0 = FindAsync_Url_0;");
            await Assert.That(client).Contains("if (on is { } __onValue) { __sb.Append(__sep).Append(\"on=\"); " + Write + "__onValue); __sep = '&'; }");
        }
    }
}
