using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// A user's base topics: what their connection hears without asking, such as their own topic, their rooms and their
    /// clubs. Every registered resolver adds to them. Resolved in a scope of its own when a connection opens, and again
    /// when <see cref="IViaductSignals.TopicsChangedAsync"/> is published for the user.
    /// </summary>
    public interface ISignalTopicResolver
    {
        /// <summary>The topics <paramref name="user"/> hears; anonymous callers have an unauthenticated principal.</summary>
        ValueTask<IReadOnlyCollection<string>> GetTopicsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Whether a user may hear a topic a page asked for, such as a record being edited, for the topics of one
    /// <see cref="Template"/>. A page topic that is no base topic and that no rule allows is refused.
    /// </summary>
    public interface ISignalTopicAccess
    {
        /// <summary>The topics this rule decides on.</summary>
        SignalTopicTemplate Template { get; }

        /// <summary>Whether <paramref name="user"/> may hear <paramref name="topic"/>, which matches <see cref="Template"/>.</summary>
        ValueTask<bool> CanListenAsync(ClaimsPrincipal user, string topic, CancellationToken cancellationToken = default);
    }

    /// <summary>The user's own topic (<see cref="SignalTopics.User"/>), for a signed-in user with an id claim.</summary>
    public sealed class UserSignalTopicResolver(IOptionsMonitor<SignalOptions> options) : ISignalTopicResolver
    {
        /// <inheritdoc/>
        public ValueTask<IReadOnlyCollection<string>> GetTopicsAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(user);
            var id = UserId(user, options.CurrentValue);
            return new(id is null ? [] : [SignalTopics.ForUser(id)]);
        }

        /// <summary>The user's id from the first of <see cref="SignalOptions.UserIdClaimTypes"/> they have; null when anonymous.</summary>
        public static string? UserId(ClaimsPrincipal user, SignalOptions options)
        {
            if (user.Identity?.IsAuthenticated != true)
                return null;
            foreach (var type in options.UserIdClaimTypes)
                if (user.FindFirst(type)?.Value is { Length: > 0 } id)
                    return id;
            return null;
        }
    }

    /// <summary>An access rule given as a function.</summary>
    public sealed class DelegateSignalTopicAccess(SignalTopicTemplate template, Func<ClaimsPrincipal, string, CancellationToken, ValueTask<bool>> canListen)
        : ISignalTopicAccess
    {
        /// <inheritdoc/>
        public SignalTopicTemplate Template { get; } = template;

        /// <inheritdoc/>
        public ValueTask<bool> CanListenAsync(ClaimsPrincipal user, string topic, CancellationToken cancellationToken = default)
            => canListen(user, topic, cancellationToken);
    }
}
