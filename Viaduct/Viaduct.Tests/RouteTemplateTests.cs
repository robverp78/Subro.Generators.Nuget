using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Viaduct;

namespace Viaduct.Tests
{
    /// <summary>Route constants of an application: each gets a link method from [RouteTemplates].</summary>
    [RouteTemplates]
    public static partial class TestPages
    {
        public const string Home = "/";
        public const string Club = "/app/clubs/{id:guid}";
        public const string Season = "/app/clubs/{id:guid}/seasons/{year:int}";
        public const string Search = "/search/{text}";
        public const string List = "/list/{page:int?}";
        public const string Files = "/files/{*path}";
        public const string Toggle = "/settings/{on:bool}";

        /// <summary>Not a route: no link.</summary>
        public static readonly string NotAConstant = "/x";
    }

    /// <summary>The links [RouteTemplates] generates, and that they reach the endpoint mapped with the same constant.</summary>
    public class RouteTemplateTests
    {
        static readonly Guid ClubId = Guid.Parse("0198d3c0-1111-7222-8333-944445555666");

        [Test]
        public async Task AConstantWithoutParameters_IsItsOwnLink()
            => await Assert.That(TestPages.HomeLink()).IsEqualTo("/");

        [Test]
        public async Task Parameters_AreFilledIn_TypedByTheirConstraints()
        {
            await Assert.That(TestPages.ClubLink(ClubId)).IsEqualTo($"/app/clubs/{ClubId:D}");
            await Assert.That(TestPages.SeasonLink(ClubId, 2026)).IsEqualTo($"/app/clubs/{ClubId:D}/seasons/2026");
            await Assert.That(TestPages.ToggleLink(true)).IsEqualTo("/settings/true");
        }

        [Test]
        public async Task Text_IsEscaped_AndACatchAllKeepsItsSlashes()
        {
            await Assert.That(TestPages.SearchLink("a b/c")).IsEqualTo("/search/a%20b%2Fc");
            await Assert.That(TestPages.FilesLink("docs/a b.pdf")).IsEqualTo("/files/docs/a%20b.pdf");
        }

        [Test]
        public async Task AnOptionalParameter_IsLeftOutWhenNull()
        {
            await Assert.That(TestPages.ListLink(2)).IsEqualTo("/list/2");
            await Assert.That(TestPages.ListLink()).IsEqualTo("/list");
        }

        [Test]
        public async Task ALink_ReachesTheEndpointMappedWithTheSameConstant()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            await using var app = builder.Build();
            app.MapGet(TestPages.Season, (Guid id, int year) => $"{id:D} {year}");
            await app.StartAsync();

            var answer = await app.GetTestClient().GetStringAsync(TestPages.SeasonLink(ClubId, 2026));

            await Assert.That(answer).IsEqualTo($"{ClubId:D} 2026");
        }
    }
}
