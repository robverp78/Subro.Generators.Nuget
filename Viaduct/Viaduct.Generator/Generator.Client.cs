using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Reflection;
using System.Security.Authentication.ExtendedProtection;
using System.Text;
using Viaduct.Client;
using static Viaduct.Generation.ViaductGeneratorFunctions;

namespace Viaduct.Generation
{
    partial class ViaductGenerator
    {
    }

    namespace Client
    {
        public static partial class ClientGeneratorFunctions
        {
            record MethodInfo(MethodCreationInfo Info, int Index)
            {
                public readonly string urlVariable = $"{Info.Name}_UrlCreator_{Index}";
                public readonly string infoVariable = $"{Info.Name}_Info_{Index}";
                public string Name => Info.Name;

                public ParameterCreationInfo[] QueryParameters = [.. Info.GetQueryParameters()];

                public ParameterCreationInfo[] Parameters => Info.Parameters;
            }

            public class ClientCodeCreator(InterfaceMetaData info, IEnumerable<MethodCallerInfo> methodCallers)
            {

                readonly InterfaceMetaData info = info;
                readonly MethodInfo[] methods = [.. info.Methods.Select((m,i)=>  new MethodInfo(m,i))];
                readonly MethodCallerInfo[] callers = [.. methodCallers];
                readonly StringBuilder sb = new();
                readonly string ClassName = $"{clientClass}_{info.Name}";


