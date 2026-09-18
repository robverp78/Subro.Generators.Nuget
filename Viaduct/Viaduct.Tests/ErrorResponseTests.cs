using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Viaduct.Client;

namespace Viaduct.Tests
{
    /// <summary>
    /// What a caller is told when the server refuses. The status code is the whole point: a permission
    /// decision, a missing record and a conflict are different situations, and code that has to tell them
    /// apart cannot do it from an exception message.
    /// </summary>
    [NotInParallel]
    public class ErrorResponseTests
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

        /// <summary>
        /// A server that answers everything the same way, so the test controls exactly what comes back.
        /// </summary>
        private static async Task<TestApp> CreateTestApp(int? statusCode, string? body = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            var app = builder.Build();

            // No status code means map nothing at all, which is how a real 404 comes about.
            if (statusCode is { } status)
            {
                app.Map("/{**rest}", async (HttpContext context) =>
                {
                    context.Response.StatusCode = status;
                    if (body is not null)
                        await context.Response.WriteAsync(body);
                });
            }

            await app.StartAsync();

            var testClient = app.GetTestClient();
            var services = new ServiceCollection();
            services.CreateAndAddHttpClient<IDocumentedService>(o => o.BaseUrl = testClient.BaseAddress!.ToString())
                .ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler());

            var provider = services.BuildServiceProvider();
            return new TestApp(provider.GetRequiredService<IDocumentedService>(), app, provider);
        }

        [Test]
        public async Task FailureResponse_CarriesTheStatusCodeAndBody()
        {
            await using var test = await CreateTestApp(StatusCodes.Status403Forbidden, "not your club");

            var exception = await Assert.ThrowsAsync<ViaductHttpException>(async () => await test.Client.GetAnswerAsync());

            await Assert.That(exception!.StatusCode).IsEqualTo(403);
            await Assert.That(exception.ResponseBody).IsEqualTo("not your club");
            await Assert.That(exception.HttpMethod).IsEqualTo("GET");
            await Assert.That(exception.RequestUri).Contains("GetAnswerAsync");

            // The message is what an unhandled failure shows in a log, so it has to say all of it.
            await Assert.That(exception.Message).Contains("403");
            await Assert.That(exception.Message).Contains("not your club");
        }

        /// <summary>A missing route is a 404 like any other, not a transport failure.</summary>
        [Test]
        public async Task UnmappedRoute_ReportsNotFound()
        {
            await using var test = await CreateTestApp(statusCode: null);

            var exception = await Assert.ThrowsAsync<ViaductHttpException>(async () => await test.Client.GetAnswerAsync());

            await Assert.That(exception!.StatusCode).IsEqualTo(404);
        }

        /// <summary>
        /// Existing code catches <see cref="ViaductException"/>; it must keep catching these.
        /// </summary>
        [Test]
        public async Task HttpFailure_IsStillAViaductException()
        {
            await using var test = await CreateTestApp(StatusCodes.Status500InternalServerError);

            var exception = await Assert.ThrowsAsync<ViaductException>(async () => await test.Client.GetAnswerAsync());

            await Assert.That(exception).IsTypeOf<ViaductHttpException>();
        }

        /// <summary>
        /// A cancelled call is the caller's own doing. Wrapping it would hide it from every
        /// <c>catch (OperationCanceledException)</c> between here and them.
        /// </summary>
        [Test]
        public async Task Cancellation_IsNotReportedAsAFailedCall()
        {
            await using var test = await CreateTestApp(StatusCodes.Status200OK, "1");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await test.Client.GetAnswerAsync(cancelled.Token));
        }
    }
}
