// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects.Legacy;
using osu.Game.Rulesets.Osu.Judgements;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Scoring.Legacy;

namespace osu.Game.Rulesets.Osu.Difficulty
{
    internal sealed class OsuLegacyScoreProcessor : ILegacyScoreProcessor
    {
        private readonly Stack<State> history = new();
        private readonly Dictionary<SpinnerTick, Spinner> spinnerByTick = new();
        private readonly Dictionary<Spinner, int> spinnerScores = new();
        private readonly Dictionary<HitResult, int> statistics = new();
        private readonly Dictionary<HitResult, int> maximumStatistics = new();
        private readonly double overallDifficulty;
        private readonly double scoreMultiplier;

        public long TotalScore { get; private set; }

        public int Combo { get; private set; }

        public int MaxCombo { get; private set; }

        public IReadOnlyDictionary<HitResult, int>? Statistics => statistics;

        public IReadOnlyDictionary<HitResult, int>? MaximumStatistics => maximumStatistics;

        public int? CountGeki => null;

        public int? CountKatu => null;

        public OsuLegacyScoreProcessor(IWorkingBeatmap workingBeatmap, IBeatmap playableBeatmap, IReadOnlyList<Mod> mods)
        {
            IBeatmap beatmap = workingBeatmap.Beatmap;
            overallDifficulty = playableBeatmap.Difficulty.OverallDifficulty;
            int drainLength = getDrainLength(beatmap);
            int difficultyMultiplier = LegacyRulesetExtensions.CalculateDifficultyPeppyStars(beatmap.Difficulty, beatmap.HitObjects.Count, drainLength);
            double modMultiplier = new OsuLegacyScoreSimulator().GetLegacyScoreMultiplier(mods, LegacyBeatmapConversionDifficultyInfo.FromBeatmap(beatmap));
            scoreMultiplier = difficultyMultiplier * modMultiplier;
            maximumStatistics[HitResult.Great] = playableBeatmap.HitObjects.Count;

            foreach (Spinner spinner in playableBeatmap.HitObjects.OfType<Spinner>())
            {
                foreach (SpinnerTick tick in spinner.NestedHitObjects.OfType<SpinnerTick>())
                    spinnerByTick[tick] = spinner;
            }
        }

        public void ApplyResult(JudgementResult result)
        {
            Spinner? affectedSpinner = result.HitObject switch
            {
                Spinner spinner => spinner,
                SpinnerTick tick when spinnerByTick.TryGetValue(tick, out Spinner? foundSpinner) => foundSpinner,
                _ => null,
            };
            int previousSpinnerScore = 0;
            bool hadSpinnerScore = affectedSpinner != null && spinnerScores.TryGetValue(affectedSpinner, out previousSpinnerScore);
            history.Push(new State(TotalScore, Combo, MaxCombo, affectedSpinner, previousSpinnerScore, hadSpinnerScore,
                new Dictionary<HitResult, int>(statistics)));

            switch (result.HitObject)
            {
                case SpinnerBonusTick:
                    applySpinnerTick(result, 1100);
                    return;

                case SpinnerTick:
                    applySpinnerTick(result, 100);
                    return;

                case SliderTick:
                    applyNested(result, 10, true);
                    return;

                case SliderHeadCircle:
                case SliderRepeat:
                    applyNested(result, 30, true);
                    return;

                case SliderTailCircle:
                    // stable leaves combo untouched when the final slider end is
                    // missed; lazer Classic currently increments it regardless.
                    applyNested(result, 30, false);
                    return;

                case Slider:
                    addComboMultipliedScore(getBaseScore(result.Type));
                    incrementStatistic(result.Type);
                    // Stable slider combo is controlled by its nested objects.
                    return;

                case HitCircle:
                    addComboMultipliedScore(getBaseScore(result.Type));
                    updateCombo(result.Type);
                    incrementStatistic(result.Type);
                    return;

                case Spinner spinner:
                    applySpinnerResult(spinner, result);
                    return;
            }
        }

