using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Viaduct.Client
{
    /// <summary>
    /// Supplies the access token for outgoing Viaduct calls.
    /// </summary>
    /// <remarks>
    /// Register an implementation to have every client that requires authorization send a token. It is asked
    /// per request rather than once, so a token that is refreshed, or that arrives only after the user signs
    /// in, is picked up without rebuilding anything.
    /// <para>
    /// For a token that needs no services of its own, <see cref="ViaductClientOptions.GetAccessToken"/> is the
    /// shorter way to say the same thing.
    /// </para>
    /// </remarks>
    public interface IViaductAccessTokenProvider
    {
        /// <summary>
        /// The token for this request, or <c>null</c> when there is none — nobody is signed in yet, or the
        /// session has expired. The request is then sent unauthenticated, and the server decides.
        /// </summary>
        /// <param name="request">The request about to be sent, for a provider that scopes tokens per API.</param>
        /// <param name="cancellationToken">Cancels acquiring the token.</param>
        ValueTask<string?> GetAccessTokenAsync(HttpRequestMessage request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Puts the access token on each outgoing request.
    /// </summary>
    /// <remarks>
    /// A handler rather than something inside the client proxy, so the token is attached once per attempt —
    /// a retry or a redirect added further down the pipeline carries a freshly asked-for token rather than the
    /// one from the first try.
    /// </remarks>
    public sealed class ViaductAuthorizationHandler(
        ViaductClientOptions options,
        IViaductAccessTokenProvider? provider = null) : DelegatingHandler
    {
        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // An Authorization header already on the request was put there deliberately, by a caller who knows
            // something this handler does not. It is never replaced.
            if (request.Headers.Authorization is null)
            {
                var token = options.GetAccessToken is { } getToken
                    ? await getToken(request, cancellationToken).ConfigureAwait(false)
                    : provider is not null
                        ? await provider.GetAccessTokenAsync(request, cancellationToken).ConfigureAwait(false)
                        : null;

                if (!string.IsNullOrEmpty(token))
                    request.Headers.Authorization = new AuthenticationHeaderValue(options.AuthorizationScheme, token);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public static class ViaductAuthorizationExtensions
    {
        /// <summary>
        /// Adds the token handler to a generated client when its options require authorization.
        /// </summary>
        /// <remarks>
        /// Called from generated registration code; there is no need to call it directly.
        /// <para>
        /// The token source is resolved when the handler is built rather than now, so an
        /// <see cref="IViaductAccessTokenProvider"/> registered after the client still counts. A client that
        /// requires authorization with no source at all is a configuration mistake that would otherwise show
        /// up as unexplained 401s, so it throws instead — with the two ways to fix it.
        /// </para>
        /// </remarks>
        public static IHttpClientBuilder AddViaductAuthorization(
            this IHttpClientBuilder builder,
            Action<ViaductClientOptions>? configure = null)
        {
            var options = configure.GetOptionsInstance();

            if (!options.RequiresAuthorization)
                return builder;

            return builder.AddHttpMessageHandler(services =>
            {
                var provider = services.GetService<IViaductAccessTokenProvider>();

                if (options.GetAccessToken is null && provider is null)
                {
                    throw new ViaductException(
                        $"The client for '{builder.Name}' requires authorization, but nothing supplies a token. "
                        + $"Set {nameof(ViaductClientOptions)}.{nameof(ViaductClientOptions.GetAccessToken)} when registering it, "
                        + $"or register an {nameof(IViaductAccessTokenProvider)} implementation.");
                }

                return new ViaductAuthorizationHandler(options, provider);
            });
        }
    }
}
