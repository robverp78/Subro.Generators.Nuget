using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Viaduct;
using Viaduct.Client;
using Viaduct.Server;

namespace Viaduct.Tests
{
    /// <summary>Its routes declared once, on the interface, for both sides: parameters go in the query.</summary>
    [BasePath("/routed")]
    [ViaductRoutes(UseRoutePathParameters = false)]
    public interface IRoutedService
    {
        Task<RouteAgreementResult> GetByCodeAsync(int code, CancellationToken cancellationToken = default);
    }

    public class RoutedService : IRoutedService
    {
        public Task<RouteAgreementResult> GetByCodeAsync(int code, CancellationToken cancellationToken = default)
            => Task.FromResult(new RouteAgreementResult(code.ToString()));
    }

    /// <summary>
    /// <see cref="ViaductRoutesAttribute"/>: the server maps and the client calls the routes it describes, with neither
    /// told at its registration; a registration's configure can still change it on its own side.
    /// </summary>
    [NotInParallel]
    public class ViaductRoutesTests
    {
        sealed class RecordingHandler : DelegatingHandler
        {
            public List<string> Paths { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Paths.Add(request.RequestUri!.PathAndQuery);
                return base.SendAsync(request, cancellationToken);
            }
        }

        static async Task<WebApplication> CreateServer(Action<ViaductServerOptions>? configure = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddScoped<IRoutedService, RoutedService>();
            builder.Services.AddJsonTypeResolvers();

            var app = builder.Build();
            app.AddEndpoints<IRoutedService>(configure);
            await app.StartAsync();
            return app;
        }

        static string[] RoutePatterns(WebApplication app)
            => app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Select(static e => e.RoutePattern.RawText!)
                .ToArray();

        [Test]
        public async Task TheClientCalls_WhatTheServerMaps_FromTheAttributeAlone()
        {
            await using var app = await CreateServer();
            await Assert.That(RoutePatterns(app)).IsEquivalentTo(["/routed/GetByCodeAsync"]);

            var recorder = new RecordingHandler();
            var clientServices = new ServiceCollection();
            clientServices.CreateAndAddHttpClient<IRoutedService>(o => o.BaseUrl = app.GetTestClient().BaseAddress!.ToString())
                .ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler())
                .AddHttpMessageHandler(() => recorder);
            await using var clientProvider = clientServices.BuildServiceProvider();
            var client = clientProvider.GetRequiredService<IRoutedService>();

            await Assert.That((await client.GetByCodeAsync(5)).Value).IsEqualTo("5");
            await Assert.That(recorder.Paths.Single()).IsEqualTo("/routed/GetByCodeAsync?code=5");
        }

        [Test]
        public async Task ARegistrationsConfigure_ComesAfterTheAttribute()
        {
            await using var app = await CreateServer(o => o.UseRoutePathParameters = true);

            await Assert.That(RoutePatterns(app)).IsEquivalentTo(["/routed/GetByCodeAsync/{code:int}"]);
        }
    }
}
