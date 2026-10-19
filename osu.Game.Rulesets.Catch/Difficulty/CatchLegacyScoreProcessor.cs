// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Catch.Objects;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects.Legacy;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Scoring.Legacy;

namespace osu.Game.Rulesets.Catch.Difficulty
{
    internal sealed class CatchLegacyScoreProcessor : ILegacyScoreProcessor
    {
        private readonly Stack<(long Score, int Combo, int MaxCombo)> history = new();
        private readonly double scoreMultiplier;

        public long TotalScore { get; private set; }

        public int Combo { get; private set; }

        public int MaxCombo { get; private set; }

        public IReadOnlyDictionary<HitResult, int>? Statistics => null;

        public IReadOnlyDictionary<HitResult, int>? MaximumStatistics => null;

        public int? CountGeki => null;

        public int? CountKatu => null;

        public CatchLegacyScoreProcessor(IWorkingBeatmap workingBeatmap, IReadOnlyList<Mod> mods)
        {
            IBeatmap beatmap = workingBeatmap.Beatmap;
            int difficultyMultiplier = LegacyRulesetExtensions.CalculateDifficultyPeppyStars(beatmap.Difficulty, beatmap.HitObjects.Count, getDrainLength(beatmap));
            double modMultiplier = new CatchLegacyScoreSimulator().GetLegacyScoreMultiplier(mods, LegacyBeatmapConversionDifficultyInfo.FromBeatmap(beatmap));
            scoreMultiplier = difficultyMultiplier * modMultiplier;
        }

        public void ApplyResult(JudgementResult result)
        {
            history.Push((TotalScore, Combo, MaxCombo));

            switch (result.HitObject)
            {
                case TinyDroplet:
                    if (result.Type.IsHit()) TotalScore += 10;
                    return;

                case Droplet:
                    if (result.Type.IsHit())
                    {
                        TotalScore += 100;
                        increaseCombo();
                    }
                    else if (result.Type.IsMiss())
                        Combo = 0;
                    return;

                case Fruit:
                    if (result.Type.IsHit())
                    {
                        const int value = 300;
                        TotalScore += value;
                        TotalScore += (int)(Math.Max(0, Combo - 1) * (value / 25 * scoreMultiplier));
                        increaseCombo();
                    }
                    else if (result.Type.IsMiss())
                        Combo = 0;
                    return;

                case Banana:
                    if (result.Type.IsHit()) TotalScore += 1100;
                    return;
            }
        }

        public void RevertResult(JudgementResult result)
        {
            if (history.TryPop(out var state))
                (TotalScore, Combo, MaxCombo) = state;
        }

        private void increaseCombo()
        {
            Combo++;
            MaxCombo = Math.Max(MaxCombo, Combo);
        }

        private static int getDrainLength(IBeatmap beatmap)
        {
            if (beatmap.HitObjects.Count == 0)
                return 0;

            int breakLength = beatmap.Breaks.Select(b => (int)Math.Round(b.EndTime) - (int)Math.Round(b.StartTime)).Sum();
            return ((int)Math.Round(beatmap.HitObjects[^1].StartTime) - (int)Math.Round(beatmap.HitObjects[0].StartTime) - breakLength) / 1000;
        }
    }
}
