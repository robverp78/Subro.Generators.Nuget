using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Viaduct.Server;

namespace Viaduct.Tests
{
    /// <summary>
    /// A documented service: the XML comments below are the subject of these tests, not decoration.
    /// </summary>
    [BasePath("/documented")]
    public interface IDocumentedService
    {
        /// <summary>Fetches the answer.</summary>
        /// <remarks>
        /// Everything the caller should know before asking,
        /// spread over more than one line.
        /// </remarks>
        Task<int> GetAnswerAsync(CancellationToken ct = default);

        /// <summary>Overridden by the attributes.</summary>
        [ViaductSummary("Summary from the attribute")]
        [ViaductDescription("Description from the attribute")]
        [ViaductEndpointName("TheChosenName")]
        Task<int> GetOverriddenAsync(CancellationToken ct = default);

        Task<int> GetUndocumentedAsync(CancellationToken ct = default);

        /// <summary>Paired with the one below: neither may lose its "Async" and collide with the other.</summary>
        Task<int> GetPairAsync(CancellationToken ct = default);

        /// <summary>See above.</summary>
        Task<int> GetPair(CancellationToken ct = default);

        /// <summary>An overload, so no endpoint name can be generated for either of these.</summary>
        Task<int> GetOverloadedAsync(int id, CancellationToken ct = default);

        /// <summary>The other overload.</summary>
        Task<int> GetOverloadedAsync(string name, CancellationToken ct = default);
    }

    public sealed class DocumentedService : IDocumentedService
    {
        public Task<int> GetAnswerAsync(CancellationToken ct = default) => Task.FromResult(42);
        public Task<int> GetOverriddenAsync(CancellationToken ct = default) => Task.FromResult(1);
        public Task<int> GetUndocumentedAsync(CancellationToken ct = default) => Task.FromResult(2);
        public Task<int> GetPairAsync(CancellationToken ct = default) => Task.FromResult(3);
        public Task<int> GetPair(CancellationToken ct = default) => Task.FromResult(4);
        public Task<int> GetOverloadedAsync(int id, CancellationToken ct = default) => Task.FromResult(id);
        public Task<int> GetOverloadedAsync(string name, CancellationToken ct = default) => Task.FromResult(name.Length);
    }

    /// <summary>
    /// What an endpoint tells the OpenAPI document about itself. Without this metadata a generated API
    /// documents as a list of untitled paths with empty operationIds, which is what client generators turn
    /// into unreadable method names.
    /// </summary>
    [NotInParallel]
    public class EndpointMetadataTests
    {
        private static async Task<WebApplication> CreateApp(Action<ViaductServerOptions>? configure = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddScopedAndMap<IDocumentedService, DocumentedService>(configure);

            var app = builder.Build();
            app.AddRegisteredEndpoints();
            await app.StartAsync();
            return app;
        }

        /// <summary>The endpoint whose route ends in <paramref name="route"/>.</summary>
        private static Endpoint Endpoint(WebApplication app, string route)
            => app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .Single(e => e is RouteEndpoint r
                    && r.RoutePattern.RawText!.EndsWith(route, StringComparison.OrdinalIgnoreCase));

        [Test]
        public async Task SummaryAndDescription_ComeFromTheXmlComments()
        {
            await using var app = await CreateApp();
            var endpoint = Endpoint(app, "/GetAnswerAsync");

            await Assert.That(endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary)
                .IsEqualTo("Fetches the answer.");

            // The comment spans two source lines; the description is one run of prose.
            await Assert.That(endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description)
                .IsEqualTo("Everything the caller should know before asking, spread over more than one line.");
        }

        [Test]
        public async Task Attributes_WinOverXmlComments()
        {
            await using var app = await CreateApp();
            var endpoint = Endpoint(app, "/GetOverriddenAsync");

            await Assert.That(endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary)
                .IsEqualTo("Summary from the attribute");
            await Assert.That(endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description)
                .IsEqualTo("Description from the attribute");
            await Assert.That(endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
                .IsEqualTo("TheChosenName");
        }

        [Test]
        public async Task EndpointName_IsGeneratedFromTheInterfaceAndMethod()
        {
            await using var app = await CreateApp();

            await Assert.That(Endpoint(app, "/GetAnswerAsync").Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
                .IsEqualTo("DocumentedService_GetAnswer");
        }

        /// <summary>
        /// <c>GetPair</c> and <c>GetPairAsync</c> are two methods, not one: dropping the suffix would give them
        /// one name, and ASP.NET Core refuses to start an application with a duplicate endpoint name.
        /// </summary>
        [Test]
        public async Task AsyncSuffix_IsKeptWhenDroppingItWouldCollide()
        {
            await using var app = await CreateApp();

            await Assert.That(Endpoint(app, "/GetPairAsync").Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
                .IsEqualTo("DocumentedService_GetPairAsync");
            await Assert.That(Endpoint(app, "/GetPair").Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
                .IsEqualTo("DocumentedService_GetPair");
        }

        /// <summary>An overload gets no name: which of the two it would refer to is a guess.</summary>
        [Test]
        public async Task Overloads_GetNoEndpointName()
        {
            await using var app = await CreateApp();
            var overloads = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .Where(e => e is RouteEndpoint r && r.RoutePattern.RawText!.Contains("GetOverloadedAsync"))
                .ToList();

            await Assert.That(overloads.Count).IsEqualTo(2);
            foreach (var endpoint in overloads)
                await Assert.That(endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()).IsNull();
        }

        [Test]
        public async Task Endpoints_AreTaggedWithTheInterfaceName()
        {
            await using var app = await CreateApp();

            await Assert.That(Endpoint(app, "/GetAnswerAsync").Metadata.GetMetadata<ITagsMetadata>()?.Tags)
                .Contains("DocumentedService");
        }

        /// <summary>An undocumented method still gets its name and tag, and simply has nothing to say.</summary>
        [Test]
        public async Task UndocumentedMethod_HasNameAndTagButNoSummary()
        {
            await using var app = await CreateApp();
            var endpoint = Endpoint(app, "/GetUndocumentedAsync");

            await Assert.That(endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
                .IsEqualTo("DocumentedService_GetUndocumented");
            await Assert.That(endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()).IsNull();
        }

        [Test]
        public async Task Metadata_CanBeTurnedOff()
        {
            await using var app = await CreateApp(o =>
            {
                o.IncludeEndpointMetadata = false;
                o.GenerateEndpointNames = false;
            });

            var endpoint = Endpoint(app, "/GetAnswerAsync");

            await Assert.That(endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()).IsNull();
            await Assert.That(endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()).IsNull();
            await Assert.That(endpoint.Metadata.GetMetadata<ITagsMetadata>()).IsNull();
        }
    }
}
