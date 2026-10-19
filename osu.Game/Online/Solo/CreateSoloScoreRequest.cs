// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Globalization;
using System.Net.Http;
using osu.Framework.IO.Network;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets.Mods;

namespace osu.Game.Online.Solo
{
    public class CreateSoloScoreRequest : APIRequest<APIScoreToken>
    {
        public BeatmapInfo BeatmapInfo { get; }
        public int BeatmapId { get; }
        public int RulesetId { get; }
        public Mod[] Mods { get; }
        private readonly string versionHash;

        public CreateSoloScoreRequest(BeatmapInfo beatmapInfo, int rulesetId, string versionHash, Mod[]? mods = null, int? beatmapId = null)
        {
            BeatmapInfo = beatmapInfo;
            BeatmapId = beatmapId ?? beatmapInfo.OnlineID;
            RulesetId = rulesetId;
            Mods = mods ?? [];
            this.versionHash = versionHash;
        }

        protected override WebRequest CreateWebRequest()
        {
            var req = base.CreateWebRequest();
            req.Method = HttpMethod.Post;
            req.AddParameter("version_hash", versionHash);
            req.AddParameter("beatmap_hash", BeatmapInfo.MD5Hash);
            req.AddParameter("ruleset_id", RulesetId.ToString(CultureInfo.InvariantCulture));
            return req;
        }

        protected override string Target => $@"beatmaps/{BeatmapId}/solo/scores";
    }
}
