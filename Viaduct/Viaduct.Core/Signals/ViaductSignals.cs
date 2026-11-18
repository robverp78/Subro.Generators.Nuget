using System.Threading;
using System.Threading.Tasks;

namespace Viaduct.Signals
{
    /// <summary>
    /// Viaduct's own signals. Registered on every server by <c>AddSignals</c>: inject it and call it.
    /// </summary>
    [Signals("viaduct")]
    public interface IViaductSignals
    {
        /// <summary>
        /// The topics this user may hear changed, such as after joining a room or losing a role: their open connections
        /// work their topics out again, without reconnecting.
        /// </summary>
        [SignalTopic(SignalTopics.User)]
        ValueTask TopicsChangedAsync(string userId, CancellationToken cancellationToken = default);
    }

    /// <summary>Topics Viaduct itself knows.</summary>
    public static class SignalTopics
    {
        /// <summary>
        /// A user's own topic, which every signed-in connection hears: a contract addresses a person with
        /// <c>[SignalTopic(SignalTopics.User)]</c> and a <c>userId</c> parameter.
        /// </summary>
        public const string User = "user/{userId}";

        /// <summary>The topic of the user with this id.</summary>
        public static string ForUser(string userId) => "user/" + SignalTopicValues.Format(userId);
    }
}
