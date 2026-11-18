using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using Viaduct.Server;

namespace Viaduct.Tests
{
    /// <summary>
    /// Endpoints registered with the services (AddScopedAndMap and the like) and mapped by AddRegisteredEndpoints. They are
    /// kept in the service collection they were registered on: with one queue for the whole process, the first application
    /// to map took every application's endpoints and the next got none, which is what two test hosts in one run are.
    /// </summary>
    [NotInParallel]
    public class RegisteredEndpointsTests
    {
        static WebApplicationBuilder CreateBuilder()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            return builder;
        }

        static string[] RoutePatterns(WebApplication app)
            => app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Select(static e => e.RoutePattern.RawText!)
                .ToArray();

        [Test]
        public async Task TwoApplications_BuiltSideBySide_EachMapTheirOwn()
        {
            // Both registered before either maps: the order a test run with two hosts gets.
            var first = CreateBuilder();
            first.Services.AddScopedAndMap<IDocumentedService, DocumentedService>();
            var second = CreateBuilder();
            second.Services.AddScopedAndMap<IDocumentedService, DocumentedService>();

            await using var firstApp = first.Build();
            await using var secondApp = second.Build();
            firstApp.AddRegisteredEndpoints();
            secondApp.AddRegisteredEndpoints();
            await firstApp.StartAsync();
            await secondApp.StartAsync();

            var firstRoutes = RoutePatterns(firstApp);
            var secondRoutes = RoutePatterns(secondApp);

            await Assert.That(firstRoutes).IsNotEmpty();
            await Assert.That(firstRoutes.Distinct().Count()).IsEqualTo(firstRoutes.Length);
            await Assert.That(secondRoutes.Length).IsEqualTo(firstRoutes.Length);

            using var firstAnswer = await firstApp.GetTestClient().GetAsync("/documented/GetAnswerAsync");
            using var secondAnswer = await secondApp.GetTestClient().GetAsync("/documented/GetAnswerAsync");
            await Assert.That(firstAnswer.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(secondAnswer.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        [Test]
        public async Task MappingTwice_MapsEachRegistrationOnce()
        {
            var builder = CreateBuilder();
            builder.Services.AddScopedAndMap<IDocumentedService, DocumentedService>();
            await Assert.That(PendingMappers.Count(builder.Services)).IsEqualTo(1);

            await using var app = builder.Build();
            app.AddRegisteredEndpoints();
            app.AddRegisteredEndpoints();
            await app.StartAsync();

            var routes = RoutePatterns(app);
            await Assert.That(routes).IsNotEmpty();
            await Assert.That(routes.Distinct().Count()).IsEqualTo(routes.Length);
            await Assert.That(PendingMappers.Count(builder.Services)).IsEqualTo(0);
        }

        [Test]
        public async Task NothingRegistered_MapsNothing()
        {
            await using var app = CreateBuilder().Build();
            app.AddRegisteredEndpoints();
            await app.StartAsync();

            await Assert.That(RoutePatterns(app)).IsEmpty();
        }
    }
}
