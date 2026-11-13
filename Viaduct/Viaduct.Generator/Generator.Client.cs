using Microsoft.CodeAnalysis.CSharp.Syntax;
using Subro.Generators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Viaduct.Client;
using static Viaduct.Generation.ViaductGeneratorFunctions;

using static Subro.Generators.GeneratorFunctions;

namespace Viaduct.Generation
{
    partial class ViaductGenerator
    {
    }

    namespace Client
    {
        public static partial class ClientGeneratorFunctions
        {
            /// <summary>The start of a call that writes a query or route value: escaped, invariant, by the value's type.</summary>
            const string QueryValueWriter = "global::Viaduct.Client.ViaductClientFunctions.AppendQueryValue(__sb, ";

            /// <summary>One part of a method's route template: text as it stands, or the value of one of the method's parameters.</summary>
            record RoutePart(string? Text, ParameterCreationInfo? Parameter);

            record MethodInfo(MethodCreationInfo Info, int Index)
            {
                public readonly string urlVariable = $"{Info.Name}_UrlCreator_{Index}";
                public readonly string methodVariable = $"{Info.Name}_Method_{Index}";
                public readonly string fastUrlMethod = $"{Info.Name}_Url_{Index}";
                public readonly string prefixVariable = $"{Info.Name}_Prefix_{Index}";
                public string Name => Info.Name;

                public ParameterCreationInfo[] QueryParameters = [.. Info.GetQueryParameters()];

                public EquatableArray<ParameterCreationInfo> Parameters => Info.Parameters;

                /// <summary>Whether the method's own route names parameters: then the rest go in the query whatever the options say.</summary>
                public readonly bool HasRouteParameters = Regex.IsMatch(Info.RouteTemplate, ViaductFunctions.GetRouteParametersPattern);

                /// <summary>
                /// The route template in parts, when each parameter it names is one of the method's by that exact name; null
                /// otherwise, when only the parameter resolvers can fill it in.
                /// </summary>
                public readonly RoutePart[]? RouteParts = SplitRoute(Info.RouteTemplate, [.. Info.GetQueryParameters()]);
            }

            static RoutePart[]? SplitRoute(string template, ParameterCreationInfo[] parameters)
            {
                var parts = new List<RoutePart>();
                var last = 0;
                foreach (Match match in Regex.Matches(template, ViaductFunctions.GetRouteParametersPattern))
                {
                    var name = match.Groups["name"].Value;
                    if (parameters.FirstOrDefault(p => p.Name == name) is not { } parameter)
                        return null;

                    if (match.Index > last)
                        parts.Add(new(template.Substring(last, match.Index - last), null));
                    parts.Add(new(null, parameter));
                    last = match.Index + match.Length;
                }

                if (last < template.Length)
                    parts.Add(new(template.Substring(last), null));
                return [.. parts];
            }

            /// <summary>The <c>HttpMethod</c> for a verb: the shared instance where there is one.</summary>
            static string HttpMethodExpression(string verb) => verb.ToUpperInvariant() switch
            {
                "GET" => "HttpMethod.Get",
                "POST" => "HttpMethod.Post",
                "PUT" => "HttpMethod.Put",
                "DELETE" => "HttpMethod.Delete",
                "PATCH" => "HttpMethod.Patch",
                "HEAD" => "HttpMethod.Head",
                "OPTIONS" => "HttpMethod.Options",
                _ => $"HttpMethod.Parse({Literal(verb)})",
            };

            /// <summary>
            /// Writes a generated client. Everything a call needs besides its arguments — the options, the base path, each
            /// method's verb and how its address is written — is worked out once per registration, in <c>ViaductRoutes</c>,
            /// and shared by every client made from it: a typed client is made for every injection.
            /// </summary>
            /// <remarks>
            /// A method's address is written by a method of its own, straight from its arguments, when nothing can change it
            /// at run time: no parameter resolvers, a base path without route parameters, and parameters that go in the query.
            /// Otherwise the route is built through the resolvers, as before.
            /// </remarks>
            public class ClientCodeCreator(InterfaceMetaData info, IEnumerable<MethodCallerInfo> methodCallers)
            {

