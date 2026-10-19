// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Rulesets.Scoring;

namespace osu.Game.Scoring.Legacy
{
    /// <summary>
    /// Calculates the non-silver rank using osu!stable's per-mode rules.
    /// These intentionally use strict comparisons where stable does.
    /// </summary>
    internal static class LegacyScoreRankCalculator
    {
        public static ScoreRank Calculate(int mode, bool passed, int n300, int n100, int n50, int ngeki, int nkatu, int nmiss)
        {
            if (!passed)
                return ScoreRank.F;

            return mode switch
            {
                2 => calculateCatch(n300, n100, n50, nkatu, nmiss),
                3 => calculateMania(n300, n100, n50, ngeki, nkatu, nmiss),
                _ => calculateOsuOrTaiko(n300, n100, n50, nmiss),
            };
        }

        private static ScoreRank calculateOsuOrTaiko(int n300, int n100, int n50, int nmiss)
        {
            int total = n300 + n100 + n50 + nmiss;
            if (total == 0)
                return ScoreRank.D;

            float ratio300 = (float)n300 / total;
            float ratio50 = (float)n50 / total;

            if (ratio300 == 1)
                return ScoreRank.X;
            if (ratio300 > 0.9f && ratio50 <= 0.01f && nmiss == 0)
                return ScoreRank.S;
            if ((ratio300 > 0.8f && nmiss == 0) || ratio300 > 0.9f)
                return ScoreRank.A;
            if ((ratio300 > 0.7f && nmiss == 0) || ratio300 > 0.8f)
                return ScoreRank.B;
            if (ratio300 > 0.6f)
                return ScoreRank.C;
            return ScoreRank.D;
        }

        private static ScoreRank calculateCatch(int n300, int n100, int n50, int nkatu, int nmiss)
        {
            int total = n300 + n100 + n50 + nkatu + nmiss;
            float accuracy = total == 0 ? 1 : (float)(n300 + n100 + n50) / total;

            if (accuracy == 1)
                return ScoreRank.X;
            if (accuracy > 0.98f)
                return ScoreRank.S;
            if (accuracy > 0.94f)
                return ScoreRank.A;
            if (accuracy > 0.9f)
                return ScoreRank.B;
            if (accuracy > 0.85f)
                return ScoreRank.C;
            return ScoreRank.D;
        }

        private static ScoreRank calculateMania(int n300, int n100, int n50, int ngeki, int nkatu, int nmiss)
        {
            int total = n300 + n100 + n50 + ngeki + nkatu + nmiss;
            float accuracy = total == 0
                ? 1
                : (float)(n50 * 50 + n100 * 100 + nkatu * 200 + (n300 + ngeki) * 300) / (total * 300);

            if (accuracy == 1)
                return ScoreRank.X;
            if (accuracy > 0.95f)
                return ScoreRank.S;
            if (accuracy > 0.9f)
                return ScoreRank.A;
            if (accuracy > 0.8f)
                return ScoreRank.B;
            if (accuracy > 0.7f)
                return ScoreRank.C;
            return ScoreRank.D;
        }
    }
}
