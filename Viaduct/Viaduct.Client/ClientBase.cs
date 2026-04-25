using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using System.Xml;
using Viaduct.Client.UrlBuilding;

namespace Viaduct.Client
{
    /// <summary>
    /// The service callers inherit from this base class
    /// </summary>
    /// <summary>
    /// Base class for generated HTTP client proxies.
    /// </summary>
    public partial class ViaductClientBase(HttpClient client, Action<ViaductClientOptions>? configure = null)
    {
        /// <summary>
        /// The HTTP client used for making requests.
        /// </summary>
        protected readonly HttpClient Client = client;
        /// <summary>
        /// The options with which the client was created
        /// </summary>
        /// <remarks>
        /// The class is initialized during creation and is meant as readonly after that. Changing values of these options will NOT alter behaviour.
        /// </remarks>
        public readonly ViaductClientOptions Options = configure.GetOptionsInstance();

        protected partial record CallInfo(UrlBuildInfo UrlBuildInfo, HttpMethod HttpMethod);


        protected HttpRequestMessage CreateRequest(CallInfo info, string url)
            => new(info.HttpMethod, url);

        protected void SetBody<T>(HttpRequestMessage request, T body)
        {
            var info = GetJsonTypeInfo<T>();
            JsonContent content;
            if (info is null)
#pragma warning disable IL2026, IL3050
                // until JsonSerializerContext support is added, attempt to deserialize without type info for non-AOT environments
                //Hopefully in the future it is possible to provide generator order, or another way to emulate the same as JsonSerializable from within the code
                //For now it is accepted that a user can use IL instead, even though the goal is to create AOT code
                content = JsonContent.Create(body);
#pragma warning restore IL2026, IL3050
            else
                content = JsonContent.Create(body, info);

            request.Content = content;
        }

        protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            HttpResponseMessage res;
            try
            {
                res = await Client.SendAsync(request, ct).ConfigureAwait(false);
                res.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                throw new ViaductException($"Error calling [{request}]: {ex.Message}", ex);
            }
            return res;
        }

        protected JsonTypeInfo<T>? GetJsonTypeInfo<T>()
        {
            var info = Options.GetJsonTypeInfo<T>();
#if DEBUG
            // In AOT environments null info will cause ReadFromJsonAsync to fail.
            // Provide a JsonSerializerContext via ViaductOptions.JsonOptions for AOT support.
            if (info is null && !JsonSerializer.IsReflectionEnabledByDefault)
                throw new ViaductException(
                    $"No JsonTypeInfo found for '{typeof(T).Name}'. " +
                    $"AOT detected - provide a JsonSerializerContext via ViaductOptions.JsonOptions.");
#endif
            return info;
        }

        /// <summary>
        /// extracts the desired type from the response
        /// </summary>
        protected async internal Task<T> ExtractResult<T>(HttpResponseMessage response, CancellationToken token = default)
        {            
            var info = GetJsonTypeInfo<T>();
            if(info is null)
            {
#pragma warning disable IL2026 , IL3050
                // until JsonSerializerContext support is added, attempt to deserialize without type info for non-AOT environments
                //Hopefully in the future it is possible to provide generator order, or another way to emulate the same as JsonSerializable from within the code
                //For now it is accepted that a user can use IL instead, even though the goal is to create AOT code
                return (await response.Content.ReadFromJsonAsync<T>(token).ConfigureAwait(false))!;
#pragma warning restore IL2026 , IL3050
            }
            else
                return (await response.Content.ReadFromJsonAsync<T>(info, token).ConfigureAwait(false))!;
            
        }


    }
}
