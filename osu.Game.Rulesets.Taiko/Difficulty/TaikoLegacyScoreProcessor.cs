// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Legacy;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Scoring.Legacy;
using osu.Game.Rulesets.Taiko.Objects;

namespace osu.Game.Rulesets.Taiko.Difficulty
{
    internal sealed class TaikoLegacyScoreProcessor : ILegacyScoreProcessor
    {
        private readonly Stack<(long Score, int Combo, int MaxCombo, int PendingStrong, HitResult PendingStrongResult, int Geki, int Katu)> history = new();
        private readonly IBeatmap playableBeatmap;
        private readonly int difficultyMultiplier;
        private readonly double modMultiplier;
        private int pendingStrongScore;
        private HitResult pendingStrongResult;

        public long TotalScore { get; private set; }

        public int Combo { get; private set; }

        public int MaxCombo { get; private set; }

        public IReadOnlyDictionary<HitResult, int>? Statistics => null;

        public IReadOnlyDictionary<HitResult, int>? MaximumStatistics => null;

        public int? CountGeki { get; private set; }

        public int? CountKatu { get; private set; }

        public TaikoLegacyScoreProcessor(IWorkingBeatmap workingBeatmap, IBeatmap playableBeatmap, IReadOnlyList<Mod> mods)
        {
            this.playableBeatmap = playableBeatmap;
            CountGeki = 0;
            CountKatu = 0;
            IBeatmap beatmap = workingBeatmap.Beatmap;
            var difficulty = beatmap.Difficulty.Clone();
            difficulty.CircleSize = 2;
            difficultyMultiplier = LegacyRulesetExtensions.CalculateDifficultyPeppyStars(difficulty, beatmap.HitObjects.Count, getDrainLength(beatmap));
            modMultiplier = new TaikoLegacyScoreSimulator().GetLegacyScoreMultiplier(mods, LegacyBeatmapConversionDifficultyInfo.FromBeatmap(beatmap));

            // Stable's denden completes only after the rotation requirement has
            // gone below zero, so it needs one more alternating hit than lazer's
            // swell. Stable also scales that requirement for DT/NC and HT/DC.
            double swellRequirementMultiplier = mods.Any(m => m is ModDoubleTime or ModNightcore)
                ? 0.75
                : mods.Any(m => m is ModHalfTime or ModDaycore)
                    ? 1.5
                    : 1;

            foreach (Swell swell in playableBeatmap.HitObjects.OfType<Swell>())
            {
                int stableRequiredHits = Math.Max(1, (int)(swell.RequiredHits * swellRequirementMultiplier)) + 1;
                if (swell.RequiredHits == stableRequiredHits)
                    continue;

                swell.RequiredHits = stableRequiredHits;
                // RequiredHits controls nested tick creation. This processor is
                // constructed before the drawable ruleset is loaded, so rebuild
                // those ticks now for the actual stable-compatible gameplay.
                swell.ApplyDefaults(playableBeatmap.ControlPointInfo, playableBeatmap.Difficulty);
            }
        }

        public void ApplyResult(JudgementResult result)
        {
            history.Push((TotalScore, Combo, MaxCombo, pendingStrongScore, pendingStrongResult, CountGeki.GetValueOrDefault(), CountKatu.GetValueOrDefault()));

            switch (result.HitObject)
            {
                case StrongNestedHitObject strong:
                    if (result.Type.IsHit())
                    {
                        TotalScore += pendingStrongScore;

                        if (strong.Parent is Hit)
                        {
                            if (pendingStrongResult == HitResult.Great)
                                CountGeki++;
                            else if (pendingStrongResult == HitResult.Ok)
                                CountKatu++;
                        }
                    }
                    pendingStrongScore = 0;
                    pendingStrongResult = HitResult.None;
                    return;

                case DrumRollTick tick:
                    if (!result.Type.IsHit())
                        return;

                    int tickScore = applyKiai(300, tick.Parent.StartTime);
                    TotalScore += tickScore;
                    // Stable scores a large drum-roll hit as twice 120% of the
                    // regular value. The main lazer result accounts for one
                    // regular value, so defer the remainder until its strong
                    // nested result is applied.
                    pendingStrongScore = tick.IsStrong ? tickScore + 2 * (tickScore / 5) : 0;
                    pendingStrongResult = HitResult.None;
                    return;

                case SwellTick:
                    if (result.Type.IsHit()) TotalScore += 300;
                    return;

                case Swell swell:
                    if (result.Type.IsHit())
                    {
                        int swellScore = getComboScore(300);
                        // Stable completes the denden on the final input, which
                        // may be earlier than the authored spinner end.
                        swellScore = applyKiai(swellScore, result.TimeAbsolute);
                        TotalScore += swellScore * 2;
                    }
                    return;

                case Hit hit:
                    int baseScore = result.Type switch
                    {
                        HitResult.Perfect or HitResult.Great => 300,
                        HitResult.Ok => 150,
                        _ => 0,
                    };

                    if (baseScore > 0)
                    {
                        int score = applyKiai(getComboScore(baseScore), hit.StartTime);
                        TotalScore += score;
                        pendingStrongScore = hit.IsStrong ? score : 0;
                        pendingStrongResult = hit.IsStrong ? result.Type : HitResult.None;
                        Combo++;
                        MaxCombo = Math.Max(MaxCombo, Combo);
                    }
                    else
                    {
                        pendingStrongScore = 0;
                        pendingStrongResult = HitResult.None;
                        if (result.Type.IsMiss()) Combo = 0;
                    }
                    return;
            }
        }

        public void RevertResult(JudgementResult result)
        {
            if (history.TryPop(out var state))
                (TotalScore, Combo, MaxCombo, pendingStrongScore, pendingStrongResult, CountGeki, CountKatu) = state;
        }

        private int getComboScore(int baseScore)
        {
            // Preserve stable's integer operations in their original order.
            int bonus = (int)((baseScore / 35 * 2 * (difficultyMultiplier + 1)) * modMultiplier) * (Math.Min(100, Combo) / 10);
            return baseScore + bonus;
        }

        private int applyKiai(int score, double time) => playableBeatmap.ControlPointInfo.EffectPointAt(time).KiaiMode ? (int)(score * 1.2f) : score;

        private static int getDrainLength(IBeatmap beatmap)
        {
            if (beatmap.HitObjects.Count == 0)
                return 0;

            int breakLength = beatmap.Breaks.Select(b => (int)Math.Round(b.EndTime) - (int)Math.Round(b.StartTime)).Sum();
            return ((int)Math.Round(beatmap.HitObjects[^1].StartTime) - (int)Math.Round(beatmap.HitObjects[0].StartTime) - breakLength) / 1000;
        }
    }
}
