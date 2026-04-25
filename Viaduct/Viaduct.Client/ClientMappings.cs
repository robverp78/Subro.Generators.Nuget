using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Viaduct.Client
{


    public static class ClientMappings
    {
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.None)]
        public static IHttpClientBuilder CreateAndAddHttpClient<TInterface>(this IServiceCollection services, Action<ViaductClientOptions>? configure = null)
            where TInterface: class
        {
            throw new ViaductMethodNotInterceptedException();          
        }

        public static ViaductClientOptions GetOptionsInstance(this Action<ViaductClientOptions>? configure)
            => configure.GetOptionsInstance(ViaductClientOptions.Default);
    }
}
