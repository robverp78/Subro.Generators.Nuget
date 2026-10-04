using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Viaduct;
using Viaduct.Client;
using Viaduct.Server;

namespace Viaduct.Tests
{
    public record RouteAgreementRequest(string Name);

    // A record rather than a string: a string result is written as text/plain, which the client cannot read.
    public record RouteAgreementResult(string Value);

    /// <summary>
    /// Methods whose routes the client and the server each work out on their own, from the same interface.
    /// </summary>
    [BasePath("/public")]
    public interface IRouteAgreementService
    {
        [CustomRoute("/translations")]
        Task<RouteAgreementResult> GetTranslationsAsync(CancellationToken cancellationToken = default);

        [CustomRoute("/users/{userId}")]
        Task<RouteAgreementResult> GetUserAsync(Guid userId, string search, CancellationToken cancellationToken = default);

        [CustomRoute("/enquiries")]
        Task<RouteAgreementResult> SubmitAsync(RouteAgreementRequest request, CancellationToken cancellationToken = default);

        [CustomRoute("/links")]
        Task<RouteAgreementResult> FindLinkAsync(string url, CancellationToken cancellationToken = default);

        Task<RouteAgreementResult> GetByCodeAsync(int code, CancellationToken cancellationToken = default);
    }

    public class RouteAgreementService : IRouteAgreementService
    {
        public Task<RouteAgreementResult> GetTranslationsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult("translations"));
        public Task<RouteAgreementResult> GetUserAsync(Guid userId, string search, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult($"{userId}|{search}"));
        public Task<RouteAgreementResult> SubmitAsync(RouteAgreementRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult(request.Name));
        public Task<RouteAgreementResult> FindLinkAsync(string url, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult(url));
        public Task<RouteAgreementResult> GetByCodeAsync(int code, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult(code.ToString()));
    }

    /// <summary>
    /// A client and a server generated from the same interface have to agree on every method's HTTP method and
    /// path, or the client calls an endpoint that is not mapped.
    /// </summary>
    /// <remarks>
    /// The client used to build its url from the method name while the server mapped the
    /// <see cref="CustomRouteAttribute"/>, so <c>GetTranslationsAsync</c> was served at <c>/public/translations</c>
    /// and called at <c>/public/GetTranslationsAsync</c>. And with <see cref="ViaductOptions.UseRoutePathParameters"/>
    /// on, the server failed to map a custom route with a parameter in it at all.
    /// </remarks>
    [NotInParallel]
    public class RouteAgreementTests
    {
        /// <summary>Records what the client actually sent.</summary>
        class RecordingHandler : DelegatingHandler
        {
            public List<(string Method, string PathAndQuery)> Calls { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls.Add((request.Method.Method, request.RequestUri!.PathAndQuery));
                return base.SendAsync(request, cancellationToken);
            }
        }

        record TestApp(IRouteAgreementService Client, RecordingHandler Recorder, WebApplication App, ServiceProvider ClientProvider) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await ClientProvider.DisposeAsync();
                await App.DisposeAsync();
            }

            /// <summary>The method and path of the one call made since the last time this was asked.</summary>
            public (string Method, string PathAndQuery) LastCall()
            {
                var call = Recorder.Calls.Single();
                Recorder.Calls.Clear();
                return call;
            }
        }

        static async Task<TestApp> CreateTestApp(bool useRoutePathParameters)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddScoped<IRouteAgreementService, RouteAgreementService>();

            var app = builder.Build();
            app.AddEndpoints<IRouteAgreementService>(o => o.UseRoutePathParameters = useRoutePathParameters);
            await app.StartAsync();

            var recorder = new RecordingHandler();
            var testHttpClient = app.GetTestClient();
            var clientServices = new ServiceCollection();
            clientServices.CreateAndAddHttpClient<IRouteAgreementService>(o =>
            {
                o.UseRoutePathParameters = useRoutePathParameters;
                o.BaseUrl = testHttpClient.BaseAddress!.ToString();
            })
                .ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler())
                .AddHttpMessageHandler(() => recorder);
            var clientProvider = clientServices.BuildServiceProvider();

            return new TestApp(clientProvider.GetRequiredService<IRouteAgreementService>(), recorder, app, clientProvider);
        }

        [Test]
        [Arguments(true)]
        [Arguments(false)]
        public async Task CustomRoute_WithoutParameters(bool useRoutePathParameters)
        {
            await using var test = await CreateTestApp(useRoutePathParameters);

            await Assert.That((await test.Client.GetTranslationsAsync()).Value).IsEqualTo("translations");
            await Assert.That(test.LastCall()).IsEqualTo(("GET", "/public/translations"));
        }

        /// <summary>
        /// A route that names a parameter is the method's whole path: the other parameters go in the query
        /// string, whatever <see cref="ViaductOptions.UseRoutePathParameters"/> says.
        /// </summary>
        [Test]
        [Arguments(true)]
        [Arguments(false)]
        public async Task CustomRoute_WithRouteParameter(bool useRoutePathParameters)
        {
            await using var test = await CreateTestApp(useRoutePathParameters);
            var userId = Guid.NewGuid();

            await Assert.That((await test.Client.GetUserAsync(userId, "smith")).Value).IsEqualTo($"{userId}|smith");
            await Assert.That(test.LastCall()).IsEqualTo(("GET", $"/public/users/{userId}?search=smith"));
        }

        [Test]
        [Arguments(true)]
        [Arguments(false)]
        public async Task CustomRoute_WithBodyParameterNamedRequest(bool useRoutePathParameters)
        {
            await using var test = await CreateTestApp(useRoutePathParameters);

            await Assert.That((await test.Client.SubmitAsync(new("Ada"))).Value).IsEqualTo("Ada");
            await Assert.That(test.LastCall()).IsEqualTo(("POST", "/public/enquiries"));
        }

        [Test]
        [Arguments(true, "/public/links/example")]
        [Arguments(false, "/public/links?url=example")]
        public async Task CustomRoute_WithParameterNamedUrl(bool useRoutePathParameters, string expectedPath)
        {
            await using var test = await CreateTestApp(useRoutePathParameters);

            await Assert.That((await test.Client.FindLinkAsync("example")).Value).IsEqualTo("example");
            await Assert.That(test.LastCall()).IsEqualTo(("GET", expectedPath));
        }

        [Test]
        [Arguments(true, "/public/GetByCodeAsync/5")]
        [Arguments(false, "/public/GetByCodeAsync?code=5")]
        public async Task DefaultRoute(bool useRoutePathParameters, string expectedPath)
        {
            await using var test = await CreateTestApp(useRoutePathParameters);

            await Assert.That((await test.Client.GetByCodeAsync(5)).Value).IsEqualTo("5");
            await Assert.That(test.LastCall()).IsEqualTo(("GET", expectedPath));
        }
    }
}