                readonly InterfaceMetaData info = info;
                readonly MethodInfo[] methods = [.. info.Methods.Select((m,i)=>  new MethodInfo(m,i))];
                readonly MethodCallerInfo[] callers = [.. methodCallers];
                readonly StringBuilder sb = new();
                readonly string ClassName = $"{clientClass}_{info.Identifier}";


                const string clientClass = nameof(ViaductClientBase);
                const string clientOptions = nameof(ViaductClientOptions);
                const string RoutesClass = "ViaductRoutes";
                const string BasePathVariable = "BasePath";

                public string Create()
                {
                    sb.Clear();
                    sb.Append(@"
#nullable enable
using Viaduct;
using Viaduct.Client;
using Viaduct.Client.UrlBuilding;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System;
").Append(info.Namespace is { Length: > 0 } ns ? $"using {ns};" : string.Empty).AppendLine()
    .AppendLine(interceptorAttribute)
    .Append($@"

namespace {GeneratedNameSpace}
{{");

                    WriteInterceptors();

    sb.Append($@"
    file class {ClassName}:{clientClass},{info.FullyQualifiedName}
    {{
        readonly {RoutesClass} __routes;

        public {ClassName}(HttpClient client, {RoutesClass} routes) : base(client, routes.Options)
        {{
            __routes = routes;
        }}
");
                    WriteRoutes();

                    writeInterfaceImplementations();

                    //close class and namespace
                    sb.AppendLine().Append(@"
    }
}
");
                    return sb.ToString();

                }


                void WriteInterceptors()
                {
                    sb.Append(@"
    file static class ").Append(info.Identifier).Append("_interceptors").Append(@"
    {");
                    foreach(var caller in callers)
                    {
                        sb.AppendInterceptor(caller);
                    }

                    sb.Append(@"
        public static IHttpClientBuilder AddMappedHttpClient(this IServiceCollection services, ").AppendConfigureArg().Append(@")
        {
            // Once for the registration, shared by every client it makes.
            var routes = new ").Append(ClassName).Append('.').Append(RoutesClass).Append(@"(configure.GetOptionsInstance());
            return services.AddHttpClient<").Append(info.FullyQualifiedName).Append(", ").Append(ClassName).Append(@">(client =>
                {
                    if(routes.BaseAddress is { } baseAddress)
                        client.BaseAddress = baseAddress;
                    return new ").Append(ClassName).Append(@"(client, routes);
                }).AddViaductAuthorization(configure);
        }
    }");
                }

                /// <summary>The class that holds what every client of the registration shares, and writes the addresses.</summary>
                void WriteRoutes()
                {
                    sb.Append($@"
        /// <summary>What every client of one registration shares: made once, read by every call.</summary>
        public sealed class {RoutesClass}
        {{
            public readonly {clientOptions} Options;
            public readonly Uri? BaseAddress;
            public readonly string {BasePathVariable};
");
                    foreach (var method in methods)
                    {
                        sb.Append("\t\t\tpublic readonly Func<").AppendArgumentTypes(method.QueryParameters);
                        if (method.QueryParameters.Length > 0) sb.Append(',');
                        sb.Append("string> ").Append(method.urlVariable).AppendLine(";")
                          .Append("\t\t\tpublic readonly HttpMethod ").Append(method.methodVariable).AppendLine(";");
                        if (CanWriteDirectly(method))
                            sb.Append("\t\t\treadonly string? ").Append(method.prefixVariable).AppendLine(";");
                    }

                    sb.Append($@"
            public {RoutesClass}({clientOptions} options)
            {{
                Options = options;
                BaseAddress = options.{nameof(ViaductClientOptions.BaseUrl)} is {{ }} baseUrl ? new Uri(baseUrl) : null;
                {BasePathVariable} = {createGetBasePathCode(info.BasePath, info.Name, info.TypeArgSegment, "Options")};

                // An address is written straight from the arguments when nothing at run time can change it.
                var __direct = Options.OverridesParameterResolver is null
                    && Options.DefaultParameterResolver is null
                    && {BasePathVariable}.IndexOf('{{') < 0;
");
                    foreach (var method in methods)
                        WriteMethodRoute(method);

                    sb.Append(@"
            }
");
                    foreach (var method in methods)
                        if (CanWriteDirectly(method))
                            WriteDirectUrl(method);

                    sb.Append(@"
        }
");
                }

                /// <summary>Whether a method's address can be written without the resolvers: when its route's parameters are all its own.</summary>
                static bool CanWriteDirectly(MethodInfo method) => method.QueryParameters.Length > 0 && method.RouteParts is not null;

                void WriteMethodRoute(MethodInfo method)
                {
                    //route builder creation. The route template is the one the server maps (a [CustomRoute] or
                    //the method name), so that both sides arrive at the same path.
                    sb.AppendLine().Append($$"""
                { //{{method.Name}}
                    {{method.methodVariable}} = {{HttpMethodExpression(method.Info.HttpMethod)}};
                    var __buildInfo = Options.CreateRouteBuildInfo({{BasePathVariable}},{{Literal(method.Info.RouteTemplate)}},[
""");
                    for (int i = 0; i < method.QueryParameters.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append("\"").Append(method.QueryParameters[i].Name).Append("\"");
                    }
                    sb.AppendLine("]);");

                    //if no appenders in runtime: simply use the created base path
                    sb.Append(@"
                    if(__buildInfo.Appenders is null)
                        ").AppendAssignUrlFunction(method).Append("__buildInfo.Path;");

                    if (CanWriteDirectly(method))
                    {
                        var parts = method.RouteParts!;
                        var firstText = parts.Length > 0 && parts[0].Text is { } text ? text : string.Empty;
                        // Joined as CreateRouteBuildInfo joins the base path and the method's route.
                        var startsWithSlash = method.Info.RouteTemplate.StartsWith("/", StringComparison.Ordinal) ? "true" : "false";
                        sb.Append(@"
                    else if(__direct").Append(method.HasRouteParameters ? "" : " && !Options." + nameof(ViaductOptions.UseRoutePathParameters)).Append(@")
                    {
                        ").Append(method.prefixVariable).Append(" = ").Append(BasePathVariable).Append(".EndsWith('/') || ").Append(startsWithSlash)
                            .Append(" ? ").Append(BasePathVariable).Append(" + ").Append(Literal(firstText))
                            .Append(" : ").Append(BasePathVariable).Append(" + \"/\" + ").Append(Literal(firstText)).Append(@";
                        ").Append(method.urlVariable).Append(" = ").Append(method.fastUrlMethod).Append(@";
                    }");
                    }

                    //if any parameter has upper case chars, there needs to be a 'tolowercase' variant
                    if (method.QueryParameters.Any(static p => p.Name.Any(char.IsUpper)))
                    {
                        sb.Append(@"
                    else if(Options.").Append(nameof(ViaductOptions.ForceLowerCaseRouteParameters)).Append(@")
                    {
                        ").AppendAssignUrlFunctionWithParamResolver(method, true).Append(@"
                    }");
                    }

                    //default implementation with parameters
                    sb.Append(@"
                    else
                    {
                        ").AppendAssignUrlFunctionWithParamResolver(method, false).Append(@"
                    }
                }").AppendLine();
                }

                /// <summary>
                /// A method that writes the address straight from the arguments: the prefix worked out once, the route's
                /// text and values, then the query. No delegate, closure or name lookup per call.
                /// </summary>
                void WriteDirectUrl(MethodInfo method)
                {
                    var parts = method.RouteParts!;
                    var routeParameters = new HashSet<string>(parts.Where(static p => p.Parameter is not null).Select(static p => p.Parameter!.Name));
                    var query = method.QueryParameters.Where(p => !routeParameters.Contains(p.Name)).ToArray();

                    sb.Append(@"
            string ").Append(method.fastUrlMethod).Append('(').AppendArguments(method.QueryParameters).Append(@")
            {
                var __prefix = this.").Append(method.prefixVariable).Append(@"!;
                var __sb = new global::System.Text.StringBuilder(__prefix.Length + 64);
                __sb.Append(__prefix);");

                    // The first text is in the prefix.
                    foreach (var part in parts.Skip(parts.Length > 0 && parts[0].Text is not null ? 1 : 0))
                    {
                        if (part.Text is { } text)
                            sb.Append(@"
                __sb.Append(").Append(Literal(text)).Append(");");
                        else
                            AppendValueStatement(part.Parameter!, string.Empty);
                    }

                    if (query.Length > 0)
                    {
                        sb.Append(@"
                var __sep = '?';");
                        foreach (var parameter in query)
                        {
                            // The name as the resolvers write it: lower-cased when the options say so.
                            var name = parameter.Name.Any(char.IsUpper)
                                ? $"(this.Options.{nameof(ViaductOptions.ForceLowerCaseRouteParameters)} ? {Literal(parameter.Name.ToLowerInvariant() + "=")} : {Literal(parameter.Name + "=")})"
                                : Literal(parameter.Name + "=");
                            AppendValueStatement(parameter, $"__sb.Append(__sep).Append({name}); ", " __sep = '&';");
                        }
                    }

                    sb.Append(@"
                return __sb.ToString();
            }
");
                }

                /// <summary>
                /// A statement that writes a parameter's value, after <paramref name="before"/>: nothing at all for a null,
                /// tested only as the type allows and without boxing, as the resolvers do.
                /// </summary>
                void AppendValueStatement(ParameterCreationInfo parameter, string before, string after = "")
                {
                    var name = parameter.Name;
                    switch (parameter.ValueKind)
                    {
                        case QueryValueKind.ValueType:
                            sb.Append(@"
                ").Append(before).Append(QueryValueWriter).Append(name).Append(");").Append(after);
                            break;
                        case QueryValueKind.NullableValueType:
                            sb.Append(@"
                if (").Append(name).Append(" is { } __").Append(name).Append("Value) { ").Append(before)
                              .Append(QueryValueWriter).Append("__").Append(name).Append("Value);").Append(after).Append(" }");
                            break;
                        case QueryValueKind.ReferenceType:
                            sb.Append(@"
                if (").Append(name).Append(" is not null) { ").Append(before).Append(QueryValueWriter).Append(name).Append(");").Append(after).Append(" }");
                            break;
                        default:
                            sb.Append(@"
                if ((object?)").Append(name).Append(" is not null) { ").Append(before).Append(QueryValueWriter).Append(name).Append(");").Append(after).Append(" }");
                            break;
                    }
                }

                void writeInterfaceImplementations()
                {
                    //add interface implementation. Use explicit implementation to avoid possible naming conflicts.
                    //The locals are prefixed with __ because they share the method's scope with its parameters,
                    //whose names are the interface's to choose: a parameter called request or url would not compile.

                    foreach (var method in methods)
                    {
                        sb.Append($@"
         {(method.Info.IsAsync ? "async " : string.Empty)}{method.Info.ReturnType.FullName} {info.FullyQualifiedName}.{method.Name}(")
                            .AppendArguments(method.Parameters).Append(@")
         {
            var __url = __routes.").Append(method.urlVariable).Append('(').AppendParameterNames(method.QueryParameters).Append(");");
                        sb.Append(@"
            var __request = CreateRequest(__routes.").Append(method.methodVariable).Append(", __url);");

                        if(method.Info.Body is { } body)
                        {
                            // Left out only when it is null: a value type equal to its default (0, an empty struct) is a body
                            // like any other, and was dropped by the "!= default" this replaced.
                            var test = body.ValueKind switch
                            {
                                QueryValueKind.ValueType => null,
                                QueryValueKind.NullableValueType or QueryValueKind.ReferenceType => $"{body.Name} is not null",
                                _ => $"(object?){body.Name} is not null",
                            };
                            if (test is null)
                                sb.Append($@"
            SetBody(__request, {body.Name});");
                            else
                                sb.Append($@"
            if({test})
                SetBody(__request, {body.Name});");
                        }

                        sb.Append(@"
            var __responseTask = SendAsync(__request").AppendCancellationTokenArg(method).Append(").ConfigureAwait(false);").AppendLine();

                        // Disposed once read: the response was handed back as soon as its headers were in.
                        if (method.Info.IsAsync)
                        {
                            sb.Append(@"
            using var __response = await __responseTask;
");
                        }
                        else
                        {
                            //not an awaitable, warned against in the compilation, but not disallowed completely
                            //force waiting for result....
                            sb.Append(@"
            #warning ").Append(ViaductErrors.NotAnAsyncMethod).Append(": Method ").Append(method.Name).Append(" on interface ").Append(info.Name).Append(" is not awaitable. Waiting for the I/O result might lead to deadlocks. Consider changing the method to Task or ValueTask");
                            if (method.Info.CancellationToken != null)
                                sb.Append(@"
            __responseTask.Wait(").AppendCancellationTokenArg(method).Append(@");");
                            sb.Append(@"
            using var __response = __responseTask.GetAwaiter().GetResult();");

                        }

                        var mi = method.Info;
                        if (mi.ReturnsValue)
                        {
                            sb.Append(@"
            return ");
                            if (mi.IsAsync)
                                sb.Append("await ");

                            sb.Append("ExtractResult<")
                                .Append((mi.InnerReturnType ?? mi.ReturnType).FullName)
                                .Append(">(__response").AppendCancellationTokenArg(method);
                            if (mi.IsAsync) sb.AppendLine(");");
                            else sb.AppendLine(").GetAwaiter().GetResult();");
                        }

                        sb.Append(@"
         }");
                    }
                }


            }

            extension(StringBuilder sb)
            {
                private StringBuilder AppendArguments(IReadOnlyList<ParameterCreationInfo> Parameters)
                {
                    for (int i = 0; i < Parameters.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var param = Parameters[i];
                        sb.Append(param.Type.FullName).Append(' ').Append(param.Name);
                    }
                    return sb;
                }

                private StringBuilder AppendConfigureArg()
             => sb.Append(@"Action<").Append(nameof(ViaductClientOptions)).Append(">? configure = null");

                private StringBuilder AppendArgumentTypes(IReadOnlyList<ParameterCreationInfo> Parameters)
                {
                    for (int i = 0; i < Parameters.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var param = Parameters[i];
                        sb.Append(param.Type.FullName);
                    }
                    return sb;
                }

                private StringBuilder AppendParameterNames(IReadOnlyList<ParameterCreationInfo> Parameters)
                {
                    for (int i = 0; i < Parameters.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var param = Parameters[i];
                        sb.Append(param.Name);
                    }
                    return sb;
                }

                StringBuilder AppendAssignUrlFunction(MethodInfo method)
                    => sb.Append(method.urlVariable).Append(" = (").AppendParameterNames(method.QueryParameters).Append(") => ");


                StringBuilder AppendCancellationTokenArg(MethodInfo method)
                {
                    if(method.Info.CancellationToken is not null and var ct)
                        sb.Append(", ").Append(ct.Name);
                    return sb;
                }

                StringBuilder AppendAssignUrlFunctionWithParamResolver(MethodInfo method, bool ToLowerCase)
                {
                    //this.Options and the __ names: the enclosing lambda's parameters are the method's, and one called
                    //Options or sb would otherwise be what these refer to.
                    sb.AppendAssignUrlFunction(method).Append(@"
                        this.Options.ResolveMethodRoute(__buildInfo");

                    if (method.QueryParameters.Length > 0)
                    {
                        sb.Append(@", (__sb,__parName) =>
                    {
                        var __res = __parName switch {");
                        foreach (var par in method.QueryParameters)
                        {
                            sb.Append(@"
                            """).Append(ToLowerCase ? par.Name.ToLower() : par.Name).Append("\" => ");
                            // A null value is left out of the query rather than sent empty ("on="), which a server
                            // cannot bind to a nullable value type: answering null with nothing appended drops the name.
                            // Tested only as the type allows, without boxing; a Nullable<T> is unwrapped so that the
                            // compiler picks the writer for T. AppendQueryValue writes as the server reads (invariant,
                            // escaped), where StringBuilder.Append wrote a date in the caller's culture.
                            switch (par.ValueKind)
                            {
                                case QueryValueKind.ValueType:
                                    sb.Append(QueryValueWriter).Append(par.Name).Append("),");
                                    break;
                                case QueryValueKind.NullableValueType:
                                    sb.Append(par.Name).Append(" is { } __").Append(par.Name).Append("Value ? ").Append(QueryValueWriter).Append("__").Append(par.Name).Append("Value) : null,");
                                    break;
                                case QueryValueKind.ReferenceType:
                                    sb.Append(par.Name).Append(" is null ? null : ").Append(QueryValueWriter).Append(par.Name).Append("),");
                                    break;
                                default:
                                    sb.Append("(object?)").Append(par.Name).Append(" is null ? null : ").Append(QueryValueWriter).Append(par.Name).Append("),");
                                    break;
                            }
                        }
                        sb.Append(@"
                           _ => null
                        };
                        return __res != null;
                    }");
                    }
                    return sb.Append(");");
                }
            }
        }
    }
}
