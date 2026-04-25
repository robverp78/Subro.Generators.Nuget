using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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


    }
}
