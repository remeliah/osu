// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Mods;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using osu.Framework.IO.Network;
using osu.Game.Extensions;
using osu.Game.Screens.Play.Leaderboards;

namespace osu.Game.Online.API.Requests
{
    public class GetScoresRequest : APIRequest<APIScoresCollection>, IEquatable<GetScoresRequest>
    {
        public const int DEFAULT_SCORES_PER_REQUEST = 50;
        public const int MAX_SCORES_PER_REQUEST = 100;

        public int ScoresRequested { get; }

        public IBeatmapInfo BeatmapInfo { get; }
        public BeatmapLeaderboardScope Scope { get; }
        public IRulesetInfo Ruleset { get; }
        public IReadOnlyList<IMod> Mods { get; }

        /// <summary>
        /// Whether <see cref="Mods"/> is an exact leaderboard filter rather than
        /// only selecting a stable ruleset variant such as Relax.
        /// </summary>
        internal bool FilterByMods { get; }

        /// <summary>
        /// Beatmap metadata returned by the stable leaderboard header.
        /// </summary>
        internal BeatmapOnlineStatus? LegacyBeatmapStatus { get; set; }
        internal int? LegacyBeatmapId { get; set; }
        internal int? LegacyBeatmapSetId { get; set; }

        public GetScoresRequest(IBeatmapInfo beatmapInfo, IRulesetInfo ruleset, BeatmapLeaderboardScope scope = BeatmapLeaderboardScope.Global, IEnumerable<IMod>? mods = null,
                                bool allowMissingOnlineId = false, bool? filterByMods = null)
        {
            if (beatmapInfo.OnlineID <= 0 && !allowMissingOnlineId)
                throw new InvalidOperationException($"Cannot lookup a beatmap's scores without having a populated {nameof(IBeatmapInfo.OnlineID)}.");

            if (scope == BeatmapLeaderboardScope.Local)
                throw new InvalidOperationException("Should not attempt to request online scores for a local scoped leaderboard");

            BeatmapInfo = beatmapInfo;
            Scope = scope;
            Ruleset = ruleset ?? throw new ArgumentNullException(nameof(ruleset));
            Mods = (mods ?? Array.Empty<IMod>()).ToArray();
            FilterByMods = filterByMods ?? mods != null;

            ScoresRequested = Scope.RequiresSupporter(FilterByMods) ? MAX_SCORES_PER_REQUEST : DEFAULT_SCORES_PER_REQUEST;
        }

        protected override string Target => $@"beatmaps/{BeatmapInfo.OnlineID}/scores";

        protected override WebRequest CreateWebRequest()
        {
            var req = base.CreateWebRequest();

            req.AddParameter(@"type", Scope.ToString().ToLowerInvariant());
            req.AddParameter(@"mode", Ruleset.ShortName);

            foreach (var mod in Mods)
                req.AddParameter(@"mods[]", mod.Acronym);

            req.AddParameter(@"limit", ScoresRequested.ToString(CultureInfo.InvariantCulture));
            return req;
        }

        public bool Equals(GetScoresRequest? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;

            return BeatmapInfo.Equals(other.BeatmapInfo)
                   && Scope == other.Scope
                   && Ruleset.Equals(other.Ruleset)
                   && FilterByMods == other.FilterByMods
                   && Mods.SequenceEqual(other.Mods);
        }
    }
}
