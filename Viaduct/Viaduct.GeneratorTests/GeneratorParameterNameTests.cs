using Microsoft.CodeAnalysis;

namespace Viaduct.Tests
{
    /// <summary>
    /// The names of a method's parameters are the interface author's to choose, and the generated code has to
    /// compile whatever they are.
    /// </summary>
    /// <remarks>
    /// The generated client method declares its own locals in the same scope as the method's parameters, and the
    /// url builder is a lambda whose parameters are the method's. Both used to be plain names — <c>request</c>,
    /// <c>url</c>, <c>sb</c> — so <c>Task SubmitAsync(SomeRequest request)</c> failed with CS0136 in code the
    /// author never wrote.
    /// </remarks>
    public class GeneratorParameterNameTests : GeneratorTestsBase
    {
        const string Source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Viaduct;
            using Viaduct.Client;
            using Viaduct.Server;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.AspNetCore.Routing;

            namespace TestApp
            {
                public record SomeRequest(string Name);

                [BasePath("/names")]
                public interface INamesApi
                {
                    // a body parameter named like the generated request local
                    Task SubmitAsync(SomeRequest request, CancellationToken cancellationToken = default);

                    // query parameters named like every other local and lambda parameter of the generated code
                    Task<string> GetUrlAsync(string url, string info, string response, string responseTask, CancellationToken cancellationToken = default);
                    Task<string> GetBuilderAsync(string sb, string parName, string res, string buildInfo, CancellationToken cancellationToken = default);
                    Task<string> GetOptionsAsync(string Options, string BasePath, string service, CancellationToken cancellationToken = default);

                    // the same, in a method with a route of its own
                    [CustomRoute("/items/{request}")]
                    Task<string> GetItemAsync(int request, string url, string sb, CancellationToken cancellationToken = default);
                }

                class Startup
                {
                    void Configure(IServiceCollection services)
                    {
                        services.CreateAndAddHttpClient<INamesApi>();
                        ServerMappings.RegisterEndpoints<INamesApi>();
                    }
                }
            }
            """;

        [Test]
        public async Task ParametersNamedLikeGeneratedLocals_Compile()
        {
            var compilation = RunFullPipeline(Source);

            var errors = compilation.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error
                && !d.ToString().Contains("Viaduct_AotTypes"))
                .ToArray();

            await Assert.That(errors).IsEmpty();
        }
    }
}
