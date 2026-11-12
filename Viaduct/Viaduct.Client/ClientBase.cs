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
    /// Base class for the generated HTTP client proxies: what every generated client inherits, and where the
    /// request building, sending and reading live.
    /// </summary>
    public partial class ViaductClientBase
    {
        /// <summary>A client with options of its own, made from <paramref name="configure"/>.</summary>
        public ViaductClientBase(HttpClient client, Action<ViaductClientOptions>? configure = null)
            : this(client, configure.GetOptionsInstance())
        {
        }

        /// <summary>
        /// A client on options made once for its registration and shared by every client made from it: a typed client is
        /// made for every injection, and this keeps that cheap.
        /// </summary>
        public ViaductClientBase(HttpClient client, ViaductClientOptions options)
        {
            Client = client;
            Options = options;
        }

        /// <summary>
        /// The HTTP client used for making requests.
        /// </summary>
        protected readonly HttpClient Client;
        /// <summary>
        /// The options with which the client was created
        /// </summary>
        /// <remarks>
        /// The class is initialized during creation and is meant as readonly after that. Changing values of these options will NOT alter behaviour.
        /// </remarks>
        public readonly ViaductClientOptions Options;

        /// <summary>
        /// What one generated method needs to make its call: the route to build, and the verb to send.
        /// </summary>
        protected partial record CallInfo(UrlBuildInfo UrlBuildInfo, HttpMethod HttpMethod);


        /// <summary>The request for a call, before its body and headers are filled in.</summary>
        protected HttpRequestMessage CreateRequest(CallInfo info, string url)
            => new(info.HttpMethod, url);

        /// <summary>The request for a call, before its body and headers are filled in.</summary>
        protected HttpRequestMessage CreateRequest(HttpMethod method, string url)
            => new(method, url);

        /// <summary>
        /// Serializes <paramref name="body"/> onto the request as JSON, with the contract <see cref="GetJsonTypeInfo{T}"/>
        /// gives: the server reads it with the same.
        /// </summary>
        protected void SetBody<T>(HttpRequestMessage request, T body)
            => request.Content = JsonContent.Create(body, GetJsonTypeInfo<T>());

        /// <summary>
        /// The most of a failure response kept in <see cref="ViaductHttpException.ResponseBody"/>. Enough for
        /// a problem-details document or a stack trace, short of pulling an entire HTML error page into an
        /// exception message.
        /// </summary>
        private const int MaxErrorBodyLength = 4096;

        /// <summary>
        /// Sends the request and returns the response, or throws. The response is handed back once its headers are in,
        /// so its JSON is read from the stream rather than buffered first; the caller disposes it.
        /// </summary>
        /// <exception cref="ViaductHttpException">
        /// The server answered with a failure status. Carries the status code and the response body, which is
        /// what tells a permission refusal from a missing record.
        /// </exception>
        /// <exception cref="ViaductException">The request never got an answer that could be read.</exception>
        protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Kept before sending: the request is disposed along with the response, and these two are what the
            // exception message needs. The address is written out only for that message, not on every call.
            var method = request.Method?.Method;
            var requestUri = request.RequestUri;

            HttpResponseMessage res;
            try
            {
                res = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller cancelled. Wrapping that would hide it from every `catch (OperationCanceledException)`
                // above, and it is not a failure of the call.
                throw;
            }
            catch (Exception ex)
            {
                throw new ViaductException($"Error calling [{method} {requestUri}]: {ex.Message}", ex);
            }

            if (!res.IsSuccessStatusCode)
            {
                using (res)
                    throw await CreateHttpExceptionAsync(res, method, requestUri?.ToString(), ct).ConfigureAwait(false);
            }

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

        /// <summary>
        /// The JSON contract for <typeparamref name="T"/>: the client's own resolver, else Subro.Json's, the one the server
        /// writes and reads with too. Never null: a type no context has is read by reflection with the same settings and
        /// converters, or, in a trimmed or AOT app, is an error that names it.
        /// </summary>
        protected JsonTypeInfo<T> GetJsonTypeInfo<T>() => Options.GetJsonTypeInfo<T>();

        /// <summary>
        /// extracts the desired type from the response
        /// </summary>
        protected async internal Task<T> ExtractResult<T>(HttpResponseMessage response, CancellationToken token = default)
            => (await response.Content.ReadFromJsonAsync(GetJsonTypeInfo<T>(), token).ConfigureAwait(false))!;


    }
}
