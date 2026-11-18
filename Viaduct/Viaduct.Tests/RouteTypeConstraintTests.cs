using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using Viaduct;
using Viaduct.Client;
using Viaduct.Server;

namespace Viaduct.Tests
{
    /// <summary>
    /// Two routes of one shape, a user by id and a search by text, which without their types ASP.NET Core cannot tell
    /// apart; and the other places a route parameter comes from.
    /// </summary>
    [BasePath("/constrained")]
    public interface IConstrainedService
    {
        [CustomRoute("/users/{userId}")]
        Task<RouteAgreementResult> GetUserAsync(Guid userId, CancellationToken cancellationToken = default);

        [CustomRoute("/users/{search}")]
        Task<RouteAgreementResult> FindUsersAsync(string search, CancellationToken cancellationToken = default);

        /// <summary>Its parameters appended to the path, as the options have it by default.</summary>
        Task<RouteAgreementResult> GetByNumberAsync(int number, bool? includeEnded = null, CancellationToken cancellationToken = default);

        /// <summary>A constraint of the route's own, which stays.</summary>
        [CustomRoute("/codes/{code:min(1)}")]
        Task<RouteAgreementResult> GetCodeAsync(int code, CancellationToken cancellationToken = default);
    }

    public class ConstrainedService : IConstrainedService
    {
        public Task<RouteAgreementResult> GetUserAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new RouteAgreementResult($"user|{userId}"));

        public Task<RouteAgreementResult> FindUsersAsync(string search, CancellationToken cancellationToken = default)
            => Task.FromResult(new RouteAgreementResult($"search|{search}"));

        public Task<RouteAgreementResult> GetByNumberAsync(int number, bool? includeEnded = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new RouteAgreementResult($"{number}|{includeEnded}"));

        public Task<RouteAgreementResult> GetCodeAsync(int code, CancellationToken cancellationToken = default)
            => Task.FromResult(new RouteAgreementResult(code.ToString()));
    }

    /// <summary>A parameter in the interface's base path, which every method has.</summary>
    [BasePath("/clubs/{clubId}")]
    public interface IClubScopedService
    {
        [CustomRoute("/name")]
        Task<RouteAgreementResult> GetNameAsync(Guid clubId, CancellationToken cancellationToken = default);
    }

    public class ClubScopedService : IClubScopedService
    {
        public Task<RouteAgreementResult> GetNameAsync(Guid clubId, CancellationToken cancellationToken = default)
            => Task.FromResult(new RouteAgreementResult($"club|{clubId}"));
    }

    /// <summary>
    /// <see cref="ViaductServerOptions.UseRouteTypeConstraints"/>: each route parameter is mapped with its type, so routes
    /// of one shape are told apart by it, and the client's paths stay as they were.
    /// </summary>
    public class RouteTypeConstraintTests
    {
        static async Task<WebApplication> CreateServer(Action<ViaductServerOptions>? configure = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddScoped<IConstrainedService, ConstrainedService>();
            builder.Services.AddScoped<IClubScopedService, ClubScopedService>();
            builder.Services.AddJsonTypeResolvers();

            var app = builder.Build();
            app.AddEndpoints<IConstrainedService>(configure);
            app.AddEndpoints<IClubScopedService>(configure);
            await app.StartAsync();
            return app;
        }

        static string[] RoutePatterns(WebApplication app)
            => app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Select(static e => e.RoutePattern.RawText!)
                .ToArray();

        static ServiceProvider CreateClients(WebApplication app)
        {
            var services = new ServiceCollection();
            var baseUrl = app.GetTestClient().BaseAddress!.ToString();
            services.CreateAndAddHttpClient<IConstrainedService>(o => o.BaseUrl = baseUrl)
                .ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler());
            services.CreateAndAddHttpClient<IClubScopedService>(o => o.BaseUrl = baseUrl)
                .ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler());
            return services.BuildServiceProvider();
        }

        [Test]
        public async Task EachRouteParameter_IsMappedWithItsType()
        {
            await using var app = await CreateServer();

            await Assert.That(RoutePatterns(app)).IsEquivalentTo([
                "/constrained/users/{userId:guid}",
                "/constrained/users/{search}",
                "/constrained/GetByNumberAsync/{number:int}/{includeended:bool?}",
                "/constrained/codes/{code:min(1)}",
                "/clubs/{clubId:guid}/name",
            ]);
        }

        [Test]
        public async Task TwoRoutesOfOneShape_AreToldApart_ByTheirTypes()
        {
            await using var app = await CreateServer();
            await using var clients = CreateClients(app);
            var client = clients.GetRequiredService<IConstrainedService>();
            var userId = Guid.NewGuid();

            await Assert.That((await client.GetUserAsync(userId)).Value).IsEqualTo($"user|{userId}");
            await Assert.That((await client.FindUsersAsync("rob")).Value).IsEqualTo("search|rob");
        }

        [Test]
        public async Task TheClientsPaths_StillReachEveryEndpoint()
        {
            await using var app = await CreateServer();
            await using var clients = CreateClients(app);
            var client = clients.GetRequiredService<IConstrainedService>();
            var clubId = Guid.NewGuid();

            await Assert.That((await client.GetByNumberAsync(7)).Value).IsEqualTo("7|");
            await Assert.That((await client.GetByNumberAsync(7, includeEnded: true)).Value).IsEqualTo("7|True");
            await Assert.That((await client.GetCodeAsync(3)).Value).IsEqualTo("3");
            await Assert.That((await clients.GetRequiredService<IClubScopedService>().GetNameAsync(clubId)).Value).IsEqualTo($"club|{clubId}");
        }

        [Test]
        public async Task AValueNotOfTheType_MatchesNoEndpoint()
        {
            await using var app = await CreateServer();
            var http = app.GetTestClient();

            await Assert.That((await http.GetAsync("/constrained/GetByNumberAsync/seven")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That((await http.GetAsync("/clubs/oslo/name")).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        [Test]
        public async Task SwitchedOff_TheRoutesAreMappedAsWritten()
        {
            await using var app = await CreateServer(o => o.UseRouteTypeConstraints = false);

            await Assert.That(RoutePatterns(app)).Contains("/constrained/users/{userId}");
            await Assert.That(RoutePatterns(app)).Contains("/constrained/GetByNumberAsync/{number}/{includeended?}");
            await Assert.That(RoutePatterns(app)).Contains("/clubs/{clubId}/name");
        }
    }
}
