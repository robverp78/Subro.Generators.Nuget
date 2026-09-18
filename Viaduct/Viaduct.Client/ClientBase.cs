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

        /// <summary>
        /// The most of a failure response kept in <see cref="ViaductHttpException.ResponseBody"/>. Enough for
        /// a problem-details document or a stack trace, short of pulling an entire HTML error page into an
        /// exception message.
        /// </summary>
        private const int MaxErrorBodyLength = 4096;

        /// <summary>
        /// Sends the request and returns the response, or throws.
        /// </summary>
        /// <exception cref="ViaductHttpException">
        /// The server answered with a failure status. Carries the status code and the response body, which is
        /// what tells a permission refusal from a missing record.
        /// </exception>
        /// <exception cref="ViaductException">The request never got an answer that could be read.</exception>
        protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Read before sending: the request is disposed along with the response, and these two are what the
            // exception message needs.
            var method = request.Method?.Method;
            var uri = request.RequestUri?.ToString();

            HttpResponseMessage res;
            try
            {
                res = await Client.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller cancelled. Wrapping that would hide it from every `catch (OperationCanceledException)`
                // above, and it is not a failure of the call.
                throw;
            }
            catch (Exception ex)
            {
                throw new ViaductException($"Error calling [{method} {uri}]: {ex.Message}", ex);
            }

            if (!res.IsSuccessStatusCode)
                throw await CreateHttpExceptionAsync(res, method, uri, ct).ConfigureAwait(false);

            return res;
        }

        /// <summary>
        /// Builds the exception for a failure response, including whatever the server said in the body.
        /// </summary>
        private static async Task<ViaductHttpException> CreateHttpExceptionAsync(
            HttpResponseMessage response,
            string? method,
            string? uri,
            CancellationToken ct)
        {
            string? body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (body != null && body.Length > MaxErrorBodyLength)
                    body = body.Substring(0, MaxErrorBodyLength) + "… (truncated)";
            }
            catch
            {
                // An unreadable body is not worth losing the status code over — that is the part a caller acts
                // on, and it is already in hand.
            }

            return new ViaductHttpException(
                (int)response.StatusCode,
                response.ReasonPhrase,
                body,
                method,
                uri);
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
