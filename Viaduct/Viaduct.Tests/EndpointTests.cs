using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Subro.JsonTypes;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Viaduct;
using Viaduct.Examples;
using Viaduct.Server;

namespace Viaduct.Tests
{


    [MarkAsJsonSerializable]
    public enum MappingMethod { Direct, Scoped, Transient, Singleton }




    [NotInParallel]
    public class EndpointTests
    {
        [After(Test)]
        public void ResetGlobalOptions()
        {
            ViaductServerOptions.Default.UseInterfaceNameForPath = false;
        }

        private record TestApp(HttpClient Client, WebApplication App) : IAsyncDisposable
        {
          
            public ValueTask DisposeAsync() => App.DisposeAsync();
        }


        private static async Task<TestApp> CreateTestApp(MappingMethod method)
        {
            var d = Subro.JsonTypes.InternalJsonTypes.Default;

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ViaductServerOptions.Default.UseInterfaceNameForPath  = true;
            switch (method)
            {
                case MappingMethod.Direct:
                    builder.Services.AddScoped<IViaductTestInterface, TestApiImplementation>();
                    break;
                case MappingMethod.Scoped:
                    builder.Services.AddScopedAndMap<IViaductTestInterface, TestApiImplementation>();
                    break;
                case MappingMethod.Transient:
                    builder.Services.AddTransientAndMap<IViaductTestInterface, TestApiImplementation>();
                    break;
                case MappingMethod.Singleton:
                    builder.Services.AddSingletonAndMap<IViaductTestInterface, TestApiImplementation>();
                    break;
            }            

            var app = builder.Build();

            if (method == MappingMethod.Direct)
                app.AddEndpoints<IViaductTestInterface>();
            else
                app.AddRegisteredEndpoints();            

            await app.StartAsync();
            return new TestApp(app.GetTestClient(), app);
        }

        [Test]
        [Arguments(MappingMethod.Direct)]
        [Arguments(MappingMethod.Scoped)]
        [Arguments(MappingMethod.Transient)]
        [Arguments(MappingMethod.Singleton)]
        public async Task AsyncVoidEndpoint_ReturnsOk(MappingMethod method)
        {
            await using var test = await CreateTestApp(method);
            var response = await test.Client.PostAsync("/ViaductTestInterface/ExecuteAsync", null);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        [Test]
        [Arguments(MappingMethod.Direct)]
        [Arguments(MappingMethod.Scoped)]
        [Arguments(MappingMethod.Transient)]
        [Arguments(MappingMethod.Singleton)]
        public async Task AsyncReturningEndpoint_ReturnsValue(MappingMethod method)
        {
            await using var test = await CreateTestApp(method);
            var response = await test.Client.GetAsync("/ViaductTestInterface/GetAnIntAsync");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var value = await response.Content.ReadFromJsonAsync<int>();
            await Assert.That(value).IsEqualTo(42);
        }

        [Test]
        [Arguments(MappingMethod.Direct)]
        [Arguments(MappingMethod.Scoped)]
        [Arguments(MappingMethod.Transient)]
        [Arguments(MappingMethod.Singleton)]
        public async Task SyncVoidEndpoint_ReturnsOk(MappingMethod method)
        {
            await using var test = await CreateTestApp(method);
            var response = await test.Client.PostAsync("/ViaductTestInterface/Execute", null);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        [Test]
        [Arguments(MappingMethod.Direct)]
        [Arguments(MappingMethod.Scoped)]
        [Arguments(MappingMethod.Transient)]
        [Arguments(MappingMethod.Singleton)]
        public async Task SyncReturningEndpoint_ReturnsValue(MappingMethod method)
        {
            await using var test = await CreateTestApp(method);
            var response = await test.Client.GetAsync("/ViaductTestInterface/GetAnInt");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var value = await response.Content.ReadFromJsonAsync<int>();
            await Assert.That(value).IsEqualTo(42);
        }

        [Test]
        [Arguments(MappingMethod.Direct)]
        [Arguments(MappingMethod.Scoped)]
        [Arguments(MappingMethod.Transient)]
        [Arguments(MappingMethod.Singleton)]
        public async Task RouteParameter_PassedCorrectly(MappingMethod method)
        {
            await using var test = await CreateTestApp(method);
            var response = await test.Client.GetAsync("/ViaductTestInterface/GetById/7");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var value = await response.Content.ReadFromJsonAsync<int>();
            await Assert.That(value).IsEqualTo(14); // 7 * 2
        }

        [Test]
        [Arguments(MappingMethod.Direct)]
        [Arguments(MappingMethod.Scoped)]
        [Arguments(MappingMethod.Transient)]
        [Arguments(MappingMethod.Singleton)]
        public async Task ComplexReturnType_ReturnsJson(MappingMethod method)
        {
            await using var test = await CreateTestApp(method);
            var response = await test.Client.GetAsync("/ViaductTestInterface/GetUserAsync/5");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var user = await response.Content.ReadFromJsonAsync<TestRecord>();
            await Assert.That(user).IsNotNull();
            await Assert.That(user!.Id).IsEqualTo(5);
            await Assert.That(user.Name).IsEqualTo("TestUser");
        }

        [Test]
        [Arguments(MappingMethod.Direct)]
        [Arguments(MappingMethod.Scoped)]
        [Arguments(MappingMethod.Transient)]
        [Arguments(MappingMethod.Singleton)]
        public async Task MultipleRouteParameters_Work(MappingMethod method)
        {
            await using var test = await CreateTestApp(method);
            var response = await test.Client.PostAsync("/ViaductTestInterface/CreateUserAsync/1/John/2000-01-01", null);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        [Test]
        [Arguments(MappingMethod.Direct)]
        [Arguments(MappingMethod.Scoped)]
        [Arguments(MappingMethod.Transient)]
        [Arguments(MappingMethod.Singleton)]
        public async Task BodyParameter_ReceivedCorrectly(MappingMethod method)
        {
            await using var test = await CreateTestApp(method);
            var record = new TestRecord(1, "Test", new DateTime(2000, 1, 1));
            var response = await test.Client.PostAsJsonAsync("/ViaductTestInterface/DoSomethingWithUserAsync", record);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        [Test]
        [Arguments(MappingMethod.Direct)]
        [Arguments(MappingMethod.Scoped)]
        [Arguments(MappingMethod.Transient)]
        [Arguments(MappingMethod.Singleton)]
        public async Task RouteAndBodyParameters_Work(MappingMethod method)
        {
            await using var test = await CreateTestApp(method);
            var record = new TestRecord(1, "Test", new DateTime(2000, 1, 1));
            var response = await test.Client.PostAsJsonAsync("/ViaductTestInterface/DoSomethingWithUserAsync/5/some-description", record);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }
}
