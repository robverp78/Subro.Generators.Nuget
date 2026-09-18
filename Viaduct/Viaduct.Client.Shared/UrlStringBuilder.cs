using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Viaduct.Client
{
    public partial record ViaductClientOptions 
    {
        /// <summary>
        /// The base URL for the HTTP client. 
        /// NB: this is different from <see cref="BasePathAttribute"/>, which is meant to be relative, and can contain routing parameters.
        /// The BaseUrl is the optional absolute http(s) base address that will be set on the HttpClient
        /// </summary>
        [StringSyntax(StringSyntaxAttribute.Uri)]
        public string? BaseUrl { get; set; }
    }


    namespace UrlBuilding
    {
        /// <summary>
        /// Should append the parameter value of the given parameter name to the string builder. 
        /// </summary>        
        /// <remarks>
        /// The generator chooses the type of resolver that is used depending on the <see cref="ViaductClientOptions.UseLowerCaseRouteParameters"/> value.
        /// If this value is true: all parameters names are made lowercase before this function is called
        /// </remarks> 
        /// <returns><c>true</c> if the parameter was resolved succesfully</returns>
        public delegate bool ParameterResolverDelegate(StringBuilder sb, string ParameterName);

        /// <summary>
        /// A method's route, prepared once at registration so that building the url for a call is only a matter
        /// of running the appenders.
        /// </summary>
        /// <param name="Path">The route template, base path included.</param>
        /// <param name="Appenders">
        /// The pieces the url is assembled from, in order. Null for a route with nothing to fill in, where
        /// <paramref name="Path"/> is already the url.
        /// </param>
        /// <param name="MethodHasRouteParameters">Whether the method has parameters that belong in the path.</param>
        public record UrlBuildInfo(string Path, IReadOnlyList<UrlPartAppender>? Appenders, bool MethodHasRouteParameters);

        /// <summary>
        /// Appends one piece of the url — a literal segment, or a parameter it asks
        /// <paramref name="pars"/> to resolve.
        /// </summary>
        public delegate void UrlPartAppender(StringBuilder sb, ParameterResolverDelegate pars);

        /// <summary>
        /// Url building helpers used by the generated clients. Extended by the generator's own partial.
        /// </summary>
        public static partial class UrlBuildFunctions
        {

        }
    }
}
