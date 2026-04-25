using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Viaduct;
using Viaduct.Generation;

namespace Viaduct.Generation
{
    partial class ViaductGenerator
    {
    }

    //internal record GeneratedInterfaceInfo(string ClassName, string Namespace, string Code);

    partial class ViaductGeneratorFunctions
    {
        const string serverOptions = "ViaductServerOptions";
        static class Server
        {
            static string createMappingFunction(string method)
            {                
                return $$"""
            public static IServiceCollection {{method}}{{AndMapSuffix}}<TInterface, TImplementation>(this IServiceCollection services, Action<{{serverOptions}}>? configure = null)
                where TInterface : class
                where TImplementation : class, TInterface
            {
                services.{{method}}<TInterface, TImplementation>();
                return Register(services,configure);
            }

            """;
            }

            const string AndMapSuffix = "AndMap";

            static void addMappingFunction(string method) => Methods.Add(method + AndMapSuffix, createMappingFunction(method));

            static Server()
            {
                addMappingFunction("AddScoped");
                addMappingFunction("AddTransient");
                addMappingFunction("AddSingleton");
            }

            public static readonly Dictionary<string, string> Methods = new()
            {
                {"RegisterEndpoints", 
                    $"           public static IServiceCollection RegisterEndpoints<TInterface>(this IServiceCollection services, Action<{serverOptions}>? configure = null) => Register(services, configure);"},
                {"AddEndpoints", 
                    $"          public static IEndpointRouteBuilder AddEndpoints<TInterface>(this IEndpointRouteBuilder app, Action<{serverOptions}>? configure = null){{ {mappingMethodName}(app, configure.GetOptionsInstance()); return app; }}"},
            };
        }        

        const string mappingMethodName = "Map";
        public static string CreateServerMappingCode(InterfaceMetaData info, IEnumerable<MethodCallerInfo> callerMethods)
        {
            string className = $"{info.Name}_Viaduct_ServerMappings";
            string Namespace = info.Namespace;
            var sb = new StringBuilder($$"""
#nullable enable
using Viaduct;
using Viaduct.Server;
using static Viaduct.Server.ViaductServerHelperFunctions;
using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using {{Namespace}};


{{interceptorAttribute}}


namespace {{GeneratedNameSpace}}
{
    file static class {{className}}
    {        
        public static IServiceCollection Register(IServiceCollection services, Action<{{serverOptions}}>? configure = null)
        {            
            services.AddJsonTypeResolvers();
            PendingMappers.Push({{mappingMethodName}},configure);  
            return services;
        }

""");
            //append interceptors
            foreach(var methodGroup in callerMethods.GroupBy(m => m.Name))
            {
                var method = methodGroup.Key;
                var code = Server.Methods[method];

                foreach (var caller in methodGroup)
                    sb.AppendInterceptor(caller);
                sb.Append(code);
            }

            sb.Append($$"""

        public static void {{mappingMethodName}}(IEndpointRouteBuilder app, {{serverOptions}} options)
        {
            options ??= {{serverOptions}}.Default;
            var basePath = {{createGetBasePathCode(info.BasePath, info.Name)}};
            var group = app.MapGroup(basePath);

            if(options.RequiresAuthorization)
                group.RequireAuthorization();

            var baseRouteParams = GetRouteParameters(basePath).ToList();

            //add mappings
            StringBuilder sbMethod = null;
""");

            foreach (var method in info.Methods)
            {
                if (method.IgnoreForServerGeneration) continue;

                sb.Append($@"
            {{ //{method.Name}                   
                var route = ""{method.RouteTemplate}"";
                var paramEnumerable = baseRouteParams.Concat(GetRouteParameters(route));
                if(options.{nameof(ViaductOptions.ForceLowerCaseRouteParameters)})
                    paramEnumerable = paramEnumerable.Select(name=>name.ToLower());
                var routeParams =  new HashSet<string>(paramEnumerable);
;");
                if (method.Parameters.Length > 0) //add route path parameters if not predefined (in runtime depending on options)
                {
                    sb.Append($@"
                if(options.{nameof(ViaductOptions.UseRoutePathParameters)})
                {{
                    if(sbMethod == null)
                        sbMethod = new StringBuilder(route);
                    else
                    {{
                        sbMethod.Length = 0;
                        sbMethod.Append(route);
                    }}
                    
                    void AppendRouteParameter(string parameterName)
                    {{                        
                        if(options.{nameof(ViaductOptions.ForceLowerCaseRouteParameters)})
                            parameterName = parameterName.ToLower();
                        if(routeParams.Remove(parameterName)) return;  //parameter was defined in predefined route, do not add again
                        sbMethod.Append(parameterName);
                    }}
");  //TODO: check cases like optional when adding route parameters

                    foreach (var param in method.Parameters)
                    {
                        if (param.QueryParameter)
                            sb.Append($@"                    
                        AppendRouteParameter(""/{{{param.Name}}}"");"); 
                    }
                    sb.Append($@"                                
                    route = sbMethod.ToString();
                }}
                else if(routeParams.Count > 0)
                {{");

                    foreach (var param in method.Parameters)
                        sb.Append($@"
                    routeParams.Remove(""{param.Name}"");");
                    //remove route parameters from template if not using route path parameters
                    sb.Append(@"
                }
");
                } //end of parameter loop


                sb.Append($@"
            if(routeParams.Count > 0)
                throw new ViaductException(""Route template for method '{method.Name}' contains parameters that cannot be found in the method signature. Parameters: "" + string.Join("", "", routeParams));
            group.MapMethods(route, [""{method.HttpMethod}""], 
                {(method.IsAsync ? "async " : "")}(");
                for (int i = 0; i < method.Parameters.Length; i++)
                {
                    var param = method.Parameters[i];
                    if (i > 0) sb.Append(", ");
                    if (!param.QueryParameter && param != method.CancellationToken) sb.Append("[FromBody] ");
                    sb.Append($"{param.Type.FullName} {param.Name}");
                }

                if (method.Parameters.Length > 0) sb.Append(", ");
                sb.Append("[FromServices] ").Append(info.Name).Append(@" service) => {");
                if (method.ReturnsValue)
                    sb.Append("return ");
                if (method.IsAsync)
                    sb.Append("await ");
                sb.Append("service.").Append(method.Name).Append('(');
                for (int i = 0; i < method.Parameters.Length; i++)
                {
                    var param = method.Parameters[i];
                    if (i > 0) sb.Append(", ");
                    sb.Append(param.Name);
                }
                if (method.IsAsync)
                    sb.Append(").ConfigureAwait(false);");
                else
                    sb.Append(");");


                if (!method.ReturnsValue)
                    sb.Append("return TypedResults.Ok();");

                sb.Append(@"                    
                    });
                }
");
            } //end method loop

            sb.Append("""            
        }
    }
}
""");

            return sb.ToString();
        }   
    }



}