                const string clientClass = nameof(ViaductClientBase);
                const string clientOptions = nameof(ViaductClientOptions);
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
using ").Append(info.Namespace).Append(';').AppendLine()
    .AppendLine(interceptorAttribute)
    .Append($@"
    
namespace {GeneratedNameSpace}
{{");

                    WriteInterceptors();

    sb.Append($@"
    file class {ClassName}:{clientClass},{info.Namespace}.{info.Name}
    {{       
        readonly string {BasePathVariable};

");
                    WriteMethodVariables();

                    sb.Append($@"

        public {ClassName}(HttpClient client, ").AppendConfigureArg().Append($@") : base(client,  configure)
        {{            
            {BasePathVariable} = {createGetBasePathCode(info.BasePath, info.Name,"Options")};
").AppendLine();

                    writemethodInitializers();

                    sb.Append("""
        }
"""); //close array and constructor


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
    file static class ").Append(info.Name).Append("_interceptors").Append(@"
    {");
                    foreach(var caller in callers)
                    {
                        sb.AppendInterceptor(caller);             
                    }

                    sb.Append(@"
        public static IHttpClientBuilder AddMappedHttpClient(this IServiceCollection services, ").AppendConfigureArg().Append(@")
        {
            return services.AddHttpClient<").Append(info.Name).Append(", ").Append(ClassName).Append(@">(client =>
                {
                    var res = new ").Append(ClassName).Append(@"(client, configure);
                    if(res.Options.").Append(nameof(ViaductClientOptions.BaseUrl)).Append(@" is not null and var BaseUrl)
                        client.BaseAddress = new Uri(BaseUrl);
                    return res;
                });
        }
    }");
                }

                void WriteMethodVariables()
                {
                    foreach (var method in methods)
                    {
                        //create url function variable
                        sb.Append("\t\treadonly Func<").AppendArgumentTypes(method.QueryParameters);
                        if (method.QueryParameters.Length > 0) sb.Append(',');
                        sb.Append("string> ").Append(method.urlVariable).Append(';').AppendLine()
                        .Append("\t\treadonly ").Append(nameof(CallInfo)).Append(' ').Append(method.infoVariable).Append(';').AppendLine();

                    }

                }

                void writemethodInitializers()
                {
                    foreach (var method in methods)
                    {
                        //route builder creation
                        sb.AppendLine().Append($$"""
            { //{{method.Name}}
                var buildInfo = Options.CreateRouteBuildInfo({{BasePathVariable}},"{{method.Name}}",[
""");
                        for (int i = 0; i < method.QueryParameters.Length; i++)
                        {
                            if (i > 0) sb.Append(", ");
                            sb.Append("\"").Append(method.QueryParameters[i].Name).Append("\"");
                        }
                        sb.AppendLine("]);");


                        //store the callinfo
                        sb.Append($@"
                {method.infoVariable} = new ").Append(nameof(CallInfo)).Append("(buildInfo, HttpMethod.Parse(\"")
                    .Append(method.Info.HttpMethod).AppendLine("\"));");

                        //if no appenders in runtime: simply use the created base path
                        sb.Append(@$"                
                if(buildInfo.Appenders is null)
                    ").AppendAssignUrlFunction(method).AppendPathReference(method).Append(';');

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
                }");

                        sb.Append(@"
            }").AppendLine();
                    }
                }

                void writeInterfaceImplementations()
                {
                    //add interface implementation. Use explicit implementation to avoid possible naming conflicts 

                    foreach (var method in methods)
                    {
                        sb.Append($@"         
         {(method.Info.IsAsync ? "async " : string.Empty)}{method.Info.ReturnType.FullName} {info.Name}.{method.Name}(")
                            .AppendArguments(method.Parameters).Append(@")
         {
            var url = ").Append(method.urlVariable).Append('(').AppendParameterNames(method.QueryParameters).Append(");");
                        sb.Append(@"
            var info = ").Append(method.infoVariable).Append(';').Append(@"
            var request = CreateRequest(info, url);");

                        if(method.Info.Body is not null)
                        {
                            sb.Append($@"
            if({method.Info.Body.Name} != default)
                SetBody(request, {method.Info.Body.Name});");
                        }

                        sb.Append(@"
            var responseTask = SendAsync(request").AppendCancellationTokenArg(method).Append(").ConfigureAwait(false);").AppendLine();

                        if (method.Info.IsAsync)
                        {
                            sb.Append(@"
            var response = await responseTask;
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
            responseTask.Wait(").AppendCancellationTokenArg(method).Append(@");");
                            sb.Append(@"
            var response = responseTask.GetAwaiter().GetResult();");
                            
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
                                .Append(">(response").AppendCancellationTokenArg(method);
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
                private StringBuilder AppendArguments(ParameterCreationInfo[] Parameters)
                {
                    for (int i = 0; i < Parameters.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var param = Parameters[i];
                        sb.Append(param.Type.FullName).Append(' ').Append(param.Name);
                    }
                    return sb;
                }

                private StringBuilder AppendConfigureArg()
             => sb.Append(@"Action<").Append(nameof(ViaductClientOptions)).Append(">? configure = null");

                private StringBuilder AppendArgumentTypes(ParameterCreationInfo[] Parameters)
                {
                    for (int i = 0; i < Parameters.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var param = Parameters[i];
                        sb.Append(param.Type.FullName);
                    }
                    return sb;
                }

                private StringBuilder AppendParameterNames(ParameterCreationInfo[] Parameters)
                {
                    for (int i = 0; i < Parameters.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var param = Parameters[i];
                        sb.Append(param.Name);
                    }
                    return sb;
                }

                private StringBuilder AppendBuilderReference(MethodInfo method)
                    => sb.Append(method.infoVariable).Append(".UrlBuildInfo");

                private StringBuilder AppendPathReference(MethodInfo method)
                    => sb.AppendBuilderReference(method).Append(".Path");

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
                    sb.AppendAssignUrlFunction(method).Append(@"
                        Options.ResolveMethodRoute(").AppendBuilderReference(method);

                    if (method.QueryParameters.Length > 0)
                    {
                        sb.Append(@", (sb,parName) =>
                    {
                        var res = parName switch {");
                        foreach (var par in method.QueryParameters)
                        {
                            sb.Append(@"
                            """).Append(ToLowerCase ? par.Name.ToLower() : par.Name).Append("\" => ");
                            if (par.IsSimpleType)
                                sb.Append("sb.Append(").Append(par.Name).Append("),");
                            else
                                sb.Append("sb.AppendRoutePart(").Append(par.Name).Append("),");
                        }
                        sb.Append(@"
                           _ => null
                        };
                        return res != null;
                    }");
                    }
                    return sb.Append(");");
                }
            }
        }
    }
}