        public void RevertResult(JudgementResult result)
        {
            if (!history.TryPop(out State state))
                return;

            TotalScore = state.Score;
            Combo = state.Combo;
            MaxCombo = state.MaxCombo;
            statistics.Clear();
            foreach ((HitResult resultType, int count) in state.Statistics)
                statistics[resultType] = count;

            if (state.Spinner != null)
            {
                if (state.HadSpinnerScore)
                    spinnerScores[state.Spinner] = state.SpinnerScore;
                else
                    spinnerScores.Remove(state.Spinner);
            }
        }

        private void applySpinnerTick(JudgementResult result, int value)
        {
            if (!result.Type.IsHit())
                return;

            TotalScore += value;
            if (result.HitObject is SpinnerTick tick && spinnerByTick.TryGetValue(tick, out Spinner? spinner))
                spinnerScores[spinner] = spinnerScores.GetValueOrDefault(spinner) + value;
        }

        private void applySpinnerResult(Spinner spinner, JudgementResult result)
        {
            int halfSpins = result is OsuSpinnerJudgementResult spinnerResult
                ? Math.Max(0, (int)(spinnerResult.TotalRotation / 180))
                : 0;
            int requiredHalfSpins = (int)(spinner.Duration / 1000 * IBeatmapDifficultyInfo.DifficultyRange(
                overallDifficulty, new DifficultyRange(3, 5, 7.5)));

            int stableSpinnerScore = 0;
            for (int halfSpin = 1; halfSpin <= halfSpins; halfSpin++)
            {
                if (halfSpin > requiredHalfSpins + 3 && (halfSpin - (requiredHalfSpins + 3)) % 2 == 0)
                    stableSpinnerScore += 1100;
                else if (halfSpin > 1 && halfSpin % 2 == 0)
                    stableSpinnerScore += 100;
            }

            TotalScore += stableSpinnerScore - spinnerScores.GetValueOrDefault(spinner);
            spinnerScores[spinner] = stableSpinnerScore;

            HitResult stableResult = halfSpins <= requiredHalfSpins - 1
                ? HitResult.Miss
                : halfSpins > requiredHalfSpins + 1
                    ? HitResult.Great
                    : halfSpins > requiredHalfSpins
                        ? HitResult.Ok
                        : HitResult.Meh;

            addComboMultipliedScore(getBaseScore(stableResult));
            updateCombo(stableResult);
            incrementStatistic(stableResult);
        }

        private void incrementStatistic(HitResult result)
        {
            if (result.IsBasic())
                statistics[result] = statistics.GetValueOrDefault(result) + 1;
        }

        private void applyNested(JudgementResult result, int value, bool breakOnMiss)
        {
            if (result.Type.IsHit())
                TotalScore += value;

            updateCombo(result.Type, breakOnMiss);
        }

        private void addComboMultipliedScore(int value)
        {
            if (value <= 0)
                return;

            TotalScore += value;
            // Integer division and truncation intentionally match osu!stable.
            TotalScore += (int)(Math.Max(0, Combo - 1) * (value / 25 * scoreMultiplier));
        }

        private void updateCombo(HitResult result, bool breakOnMiss = true)
        {
            if (result.IsHit())
            {
                Combo++;
                MaxCombo = Math.Max(MaxCombo, Combo);
            }
            else if (breakOnMiss && result.IsMiss())
                Combo = 0;
        }

        private static int getBaseScore(HitResult result) => result switch
        {
            HitResult.Perfect or HitResult.Great => 300,
            HitResult.Ok => 100,
            HitResult.Meh => 50,
            _ => 0,
        };

        private static int getDrainLength(IBeatmap beatmap)
        {
            if (beatmap.HitObjects.Count == 0)
                return 0;

            int breakLength = beatmap.Breaks.Select(b => (int)Math.Round(b.EndTime) - (int)Math.Round(b.StartTime)).Sum();
            return ((int)Math.Round(beatmap.HitObjects[^1].StartTime) - (int)Math.Round(beatmap.HitObjects[0].StartTime) - breakLength) / 1000;
        }

        private readonly record struct State(
            long Score,
            int Combo,
            int MaxCombo,
            Spinner? Spinner,
            int SpinnerScore,
            bool HadSpinnerScore,
            Dictionary<HitResult, int> Statistics);
    }
}
