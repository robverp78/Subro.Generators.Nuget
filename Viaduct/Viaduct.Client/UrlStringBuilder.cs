using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Viaduct.Client.UrlBuilding;

namespace Viaduct.Client
{
    partial record ViaductClientOptions
    {


        /// <summary>
        /// Resolves route parameters before anything else, including the values the generated proxy passes.
        /// For a value that is the caller's to decide rather than the call's — a tenant or club id carried in
        /// every path, say.
        /// </summary>
        public ParameterResolverDelegate? OverridesParameterResolver { get; set; }

        /// <summary>
        /// Resolves route parameters the call itself did not supply. The last thing tried before
        /// <see cref="MissingRouteParameterException"/> is thrown.
        /// </summary>
        public ParameterResolverDelegate? DefaultParameterResolver { get; set; }
    }

    public static partial class ViaductClientFunctions
    {
        extension(StringBuilder sb)
        {
            /// <summary>
            /// Appends a <see cref="DateOnly"/> value to the route.
            /// </summary>
            /// <returns>The same <see cref="StringBuilder"/> for chaining.</returns>
            /// <remarks>Format: ISO 8601 date only, e.g. <c>2024-03-15</c></remarks>
            public StringBuilder AppendRoutePart(DateOnly value) =>
                sb.Append(value.ToString("yyyy-MM-dd"));

            /// <summary>
            /// Appends a <see cref="DateTime"/> value to the route.
            /// </summary>
            /// <returns>The same <see cref="StringBuilder"/> for chaining.</returns>
            /// <remarks>Format: ISO 8601 datetime with UTC, e.g. <c>2024-03-15T10:30:00Z</c></remarks>
            public StringBuilder AppendRoutePart(DateTime value) =>
                sb.Append(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));

            /// <summary>
            /// Appends a <see cref="DateTimeOffset"/> value to the route.
            /// </summary>
            /// <returns>The same <see cref="StringBuilder"/> for chaining.</returns>
            /// <remarks>Format: ISO 8601 datetime with offset, e.g. <c>2024-03-15T10:30:00%2B01%3A00</c> (URL-escaped)</remarks>
            public StringBuilder AppendRoutePart(DateTimeOffset value) =>
                sb.Append(Uri.EscapeDataString(value.ToString("yyyy-MM-ddTHH:mm:sszzz")));

            /// <summary>
            /// Appends a <see cref="TimeOnly"/> value to the route.
            /// </summary>
            /// <returns>The same <see cref="StringBuilder"/> for chaining.</returns>
            /// <remarks>Format: ISO 8601 time only, e.g. <c>10:30:00</c></remarks>
            public StringBuilder AppendRoutePart(TimeOnly value) =>
                sb.Append(value.ToString("HH:mm:ss"));

            /// <summary>
            /// Appends a <see cref="TimeSpan"/> value to the route.
            /// </summary>
            /// <returns>The same <see cref="StringBuilder"/> for chaining.</returns>
            /// <remarks>Format: ISO 8601 duration, e.g. <c>P1DT2H3M4S</c> (URL-escaped)</remarks>
            public StringBuilder AppendRoutePart(TimeSpan value) =>
                sb.Append(Uri.EscapeDataString(XmlConvert.ToString(value)));

            /// <summary>
            /// Appends an arbitrary value to the route by calling <see cref="object.ToString"/> and URL-escaping the result.
            /// </summary>
            /// <returns>The same <see cref="StringBuilder"/> for chaining.</returns>
            public StringBuilder AppendRoutePart<T>(T value) =>
                value?.ToString() is string str ? sb.Append(Uri.EscapeDataString(str)) : sb;
        }
    }
}

namespace Viaduct.Client.UrlBuilding
{



    partial class UrlBuildFunctions
    {

        [GeneratedRegex(ViaductFunctions.GetRouteParametersPattern)]
        private static partial Regex GetRouteParametersRegex();

