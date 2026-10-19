// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Beatmaps;
using osu.Game.Online.Rooms;
using osu.Game.Scoring;

namespace osu.Game.Online.Solo
{
    public class SubmitSoloScoreRequest : SubmitScoreRequest
    {
        public int BeatmapId { get; }
        public Score ReplayScore { get; }
        public IBeatmap Beatmap { get; }

        public SubmitSoloScoreRequest(Score score, IBeatmap beatmap, long scoreId, int beatmapId)
            : base(score.ScoreInfo, scoreId)
        {
            ReplayScore = score;
            Beatmap = beatmap;
            BeatmapId = beatmapId;
        }

        protected override string Target => $@"beatmaps/{BeatmapId}/solo/scores/{ScoreId}";
    }
}
