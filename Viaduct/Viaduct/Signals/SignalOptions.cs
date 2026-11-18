using System;
using System.Collections.Generic;

namespace Viaduct.Signals
{
    /// <summary>
    /// The server's signal settings. Read through <c>IOptionsMonitor</c>, so a change applies without a restart; bind them
    /// from configuration (<c>Signals</c>) or feed them from the application's own settings with an
    /// <c>IConfigureOptions&lt;SignalOptions&gt;</c>.
    /// </summary>
    public sealed class SignalOptions
    {
        /// <summary>
        /// How long a signal is kept for clients catching up after a gap. A client away longer gets a reset and loads what
        /// it shows again. Default 24 hours.
        /// </summary>
        public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// The largest payload accepted, in UTF-8 bytes: signals say what changed, the data is fetched. Default 4 KB.
        /// </summary>
        public int MaxPayloadBytes { get; set; } = 4096;

        /// <summary>
        /// How many signals the in-memory store keeps at most, besides <see cref="Retention"/>. Default 10,000.
        /// </summary>
        public int InMemoryCapacity { get; set; } = 10_000;

        /// <summary>
        /// How many signals a listener may have waiting before it is closed; its client then reconnects and catches up.
        /// Default 1,000.
        /// </summary>
        public int ListenerCapacity { get; set; } = 1_000;

        /// <summary>
        /// How often a stream sends a comment when nothing else is sent, so proxies do not close it as idle. Default 25 seconds.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(25);

        /// <summary>The longest a long poll (<c>?wait=</c>) is held. Default 60 seconds.</summary>
        public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>How many topics a client may ask for besides its base topics. Default 50.</summary>
        public int MaxTopics { get; set; } = 50;

        /// <summary>
        /// End a stream when the caller's access token expires (its <c>exp</c> claim), so the client reconnects with a fresh
        /// one and its topics are checked again. Default true.
        /// </summary>
        public bool EndAtTokenExpiry { get; set; } = true;

        /// <summary>
        /// Map the endpoint with <c>RequireAuthorization()</c>. Default true, like the rest of an API; turn it off to serve
        /// public topics to anonymous callers. Read when the endpoint is mapped.
        /// </summary>
        public bool RequireAuthorization { get; set; } = true;

        /// <summary>
        /// The claims that hold a user's id, for their own topic (<see cref="SignalTopics.User"/>), first found wins.
        /// Default the name identifier, then <c>sub</c>.
        /// </summary>
        public string[] UserIdClaimTypes { get; set; } = [System.Security.Claims.ClaimTypes.NameIdentifier, "sub"];

        /// <summary>
        /// The signals (type keys) after which a stream works out its user's topics again, and checks its page topics again:
        /// <see cref="IViaductSignals.TopicsChangedAsync"/>, and whatever the application adds, such as its own signal that
        /// someone's roles changed (<see cref="SignalServerBuilder.ResolveTopicsAgainOn"/>). What follows one waits for the
        /// new topics, so nothing is filtered with the old ones.
        /// </summary>
        public HashSet<string> ResolveTopicsAgainOn { get; } = new(StringComparer.Ordinal) { ViaductSignalsContract.Instance.TopicsChanged.Key };
    }
}
