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

    /// <summary>A body that is a value type, so that its default is a value like any other.</summary>
    public record struct RouteAgreementPoint(int X, int Y);

    /// <summary>
    /// A value with a converter registered with Subro.Json, written as <c>"C-5"</c>: what both sides must agree on for it
    /// to cross at all.
    /// </summary>
    public readonly record struct RouteAgreementCode(int Number)
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Register() => Subro.Json.RegisterConverter(new Converter());

        sealed class Converter : System.Text.Json.Serialization.JsonConverter<RouteAgreementCode>
        {
            public override RouteAgreementCode Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
                => new(int.Parse(reader.GetString()!["C-".Length..]));

            public override void Write(System.Text.Json.Utf8JsonWriter writer, RouteAgreementCode value, System.Text.Json.JsonSerializerOptions options)
                => writer.WriteStringValue($"C-{value.Number}");
        }
    }

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

        [CustomRoute("/days")]
        Task<RouteAgreementResult> GetDayAsync(DateOnly? on = null, CancellationToken cancellationToken = default);

        [CustomRoute("/people")]
        Task<RouteAgreementResult> FindPersonAsync(string FirstName, int? Age = null, CancellationToken cancellationToken = default);

        [CustomRoute("/points")]
        Task<RouteAgreementResult> PlaceAsync(RouteAgreementPoint point, CancellationToken cancellationToken = default);

        [CustomRoute("/codes")]
        Task<RouteAgreementCode> NextCodeAsync(RouteAgreementCode code, CancellationToken cancellationToken = default);
    }

    public class RouteAgreementService : IRouteAgreementService
    {
        public Task<RouteAgreementResult> GetTranslationsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult("translations"));
        public Task<RouteAgreementResult> GetUserAsync(Guid userId, string search, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult($"{userId}|{search}"));
        public Task<RouteAgreementResult> SubmitAsync(RouteAgreementRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult(request.Name));
        public Task<RouteAgreementResult> FindLinkAsync(string url, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult(url));
        public Task<RouteAgreementResult> GetByCodeAsync(int code, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult(code.ToString()));
        public Task<RouteAgreementResult> GetDayAsync(DateOnly? on = null, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult(on?.ToString("yyyy-MM-dd") ?? "none"));
        public Task<RouteAgreementResult> FindPersonAsync(string FirstName, int? Age = null, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult($"{FirstName}|{Age}"));
        public Task<RouteAgreementResult> PlaceAsync(RouteAgreementPoint point, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementResult($"{point.X},{point.Y}"));
        public Task<RouteAgreementCode> NextCodeAsync(RouteAgreementCode code, CancellationToken cancellationToken = default) => Task.FromResult(new RouteAgreementCode(code.Number + 1));
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

        /// <summary>Wraps every response's content, to see that the client disposes it.</summary>
        class DisposalHandler : DelegatingHandler
        {
            public List<TrackedContent> Contents { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                var tracked = new TrackedContent(response.Content);
                Contents.Add(tracked);
                response.Content = tracked;
                return response;
            }
        }

        sealed class TrackedContent(HttpContent inner) : HttpContent
        {
            public bool Disposed { get; private set; }

            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => inner.CopyToAsync(stream);

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                if (disposing)
                    inner.Dispose();
                base.Dispose(disposing);
            }
        }

        static async Task<TestApp> CreateTestApp(bool useRoutePathParameters, Action<ViaductClientOptions>? configure = null, DelegatingHandler? extra = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddScoped<IRouteAgreementService, RouteAgreementService>();
            builder.Services.AddJsonTypeResolvers();

            var app = builder.Build();
            app.AddEndpoints<IRouteAgreementService>(o => o.UseRoutePathParameters = useRoutePathParameters);
            await app.StartAsync();

            var recorder = new RecordingHandler();
            var testHttpClient = app.GetTestClient();
            var clientServices = new ServiceCollection();
            var client = clientServices.CreateAndAddHttpClient<IRouteAgreementService>(o =>
            {
                o.UseRoutePathParameters = useRoutePathParameters;
                o.BaseUrl = testHttpClient.BaseAddress!.ToString();
                configure?.Invoke(o);
            })
                .ConfigurePrimaryHttpMessageHandler(() => app.GetTestServer().CreateHandler())
                .AddHttpMessageHandler(() => recorder);
            if (extra is not null)
                client.AddHttpMessageHandler(() => extra);
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

        /// <summary>
        /// A query parameter that is null is left out, not sent empty: a server cannot bind <c>on=</c> to a
        /// <c>DateOnly?</c> (found 2026-11-11, Vesta's group members). Query parameters only: with
        /// <see cref="ViaductOptions.UseRoutePathParameters"/> a null value still leaves an empty path segment.
        /// </summary>
        [Test]
        public async Task ANullQueryValue_IsLeftOut()
        {
            await using var test = await CreateTestApp(useRoutePathParameters: false);

            await Assert.That((await test.Client.GetDayAsync()).Value).IsEqualTo("none");
            await Assert.That(test.LastCall()).IsEqualTo(("GET", "/public/days"));
        }

        /// <summary>
        /// A date in the query is written as the server reads it, whatever the caller's culture: under Norwegian
        /// formatting 3 November went out as 03.11.2026 and arrived as 11 March (found 2026-11-11).
        /// </summary>
        [Test]
        public async Task ADateInTheQuery_ArrivesAsTheSameDay_InAnyCulture()
        {
            await using var test = await CreateTestApp(useRoutePathParameters: false);
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("nb-NO");
            try
            {
                await Assert.That((await test.Client.GetDayAsync(new DateOnly(2026, 11, 3))).Value).IsEqualTo("2026-11-03");
                await Assert.That(test.LastCall()).IsEqualTo(("GET", "/public/days?on=2026-11-03"));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
        }

        /// <summary>Text in the query is escaped, so an ampersand stays part of the value.</summary>
        [Test]
        public async Task TextInTheQuery_IsEscaped()
        {
            await using var test = await CreateTestApp(useRoutePathParameters: false);
            var userId = Guid.NewGuid();

            await Assert.That((await test.Client.GetUserAsync(userId, "a&b c")).Value).IsEqualTo($"{userId}|a&b c");
        }

        /// <summary>The calls whose addresses the two ways of writing one are compared on.</summary>
        static async Task<List<string>> AddressesOf(TestApp test)
        {
            var userId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            List<string> addresses = [];
            async Task Call(Func<Task> call)
            {
                await call();
                addresses.Add(test.LastCall().PathAndQuery);
            }

            await Call(() => test.Client.GetUserAsync(userId, "a&b c"));
            await Call(() => test.Client.FindLinkAsync("x y"));
            await Call(() => test.Client.GetByCodeAsync(5));
            await Call(() => test.Client.GetDayAsync());
            await Call(() => test.Client.GetDayAsync(new DateOnly(2026, 11, 3)));
            await Call(() => test.Client.FindPersonAsync("Ada", 30));
            await Call(() => test.Client.FindPersonAsync("Ada"));
            return addresses;
        }

        /// <summary>
        /// An address is written straight from the arguments when the options allow it, and through the parameter
        /// resolvers when they are set: both write the same address. A resolver that resolves nothing forces the second.
        /// </summary>
        [Test]
        [Arguments(false)]
        [Arguments(true)]
        public async Task TheDirectAndTheResolvedAddress_AreTheSame(bool forceLowerCase)
        {
            await using var direct = await CreateTestApp(false, o => o.ForceLowerCaseRouteParameters = forceLowerCase);
            await using var resolved = await CreateTestApp(false, o =>
            {
                o.ForceLowerCaseRouteParameters = forceLowerCase;
                o.DefaultParameterResolver = (_, _) => false;
            });

            var addresses = await AddressesOf(direct);

            await Assert.That(addresses).IsEquivalentTo(await AddressesOf(resolved));
            await Assert.That(addresses).Contains("/public/users/11111111-2222-3333-4444-555555555555?search=a%26b%20c");
            await Assert.That(addresses).Contains(forceLowerCase ? "/public/people?firstname=Ada&age=30" : "/public/people?FirstName=Ada&Age=30");
            await Assert.That(addresses).Contains(forceLowerCase ? "/public/people?firstname=Ada" : "/public/people?FirstName=Ada");
        }

        /// <summary>
        /// A typed client is made for every injection, but its options and routes are made once for the registration:
        /// resolving it again does not run the configuration again.
        /// </summary>
        [Test]
        public async Task ClientsOfOneRegistration_ShareItsOptions()
        {
            var configured = 0;
            var services = new ServiceCollection();
            services.CreateAndAddHttpClient<IRouteAgreementService>(o =>
            {
                configured++;
                o.BaseUrl = "https://api.test/";
            });
            await using var provider = services.BuildServiceProvider();

            var first = provider.GetRequiredService<IRouteAgreementService>();
            var afterFirst = configured;
            for (var i = 0; i < 3; i++)
                await Assert.That(provider.GetRequiredService<IRouteAgreementService>()).IsNotSameReferenceAs(first);

            await Assert.That(configured).IsEqualTo(afterFirst);
        }

        /// <summary>
        /// A response is handed back once its headers are in, so its JSON is read from the stream; the client disposes
        /// it once read, with a result or without.
        /// </summary>
        [Test]
        public async Task EveryResponse_IsDisposed()
        {
            var disposal = new DisposalHandler();
            await using var test = await CreateTestApp(false, extra: disposal);

            await Assert.That((await test.Client.GetDayAsync(new DateOnly(2026, 11, 3))).Value).IsEqualTo("2026-11-03");
            await test.Client.SubmitAsync(new("Ada"));

            await Assert.That(disposal.Contents.Count).IsEqualTo(2);
            await Assert.That(disposal.Contents.All(static content => content.Disposed)).IsTrue();
        }

        /// <summary>Hands out reflection-based metadata, and counts how often it is asked for each type.</summary>
        sealed class CountingResolver : System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver
        {
            readonly System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver inner = new();

            public Dictionary<Type, int> Asked { get; } = [];

            public System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(Type type, System.Text.Json.JsonSerializerOptions options)
            {
                Asked[type] = Asked.GetValueOrDefault(type) + 1;
                return inner.GetTypeInfo(type, options);
            }
        }

        /// <summary>
        /// A client's own JSON resolver is asked for a type once, not on every call: asked directly, a resolver builds
        /// new metadata each time.
        /// </summary>
        [Test]
        public async Task AClientsOwnResolver_IsAskedOncePerType()
        {
            var resolver = new CountingResolver();
            await using var test = await CreateTestApp(false, o => o.JsonTypeInfoResolver = resolver);

            for (var i = 0; i < 3; i++)
                await Assert.That((await test.Client.GetByCodeAsync(i)).Value).IsEqualTo(i.ToString());

            await Assert.That(resolver.Asked.GetValueOrDefault(typeof(RouteAgreementResult))).IsEqualTo(1);
        }

        /// <summary>
        /// With parameters in the path, a null leaves its segment out rather than an empty one, and the server maps a
        /// parameter that may be left out as an optional segment.
        /// </summary>
        [Test]
        public async Task ANullValueInThePath_LeavesItsSegmentOut()
        {
            await using var test = await CreateTestApp(useRoutePathParameters: true);

            await Assert.That((await test.Client.GetDayAsync()).Value).IsEqualTo("none");
            await Assert.That(test.LastCall()).IsEqualTo(("GET", "/public/days"));
            await Assert.That((await test.Client.GetDayAsync(new DateOnly(2026, 11, 3))).Value).IsEqualTo("2026-11-03");
            await Assert.That(test.LastCall()).IsEqualTo(("GET", "/public/days/2026-11-03"));
        }

        /// <summary>A value-type body equal to its default is sent like any other; it used to be left out as if null.</summary>
        [Test]
        [Arguments(true)]
        [Arguments(false)]
        public async Task AValueTypeBody_EqualToItsDefault_IsSent(bool useRoutePathParameters)
        {
            await using var test = await CreateTestApp(useRoutePathParameters);

            await Assert.That((await test.Client.PlaceAsync(default)).Value).IsEqualTo("0,0");
            await Assert.That((await test.Client.PlaceAsync(new(3, 4))).Value).IsEqualTo("3,4");
        }

        /// <summary>Keeps the JSON of each request and response, as it went over the wire.</summary>
        sealed class WireRecorder : DelegatingHandler
        {
            public List<(string Sent, string Received)> Bodies { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var sent = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                var response = await base.SendAsync(request, cancellationToken);
                var received = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Content = new StringContent(received, System.Text.Encoding.UTF8, "application/json");
                Bodies.Add((sent, received));
                return response;
            }
        }

        /// <summary>
        /// Client and server read and write JSON with the same converters, Subro.Json's: a value with a converter of its
        /// own goes over the wire as that converter writes it, both ways.
        /// </summary>
        [Test]
        public async Task AValueWithASubroJsonConverter_CrossesAsItsConverterWritesIt()
        {
            var wire = new WireRecorder();
            await using var test = await CreateTestApp(false, extra: wire);

            await Assert.That(await test.Client.NextCodeAsync(new RouteAgreementCode(5))).IsEqualTo(new RouteAgreementCode(6));

            var (sent, received) = wire.Bodies.Single();
            await Assert.That(sent).IsEqualTo("\"C-5\"");
            await Assert.That(received).IsEqualTo("\"C-6\"");
        }
    }
}
