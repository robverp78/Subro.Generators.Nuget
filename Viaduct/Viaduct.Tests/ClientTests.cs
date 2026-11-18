using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using Viaduct;
using Viaduct.Client;
using Viaduct.Client.UrlBuilding;
using Viaduct.Examples;
using Viaduct.Server;

namespace Viaduct.Tests
{
    [NotInParallel]
    public class ClientTests
    {
        [After(Test)]
        public void ResetGlobalOptions()
        {
            ViaductServerOptions.Default.UseInterfaceNameForPath = false;
        }

        private record TestApp(IViaductTestInterface Client, WebApplication App, ServiceProvider ClientProvider) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                ClientProvider.Dispose();
                await App.DisposeAsync();
                
            }
        }

        private static async Task<TestApp> CreateTestApp()
        {
            // Server side: mount endpoints backed by the real implementation
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ViaductServerOptions.Default.UseInterfaceNameForPath = true;
            builder.Services.AddScoped<IViaductTestInterface, TestApiImplementation>();

            var app = builder.Build();
            app.AddEndpoints<IViaductTestInterface>();
            await app.StartAsync();

            // Client side: separate DI container with the test HttpClient
            var testHttpClient = app.GetTestClient();
            var clientServices = new ServiceCollection();
            
            clientServices.AddSingleton(testHttpClient);
            clientServices.CreateAndAddHttpClient<IViaductTestInterface>(o =>
            {
                o.UseInterfaceNameForPath = true;
                o.BaseUrl = testHttpClient.BaseAddress!.ToString();
            })
                .ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler());
            var clientProvider = clientServices.BuildServiceProvider();
            var client = clientProvider.GetRequiredService<IViaductTestInterface>();

            return new TestApp(client, app, clientProvider);
        }

        [Test]
        public async Task Client_CanCallEndpoints()
        {
            await using var test = await CreateTestApp();

            // Async void
            await test.Client.ExecuteAsync();

            // Async returning value
            var intResult = await test.Client.GetAnIntAsync();
            await Assert.That(intResult).IsEqualTo(42);

            // Route parameter
            var byIdResult = await test.Client.GetById(7);
            await Assert.That(byIdResult).IsEqualTo(14);

            // Complex return type
            var user = await test.Client.GetUserAsync(5, CancellationToken.None);
            await Assert.That(user).IsNotNull();
            await Assert.That(user!.Id).IsEqualTo(5);
            await Assert.That(user.Name).IsEqualTo("TestUser");

            // Body parameter
            var record = new TestRecord(1, "Test", new DateTime(2000, 1, 1));
            await test.Client.DoSomethingWithUserAsync(record);

            // Route + body parameters
            await test.Client.DoSomethingWithUserAsync(5, "some-description", record);
        }

        [Test]
        [Arguments("/method")]
        [Arguments("/simplePath/method")]
        [Arguments("/path/method/{id}", new[] { "id" }, "/path/method/1")]
        [Arguments("/{user}/method/{id}", new[] { "user", "id" }, "/testUser/method/1",true)]
        [Arguments("/{user}/method/{id}", new[] { "user", "id" }, "/testUser/method/1", false)] //with use route params= false, but param already in route = same result
        [Arguments("/{user}/method", new[] {"user", "id" }, "/testUser/method?id=1", false)]
        public async Task TestUrlBuilder(string Path, string[]? parameters = null, string? expected = null, bool UseRoutePathParameters = true)
        {
            var options = new ViaductClientOptions() { UseRoutePathParameters = UseRoutePathParameters };
            var builder = options.CreateRouteBuildInfo(string.Empty, Path, parameters ?? []);

            bool appendPar(StringBuilder sb, string name)
            {
                var s = name switch
                {
                    "id" => sb.Append(1),
                    "user" => sb.Append("testUser"),
                    _ => null
                };
                return s is not null;
            }
            options.DefaultParameterResolver = appendPar;

            var output = options.ResolveMethodRoute(builder,null);
            await Assert.That(output).IsEqualTo(expected ?? Path);
        }

        /// <summary>
        /// A call that returns nothing still reads its answer to the end before disposing it: in a browser, disposing a
        /// streamed answer unread aborts its fetch, and the call shows as failed although it succeeded.
        /// </summary>
        [Test]
        public async Task Client_ReadsTheAnswerToItsEnd_ForACallThatReturnsNothing()
        {
            var answer = new WatchedStream("{\"done\":true}"u8.ToArray());
            var services = new ServiceCollection();
            services.CreateAndAddHttpClient<IViaductTestInterface>(o =>
            {
                o.UseInterfaceNameForPath = true;
                o.BaseUrl = "http://viaduct.test/";
            }).ConfigurePrimaryHttpMessageHandler(() => new AnswerWith(answer));
            await using var provider = services.BuildServiceProvider();

            await provider.GetRequiredService<IViaductTestInterface>().ExecuteAsync();

            await Assert.That(answer.ReadToEnd).IsTrue();
        }

        /// <summary>A stream that says whether it was read to its end.</summary>
        private sealed class WatchedStream(byte[] content) : MemoryStream(content)
        {
            public bool ReadToEnd { get; private set; }

            public override int Read(byte[] buffer, int offset, int count) => Note(base.Read(buffer, offset, count));

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => Note(await base.ReadAsync(buffer, cancellationToken));

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => Note(await base.ReadAsync(buffer, offset, count, cancellationToken));

            private int Note(int read)
            {
                if (read == 0)
                    ReadToEnd = true;
                return read;
            }
        }

        /// <summary>Answers every call with 200 and <paramref name="body"/>, as a server would.</summary>
        private sealed class AnswerWith(Stream body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body), RequestMessage = request });
        }

        [Test]
        public async Task Client_ThrowsViaductException_On404Response()
        {
            // Server with no routes — every call returns 404
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ViaductServerOptions.Default.UseInterfaceNameForPath = true;
            var app = builder.Build();
            await app.StartAsync();
            await using var _ = app;

            var testHttpClient = app.GetTestClient();
            var services = new ServiceCollection();
            services.AddSingleton(testHttpClient);
            services.CreateAndAddHttpClient<IViaductTestInterface>(o =>
            {
                o.UseInterfaceNameForPath = true;
                o.BaseUrl = testHttpClient.BaseAddress!.ToString();
            }).ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler());
            await using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<IViaductTestInterface>();

            ViaductException? caught404 = null;
            try { await client.ExecuteAsync(); }
            catch (ViaductException ex) { caught404 = ex; }
            await Assert.That(caught404).IsNotNull();
        }

        [Test]
        public async Task Client_ThrowsViaductException_On500Response()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ViaductServerOptions.Default.UseInterfaceNameForPath = true;
            var app = builder.Build();
            app.MapPost("/ViaductTestInterface/ExecuteAsync", () => Results.StatusCode(500));
            await app.StartAsync();
            await using var _ = app;

            var testHttpClient = app.GetTestClient();
            var services = new ServiceCollection();
            services.AddSingleton(testHttpClient);
            services.CreateAndAddHttpClient<IViaductTestInterface>(o =>
            {
                o.UseInterfaceNameForPath = true;
                o.BaseUrl = testHttpClient.BaseAddress!.ToString();
            }).ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler());
            await using var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<IViaductTestInterface>();

            ViaductException? caught500 = null;
            try { await client.ExecuteAsync(); }
            catch (ViaductException ex) { caught500 = ex; }
            await Assert.That(caught500).IsNotNull();
        }


    }
}
