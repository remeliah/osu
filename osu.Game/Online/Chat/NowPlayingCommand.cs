// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Users;

namespace osu.Game.Online.Chat
{
    public partial class NowPlayingCommand : Component
    {
        [Resolved]
        private IChannelPostTarget channelManager { get; set; } = null!;

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        [Resolved]
        private Bindable<WorkingBeatmap> currentBeatmap { get; set; } = null!;

        [Resolved]
        private Bindable<IReadOnlyList<Mod>> selectedMods { get; set; } = null!;

        [Resolved]
        private IBindable<RulesetInfo> currentRuleset { get; set; } = null!;

        private readonly Channel? target;
        private IBindable<UserActivity?> userActivity = null!;

        /// <summary>
        /// Creates a new <see cref="NowPlayingCommand"/> to post the currently-playing beatmap to a parenting <see cref="IChannelPostTarget"/>.
        /// </summary>
        /// <param name="target">The target channel to post to. If <c>null</c>, the currently-selected channel will be posted to.</param>
        public NowPlayingCommand(Channel target)
        {
            this.target = target;
        }

        [BackgroundDependencyLoader]
        private void load(SessionStatics session)
        {
            userActivity = session.GetBindable<UserActivity?>(Static.UserOnlineActivity);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            string verb;

            int beatmapOnlineID;
            int beatmapSetOnlineID;
            string beatmapDisplayTitle;
            (int sourceBeatmapOnlineID, int sourceBeatmapSetOnlineID) = currentBeatmap.Value.GetOnlineIds();

            switch (userActivity.Value)
            {
                case UserActivity.InGame game:
                    verb = "playing";
                    beatmapOnlineID = game.BeatmapID > 0 ? game.BeatmapID : sourceBeatmapOnlineID;
                    beatmapSetOnlineID = sourceBeatmapSetOnlineID;
                    beatmapDisplayTitle = game.BeatmapDisplayTitle;
                    break;

                case UserActivity.EditingBeatmap edit:
                    verb = "editing";
                    beatmapOnlineID = edit.BeatmapID > 0 ? edit.BeatmapID : sourceBeatmapOnlineID;
                    beatmapSetOnlineID = sourceBeatmapSetOnlineID;
                    beatmapDisplayTitle = edit.BeatmapDisplayTitle;
                    break;

                default:
                    verb = "listening to";
                    beatmapOnlineID = sourceBeatmapOnlineID;
                    beatmapSetOnlineID = sourceBeatmapSetOnlineID;
                    beatmapDisplayTitle = currentBeatmap.Value.BeatmapInfo.GetDisplayTitle();
                    break;
            }

            string[] pieces =
            {
                "is",
                verb,
                getBeatmapPart(),
                getRulesetPart(),
                getModPart(),
            };

            channelManager.PostMessage(string.Join(' ', pieces.Where(p => !string.IsNullOrEmpty(p))), true, target);
            Expire();

            string getBeatmapPart()
            {
                if (beatmapOnlineID <= 0)
                    return beatmapDisplayTitle;

                return FormatBanchoBeatmapLink(api.Endpoints.WebsiteUrl, beatmapSetOnlineID, beatmapOnlineID, beatmapDisplayTitle);
            }

            string getRulesetPart()
            {
                if (userActivity.Value is not UserActivity.InGame) return string.Empty;

                return currentRuleset.Value.OnlineID switch
                {
                    1 => "<Taiko>",
                    2 => "<CatchTheBeat>",
                    3 => "<osu!mania>",
                    _ => string.Empty,
                };
            }

            string getModPart()
            {
                if (userActivity.Value is not UserActivity.InGame) return string.Empty;

                if (selectedMods.Value.Count == 0)
                {
                    return string.Empty;
                }

                StringBuilder modsString = new StringBuilder();
                foreach (var mod in selectedMods.Value.Where(mod => mod is not ModClassic and not ModNoMod))
                {
                    string? formatted = mod.Acronym switch
                    {
                        "EZ" => "-Easy",
                        "NF" => "-NoFail",
                        "HD" => "+Hidden",
                        "PF" => "+Perfect",
                        "SD" => "+SuddenDeath",
                        "HR" => "+HardRock",
                        "NC" => "+Nightcore",
                        "DT" => "+DoubleTime",
                        "HT" => "-HalfTime",
                        "FL" => "+Flashlight",
                        "SO" => "-SpunOut",
                        "AT" => "|Autoplay|",
                        "CN" => "|Cinema|",
                        "RX" => "~Relax~",
                        "AP" => "~Autopilot~",
                        "TP" => "~Target~",
                        "1K" or "2K" or "3K" or "4K" or "5K" or "6K" or "7K" or "8K" or "9K" or "10K" or "12K" or "14K" or "16K" or "18K" => $"|{mod.Acronym}|",
                        _ => null,
                    };

                    if (formatted != null)
                        modsString.Append(formatted).Append(' ');
                }

                return modsString.ToString().TrimEnd();
            }
        }

        internal static string FormatBanchoBeatmapLink(string websiteUrl, int beatmapSetId, int beatmapId, string displayTitle)
        {
            var website = new System.Uri(websiteUrl);
            int syntacticSetId = beatmapSetId > 0 ? beatmapSetId : 0;
            string url = $"{website.GetLeftPart(System.UriPartial.Authority)}/beatmapsets/{syntacticSetId}#/{beatmapId}";
            return $"[{url} {displayTitle}]";
        }
    }
}