        extension(ViaductClientOptions options)
        {
            /// <summary>
            /// Generates a list of <see cref="UrlPartAppender"/> for the given path, based on the predefined route parameters in the path and the provided parameter names.
            /// </summary>                
            public UrlBuildInfo CreateRouteBuildInfo(
                [StringSyntax(ViaductFunctions.RouteSyntax)] string BasePath,
                [StringSyntax(ViaductFunctions.RouteSyntax)] string MethodPath,
                IEnumerable<string> MethodParameters)
            {

                //check for predefined route parameters
                string PathAndMethod = BasePath.EndsWith('/') || MethodPath.StartsWith('/') ? BasePath + MethodPath : BasePath + "/" + MethodPath;                
                var matches = GetRouteParametersRegex().Matches(PathAndMethod);
                if (options.ForceLowerCaseRouteParameters)
                    MethodParameters = MethodParameters.Select(static s => s.ToLower());
                var parameters = MethodParameters.ToList();

                if (matches.Count == 0 && parameters.Count == 0)
                    //No route parameters, just return the path as is and no appenders
                    return new(PathAndMethod, null, false);

                List<UrlPartAppender> Appenders = [];
                bool methodHasRouteParameters = false;

                // It the path contains predefined route parameters, the path has to be generated around those

                if (matches.Count > 0)
                {
                    int lastPos = 0;

                    void addUntil(int pos)
                    {
                        if (pos > lastPos)
                        {
                            var val = PathAndMethod[lastPos..pos];
                            Appenders.Add((sb, _) => sb.Append(val));
                        }
                    }

                    for (int i = 0; i < matches.Count; i++)
                    {
                        var m = matches[i];
                        var parName = m.Groups["name"].Value;
                        if (options.ForceLowerCaseRouteParameters)
                            parName = parName.ToLowerInvariant();

                        addUntil(m.Index);
                        if (m.Index >= BasePath.Length)
                            methodHasRouteParameters = true;
                        lastPos = m.Index + m.Length;
                        parameters.Remove(parName);
                        Appenders.Add((sb, pars) => pars(sb, parName));
                    }
                    addUntil(PathAndMethod.Length);
                }
                else
                    Appenders.Add((sb, pars) => sb.Append(PathAndMethod));

                if (parameters.Count > 0)
                {
                    if (options.UseRoutePathParameters && !methodHasRouteParameters)
                    {
                        foreach (var parName in parameters)
                            Appenders.Add((sb, pars) =>
                            {
                                if (sb.Length == 0 || sb[^1] != '/') sb.Append('/');
                                pars(sb, parName);
                            });
                    }
                    else
                    {
                        string[] parNames = [.. parameters];
                        Appenders.Add((sb, parResolver) => AddOptionalParameters(parNames, sb, parResolver));
                    }
                }

                return new(PathAndMethod, Appenders,  methodHasRouteParameters);
            }

            /// <summary>
            /// At the moment this just creates a new stringbuilder. Put here in case there is a need to pool them in the future, or to use a custom implementation.
            /// </summary>
            internal static StringBuilder CreateStringBuilder() => new();

            /// <summary>
            /// Uses <see cref="UrlBuildInfo"/> with parameter resolvers to create the url
            /// </summary>            
            /// <exception cref="MissingRouteParameterException"></exception>
            /// <remarks>
            /// For parameter resolving, depending on whether they are given,
            /// first <see cref="ViaductClientOptions.OverridesParameterResolver"/> is tried, then the given <paramref name="parameterResolver"/>, and
            /// finally <see cref="ViaductClientOptions.DefaultParameterResolver"/>
            /// If none of these resolve the route parameter (if there are route parameters), a <see cref="MissingRouteParameterException"/> is thrown
            /// </remarks>
            public string ResolveMethodRoute(UrlBuildInfo info, ParameterResolverDelegate? parameterResolver = null)
            {
                if (info.Appenders?.Count > 0)
                {
                    ParameterResolverDelegate resolver =
                        (sb, par) => options.OverridesParameterResolver?.Invoke(sb,par)
                            ?? parameterResolver?.Invoke(sb, par)
                            ?? options.DefaultParameterResolver?.Invoke(sb, par)
                            ?? throw new MissingRouteParameterException(par, info.Path);

                    var sb = CreateStringBuilder();
                    foreach (var appender in info.Appenders)
                        appender(sb, resolver);
                    return sb.ToString();
                }
                else
                    return info.Path;                
            }
        }

        static void AddOptionalParameters(string[] parNames, StringBuilder sb, ParameterResolverDelegate parResolver)
        {
            char sep = '?';
            foreach (var par in parNames)
            {
                int len = sb.Length;
                sb.Append(sep).Append(par).Append('=');
                int checkLen = sb.Length;
                bool chk = parResolver(sb, par);
                if (!chk && checkLen == sb.Length) 
                    sb.Length = len;//if the parameter resolver did not append anything, remove the separator and parameter name
                else
                    sep = '&';
            }
        }
    }



    
}
