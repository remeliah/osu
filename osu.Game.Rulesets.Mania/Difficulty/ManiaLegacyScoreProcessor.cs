// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mania.Objects;
using osu.Game.Rulesets.Mania.Scoring;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Scoring.Legacy;

namespace osu.Game.Rulesets.Mania.Difficulty
{
    internal sealed class ManiaLegacyScoreProcessor : ILegacyScoreProcessor
    {
        private const int combo_bonus = 384;
        private const double combo_multi = 100;
        private const double score_300g = 320;

        private readonly Stack<State> history = new();
        private readonly Dictionary<HitObject, HoldNote> nestedHoldNotes = new();
        private readonly Dictionary<HoldNote, HoldState> holdStates = new();
        private readonly Dictionary<HitResult, int> statistics = new();
        private readonly Dictionary<HitResult, int> maximumStatistics = new();
        private readonly int maximumJudgements;
        private readonly double modMultiplier;
        private readonly double comboPenaltyMultiplier;
        private readonly double scoreMultiplier;

        private double totalScoreDouble;
        private double comboAddition = combo_multi;
        private int combo;
        private int processedJudgements;

        public long TotalScore { get; private set; }

        public int Combo => combo;

        public int MaxCombo { get; private set; }

        public IReadOnlyDictionary<HitResult, int>? Statistics => statistics;

        public IReadOnlyDictionary<HitResult, int>? MaximumStatistics => maximumStatistics;

        public int? CountGeki => null;

        public int? CountKatu => null;

        public ManiaLegacyScoreProcessor(IWorkingBeatmap workingBeatmap, IBeatmap playableBeatmap, IReadOnlyList<Mod> mods)
        {
            // Stable counts every top-level note exactly once. A lazer hold note
            // exposes separate head, body and tail results, none of which may be
            // included in this denominator individually.
            maximumJudgements = playableBeatmap.HitObjects.Count;
            maximumStatistics[HitResult.Perfect] = maximumJudgements;
            modMultiplier = new ManiaLegacyScoreSimulator().GetLegacyScoreMultiplier(mods, LegacyBeatmapConversionDifficultyInfo.FromBeatmap(workingBeatmap.Beatmap));
            comboPenaltyMultiplier = 1;
            if (mods.Any(m => m is ManiaModHidden)) comboPenaltyMultiplier *= 1.06;
            if (mods.Any(m => m is ManiaModHardRock)) comboPenaltyMultiplier *= 1.08;
            if (mods.Any(m => m is ManiaModDoubleTime or ManiaModNightcore)) comboPenaltyMultiplier *= 1.1;
            if (mods.Any(m => m is ManiaModFlashlight)) comboPenaltyMultiplier *= 1.06;
            scoreMultiplier = maximumJudgements > 0 ? 500000d / maximumJudgements * modMultiplier : 0;

            foreach (HoldNote hold in playableBeatmap.HitObjects.OfType<HoldNote>())
            {
                if (hold.Head != null)
                    nestedHoldNotes[hold.Head] = hold;
                if (hold.Body != null)
                    nestedHoldNotes[hold.Body] = hold;
                if (hold.Tail != null)
                    nestedHoldNotes[hold.Tail] = hold;
            }
        }

        public void ApplyResult(JudgementResult result)
        {
            history.Push(new State(TotalScore, totalScoreDouble, comboAddition, combo, MaxCombo, processedJudgements,
                new Dictionary<HoldNote, HoldState>(holdStates), new Dictionary<HitResult, int>(statistics)));

            processHoldTicksUntil(result.TimeAbsolute);

            if (nestedHoldNotes.TryGetValue(result.HitObject, out HoldNote? hold))
            {
                applyHoldResult(hold, result);
                return;
            }

            if (!result.Type.IsBasic())
                return;

            applyBasicResult(result.Type);
        }

        private void applyHoldResult(HoldNote hold, JudgementResult result)
        {
            holdStates.TryGetValue(hold, out HoldState state);

            switch (result.HitObject)
            {
                case HeadNote:
                    bool headHit = result.Type.IsHit();
                    holdStates[hold] = state with
                    {
                        HeadOffset = result.TimeOffset,
                        HeadHit = headHit,
                        Active = headHit,
                        NextComboTime = result.TimeAbsolute + 100
                    };
                    return;

                case HoldNoteBody when result.Type == HitResult.ComboBreak:
                    state = state with { HasHoldBreak = true, Active = false };
                    holdStates[hold] = state;
                    combo = 0;
                    return;

                case TailNote:
                    holdStates[hold] = state with { Active = false };
                    applyBasicResult(getStableHoldResult(hold, state, result));
                    return;
            }
        }

