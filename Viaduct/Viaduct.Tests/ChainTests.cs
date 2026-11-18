using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using Viaduct.Client;
using Viaduct.Server;

namespace Viaduct.Tests
{
    /// <summary>
    /// Registrations that share options through a chain, <c>services.ViaductEndpoints(...)</c> and
    /// <c>services.ViaductClients(...)</c>: the chain's options reach its registrations and no others, a registration's own
    /// come after them, and the process-wide defaults are left alone.
    /// </summary>
    [NotInParallel]
    public class ChainTests
    {
        static WebApplicationBuilder CreateBuilder()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            return builder;
        }

        static async Task<string[]> MappedRoutes(WebApplicationBuilder builder)
        {
            await using var app = builder.Build();
            app.AddRegisteredEndpoints();
            await app.StartAsync();
            return app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Select(static e => e.RoutePattern.RawText!)
                .ToArray();
        }

        [Test]
        public async Task TheChainsOptions_ReachEachRegistrationOnIt_AndNoOther()
        {
            var builder = CreateBuilder();
            builder.Services.AddScoped<IClubScopedService, ClubScopedService>();
            builder.Services.ViaductEndpoints(o => o.BasePath = "/chained")
                .AddScopedAndMap<IConstrainedService, ConstrainedService>()
                .RegisterEndpoints<IClubScopedService>();
            builder.Services.AddScopedAndMap<IDocumentedService, DocumentedService>();

            var routes = await MappedRoutes(builder);

            await Assert.That(routes).Contains("/chained/constrained/users/{userId:guid}");
            await Assert.That(routes).Contains("/chained/clubs/{clubId:guid}/name");
            await Assert.That(routes.Where(static r => r.StartsWith("/documented/"))).IsNotEmpty();
            await Assert.That(routes.Where(static r => r.Contains("documented") && r.StartsWith("/chained"))).IsEmpty();
            await Assert.That(ViaductServerOptions.Default.BasePath).IsNull();
        }

        [Test]
        public async Task ARegistrationsOwnOptions_ComeAfterTheChains()
        {
            var builder = CreateBuilder();
            builder.Services.ViaductEndpoints(o => { o.BasePath = "/chained"; o.UseRouteTypeConstraints = false; })
                .AddScopedAndMap<IConstrainedService, ConstrainedService>(o => o.BasePath = "/own");

            var routes = await MappedRoutes(builder);

            // The registration's base path replaces the chain's; what it does not set stays as the chain has it.
            await Assert.That(routes).Contains("/own/constrained/users/{userId}");
            await Assert.That(routes.Where(static r => r.StartsWith("/chained"))).IsEmpty();
        }

        [Test]
        public async Task TheChainsOptionsAndSetup_ReachEachClientOnIt_TheirOwnAfter()
        {
            var recorder = new Recorder();
            var services = new ServiceCollection();
            services.ViaductClients(o => o.BaseUrl = "https://chain.test/", client => client
                    .AddHttpMessageHandler(() => new Step("chain"))
                    .ConfigurePrimaryHttpMessageHandler(() => recorder))
                .Add<IConstrainedService>()
                .Add<IClubScopedService>(o => o.BaseUrl = "https://own.test/", client => client.AddHttpMessageHandler(() => new Step("own")));
            await using var provider = services.BuildServiceProvider();

            var clubId = Guid.NewGuid();
            await provider.GetRequiredService<IConstrainedService>().GetCodeAsync(3);
            await provider.GetRequiredService<IClubScopedService>().GetNameAsync(clubId);

            await Assert.That(recorder.Calls.Count).IsEqualTo(2);
            await Assert.That(recorder.Calls[0].Uri).IsEqualTo("https://chain.test/constrained/codes/3");
            await Assert.That(recorder.Calls[0].Steps).IsEqualTo("chain");
            await Assert.That(recorder.Calls[1].Uri).IsEqualTo($"https://own.test/clubs/{clubId}/name");
            await Assert.That(recorder.Calls[1].Steps).IsEqualTo("chain,own");
            await Assert.That(ViaductClientOptions.Default.BaseUrl).IsNull();
        }

        /// <summary>Marks a request with its name on the way out, so the order the handlers ran in shows.</summary>
        sealed class Step(string name) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Headers.Add("X-Step", name);
                return base.SendAsync(request, cancellationToken);
            }
        }

        sealed class Recorder : HttpMessageHandler
        {
            public List<(string Uri, string Steps)> Calls { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls.Add((request.RequestUri!.ToString(),
                    request.Headers.TryGetValues("X-Step", out var steps) ? string.Join(",", steps) : ""));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"value":"ok"}""", Encoding.UTF8, "application/json"),
                });
            }
        }
    }
}
