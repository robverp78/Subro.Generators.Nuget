using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using Viaduct.Client;

namespace Viaduct.Tests
{
    /// <summary>
    /// A provider that hands out whatever the test tells it to, including nothing.
    /// </summary>
    internal sealed class TestTokenProvider(string? token) : IViaductAccessTokenProvider
    {
        public int Calls { get; private set; }

        public ValueTask<string?> GetAccessTokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return new ValueTask<string?>(token);
        }
    }

    /// <summary>
    /// The token a client sends. <c>RequiresAuthorization</c> documented client behaviour that did not exist:
    /// only the server side read it, so a client marked as needing authorization sent nothing and collected
    /// 401s.
    /// </summary>
    [NotInParallel]
    public class ClientAuthorizationTests
    {
        private record TestApp(IDocumentedService Client, WebApplication App, ServiceProvider Provider)
            : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                Provider.Dispose();
                await App.DisposeAsync();
            }
        }

        /// <summary>Echoes the Authorization header back as the response body, so the test can see it.</summary>
        private static async Task<TestApp> CreateTestApp(
            Action<ViaductClientOptions> configureClient,
            Action<IServiceCollection>? configureClientServices = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            var app = builder.Build();
            app.Map("/{**rest}", async (HttpContext context) =>
                await context.Response.WriteAsync(
                    $"\"{context.Request.Headers.Authorization.ToString().Replace("\"", string.Empty)}\""));

            await app.StartAsync();

            var testClient = app.GetTestClient();
            var services = new ServiceCollection();
            configureClientServices?.Invoke(services);

            services.CreateAndAddHttpClient<IDocumentedService>(o =>
            {
                o.BaseUrl = testClient.BaseAddress!.ToString();
                configureClient(o);
            }).ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler());

            var provider = services.BuildServiceProvider();
            return new TestApp(provider.GetRequiredService<IDocumentedService>(), app, provider);
        }

        /// <summary>The endpoint answers with the header it saw; the proxy deserializes it as the string.</summary>
        private static Task<string> CallAsync(TestApp test) => test.Client.GetHeaderAsync();

        [Test]
        public async Task TokenFromOptions_IsSentAsABearerToken()
        {
            await using var test = await CreateTestApp(o =>
            {
                o.RequiresAuthorization = true;
                o.GetAccessToken = (_, _) => new ValueTask<string?>("token-from-options");
            });

            await Assert.That(await CallAsync(test)).IsEqualTo("Bearer token-from-options");
        }

        [Test]
        public async Task TokenFromRegisteredProvider_IsSent()
        {
            var provider = new TestTokenProvider("token-from-provider");

            await using var test = await CreateTestApp(
                o => o.RequiresAuthorization = true,
                services => services.AddSingleton<IViaductAccessTokenProvider>(provider));

            await Assert.That(await CallAsync(test)).IsEqualTo("Bearer token-from-provider");
            await Assert.That(provider.Calls).IsEqualTo(1);
        }

        /// <summary>
        /// Asked per request rather than once, so a token that changes — refreshed, or acquired after someone
        /// signs in — is used without rebuilding the client.
        /// </summary>
        [Test]
        public async Task TokenIsRequestedForEveryCall()
        {
            var token = "first";

            await using var test = await CreateTestApp(o =>
            {
                o.RequiresAuthorization = true;
                o.GetAccessToken = (_, _) => new ValueTask<string?>(token);
            });

            await Assert.That(await CallAsync(test)).IsEqualTo("Bearer first");

            token = "second";
            await Assert.That(await CallAsync(test)).IsEqualTo("Bearer second");
        }

        /// <summary>
        /// Nobody is signed in. The call goes out unauthenticated and the server decides — which is a 401 the
        /// caller can act on, not a client-side failure.
        /// </summary>
        [Test]
        public async Task NoTokenAvailable_SendsNoHeader()
        {
            await using var test = await CreateTestApp(o =>
            {
                o.RequiresAuthorization = true;
                o.GetAccessToken = (_, _) => new ValueTask<string?>((string?)null);
            });

            await Assert.That(await CallAsync(test)).IsEqualTo(string.Empty);
        }

        [Test]
        public async Task WithoutRequiresAuthorization_NoTokenIsSent()
        {
            await using var test = await CreateTestApp(
                o => o.GetAccessToken = (_, _) => new ValueTask<string?>("unused"),
                services => services.AddSingleton<IViaductAccessTokenProvider>(new TestTokenProvider("unused")));

            await Assert.That(await CallAsync(test)).IsEqualTo(string.Empty);
        }

        [Test]
        public async Task CustomScheme_IsUsed()
        {
            await using var test = await CreateTestApp(o =>
            {
                o.RequiresAuthorization = true;
                o.AuthorizationScheme = "Token";
                o.GetAccessToken = (_, _) => new ValueTask<string?>("abc");
            });

            await Assert.That(await CallAsync(test)).IsEqualTo("Token abc");
        }

        /// <summary>
        /// A header already on the request was put there deliberately, by code that knows more about that
        /// request than this handler does.
        /// </summary>
        [Test]
        public async Task ExistingAuthorizationHeader_IsNotReplaced()
        {
            var handler = new ViaductAuthorizationHandler(
                new ViaductClientOptions
                {
                    RequiresAuthorization = true,
                    GetAccessToken = (_, _) => new ValueTask<string?>("from-viaduct"),
                })
            {
                InnerHandler = new CapturingHandler(),
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/anything");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "already-here");

            using var invoker = new HttpMessageInvoker(handler);
            await invoker.SendAsync(request, CancellationToken.None);

            await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("already-here");
        }

        /// <summary>Requiring authorization with no way to get a token is a mistake worth saying out loud.</summary>
        [Test]
        public async Task RequiresAuthorizationWithNoTokenSource_Throws()
        {
            // Thrown where the handler pipeline is built, which is when the typed client is first resolved.
            var exception = await Assert.ThrowsAsync<ViaductException>(async () =>
            {
                await using var test = await CreateTestApp(o => o.RequiresAuthorization = true);
                await CallAsync(test);
            });

            await Assert.That(exception!.Message).Contains(nameof(IViaductAccessTokenProvider));
        }

        private sealed class CapturingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
