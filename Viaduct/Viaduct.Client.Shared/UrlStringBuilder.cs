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
        public record UrlBuildInfo(string Path, IReadOnlyList<UrlPartAppender>? Appenders, bool MethodHasRouteParameters);

        public delegate void UrlPartAppender(StringBuilder sb, ParameterResolverDelegate pars);

        public static partial class UrlBuildFunctions
        {

        }
    }
}
