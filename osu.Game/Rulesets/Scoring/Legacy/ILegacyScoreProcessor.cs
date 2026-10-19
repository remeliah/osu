// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Scoring;

namespace osu.Game.Rulesets.Scoring.Legacy
{
    /// <summary>
    /// Tracks the original osu!stable ScoreV1 total directly from gameplay
    /// judgements. This must not derive its value from standardised lazer score.
    /// </summary>
    public interface ILegacyScoreProcessor
    {
        long TotalScore { get; }

        int Combo { get; }

        int MaxCombo { get; }

        /// <summary>
        /// Stable-compatible result counts, or <see langword="null"/> when the
        /// lazer ruleset already produces identical counts.
        /// </summary>
        IReadOnlyDictionary<HitResult, int>? Statistics { get; }

        /// <summary>
        /// Maximum stable-compatible result counts corresponding to
        /// <see cref="Statistics"/>.
        /// </summary>
        IReadOnlyDictionary<HitResult, int>? MaximumStatistics { get; }

        /// <summary>
        /// Stable's mode-specific bonus counters. These cannot always be
        /// reconstructed from lazer hit results (notably for taiko strong
        /// 300s, strong 100s and strong drum-roll ticks).
        /// </summary>
        int? CountGeki { get; }

        int? CountKatu { get; }

        void ApplyResult(JudgementResult result);

        void RevertResult(JudgementResult result);
    }
}