        private static HitResult getStableHoldResult(HoldNote hold, HoldState state, JudgementResult tailResult)
        {
            if (!state.HeadHit || hold.Head?.HitWindows is not ManiaHitWindows windows)
                return HitResult.Miss;

            double startDifference = Math.Abs(state.HeadOffset);
            double endDifference = Math.Abs(tailResult.TimeOffset);
            double totalDifference = startDifference + endDifference;

            // Stable mirrors late releases around the tail and considers both an
            // early and late release outside HitWindow50 a miss.
            if (endDifference > windows.WindowFor(HitResult.Meh))
                return HitResult.Miss;

            HitResult result;

            if (startDifference <= windows.WindowFor(HitResult.Perfect) * 1.2
                && totalDifference <= windows.WindowFor(HitResult.Perfect) * 2.4)
                result = HitResult.Perfect;
            else if (startDifference <= windows.WindowFor(HitResult.Great) * 1.1
                     && totalDifference <= windows.WindowFor(HitResult.Great) * 2.2)
                result = HitResult.Great;
            else if (startDifference <= windows.WindowFor(HitResult.Good)
                     && totalDifference <= windows.WindowFor(HitResult.Good) * 2)
                result = HitResult.Good;
            else if (startDifference <= windows.WindowFor(HitResult.Ok)
                     && totalDifference <= windows.WindowFor(HitResult.Ok) * 2)
                result = HitResult.Ok;
            else
                result = HitResult.Meh;

            // A stable LN which was dropped can recover, but any 300 judgement
            // is capped to 200.
            if (state.HasHoldBreak && result > HitResult.Good)
                result = HitResult.Good;

            return result;
        }

        private void processHoldTicksUntil(double time)
        {
            while (true)
            {
                HoldNote? nextHold = null;
                double nextTime = double.PositiveInfinity;

                foreach ((HoldNote hold, HoldState state) in holdStates)
                {
                    if (!state.Active || state.NextComboTime > hold.EndTime || state.NextComboTime > time || state.NextComboTime >= nextTime)
                        continue;

                    nextHold = hold;
                    nextTime = state.NextComboTime;
                }

                if (nextHold == null)
                    return;

                HoldState current = holdStates[nextHold];
                holdStates[nextHold] = current with { NextComboTime = current.NextComboTime + 100 };
                combo++;
                MaxCombo = Math.Max(MaxCombo, combo);
            }
        }

        private void applyBasicResult(HitResult resultType)
        {
            int baseScore;
            int comboDelta;
            switch (resultType)
            {
                case HitResult.Perfect:
                    baseScore = 320;
                    comboAddition += 2;
                    comboDelta = 32;
                    break;

                case HitResult.Great:
                    baseScore = 300;
                    comboAddition += 1;
                    comboDelta = 32;
                    break;

                case HitResult.Good:
                    baseScore = 200;
                    comboAddition -= 8 / comboPenaltyMultiplier;
                    comboDelta = 16;
                    break;

                case HitResult.Ok:
                    baseScore = 100;
                    comboAddition -= 24 / comboPenaltyMultiplier;
                    comboDelta = 8;
                    break;

                case HitResult.Meh:
                    baseScore = 50;
                    comboAddition -= 44 / comboPenaltyMultiplier;
                    comboDelta = 4;
                    break;

                default:
                    baseScore = 0;
                    comboAddition = -56 / comboPenaltyMultiplier;
                    comboDelta = 0;
                    break;
            }

            if (combo != 0 && combo % combo_bonus == 0)
                comboAddition = combo_multi;

            comboAddition = Math.Clamp(comboAddition, 0, combo_multi);
            double increase = Math.Sqrt(comboAddition) * comboDelta * scoreMultiplier / score_300g;
            increase += baseScore * scoreMultiplier / score_300g;

            // Stable truncates each live increase while retaining a double
            // accumulator, then rounds once the completed play is finalised.
            TotalScore = (long)totalScoreDouble + (long)increase;
            totalScoreDouble += increase;
            processedJudgements++;
            statistics[resultType] = statistics.GetValueOrDefault(resultType) + 1;

            if (baseScore > 0)
            {
                combo++;
                MaxCombo = Math.Max(MaxCombo, combo);
            }
            else
                combo = 0;

            if (maximumJudgements > 0 && processedJudgements >= maximumJudgements)
            {
                TotalScore = (long)Math.Round(totalScoreDouble);
                if (TotalScore == 999999)
                    TotalScore = 1000000;
            }
        }

        public void RevertResult(JudgementResult result)
        {
            if (!history.TryPop(out State state))
                return;

            TotalScore = state.TotalScore;
            totalScoreDouble = state.TotalScoreDouble;
            comboAddition = state.ComboAddition;
            combo = state.Combo;
            MaxCombo = state.MaxCombo;
            processedJudgements = state.ProcessedJudgements;
            holdStates.Clear();
            foreach ((HoldNote hold, HoldState holdState) in state.HoldStates)
                holdStates[hold] = holdState;
            statistics.Clear();
            foreach ((HitResult resultType, int count) in state.Statistics)
                statistics[resultType] = count;
        }

        private readonly record struct HoldState(double HeadOffset, bool HeadHit, bool HasHoldBreak, bool Active, double NextComboTime);

        private readonly record struct State(
            long TotalScore,
            double TotalScoreDouble,
            double ComboAddition,
            int Combo,
            int MaxCombo,
            int ProcessedJudgements,
            Dictionary<HoldNote, HoldState> HoldStates,
            Dictionary<HitResult, int> Statistics);
    }
}
