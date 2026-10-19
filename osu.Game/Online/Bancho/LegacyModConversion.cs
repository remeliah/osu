// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;

namespace osu.Game.Online.Bancho
{
    internal static class LegacyModConversion
    {
        public static bool TryConvertForSubmission(Ruleset ruleset, IReadOnlyList<Mod> mods, out LegacyMods legacyMods, out string error)
        {
            legacyMods = LegacyMods.None;

            ModClassic? classic = mods.OfType<ModClassic>().SingleOrDefault();
            if (classic == null)
            {
                error = "Classic mod is required for stable score submission.";
                return false;
            }

            if (!classic.UsesDefaultConfiguration)
            {
                error = "Classic uses custom settings which cannot be represented by an osu!stable score.";
                return false;
            }

            foreach (Mod mod in mods.Where(mod => mod is not ModClassic and not ModNoMod))
            {
                if (!mod.UsesDefaultConfiguration)
                {
                    error = $"{mod.Acronym} uses settings which cannot be represented by the stable mod bitmask.";
                    return false;
                }

                LegacyMods single = ruleset.ConvertToLegacyMods([mod]);
                if (single == LegacyMods.None)
                {
                    error = $"{mod.Acronym} does not exist in osu!stable and this score will remain unranked.";
                    return false;
                }

                legacyMods |= single;
            }

            if (legacyMods.HasFlag(LegacyMods.ScoreV2))
            {
                error = "ScoreV2 is not a classic score and is not submitted to the stable leaderboard.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        public static LegacyMods ConvertLeaderboardFilter(Ruleset ruleset, IEnumerable<IMod> mods)
        {
            LegacyMods result = LegacyMods.None;
            foreach (IMod candidate in mods)
            {
                if (candidate is ModClassic or ModNoMod)
                    continue;

                // Lazer-only mods do not have stable bitmask equivalents. They
                // make the local play unranked, but must not prevent retrieval of
                // the underlying stable/RX leaderboard.
                if (candidate is not Mod mod || !mod.UsesDefaultConfiguration)
                    continue;

                LegacyMods single = ruleset.ConvertToLegacyMods([mod]);
                if (single == LegacyMods.None || single.HasFlag(LegacyMods.ScoreV2))
                    continue;

                result |= single;
            }
            return result;
        }

        /// <summary>
        /// Returns the multiplier displayed by osu!stable for a set of mods.
        /// Relax and Autopilot make a stable score unranked and are displayed as
        /// 0.00x, independently of lazer's gameplay score multiplier.
        /// </summary>
        public static double GetStableDisplayMultiplier(Ruleset ruleset, IReadOnlyList<Mod> mods, double lazerMultiplier)
        {
            LegacyMods legacyMods = ConvertLeaderboardFilter(ruleset, mods);
            return legacyMods.HasFlag(LegacyMods.Relax) || legacyMods.HasFlag(LegacyMods.Autopilot)
                ? 0
                : lazerMultiplier;
        }
    }
}
