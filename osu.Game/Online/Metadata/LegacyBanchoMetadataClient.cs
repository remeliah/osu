// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Users;

namespace osu.Game.Online.Metadata
{
    /// <summary>
    /// Supplies lazer dashboard presence directly from stable Bancho packets.
    /// No metadata SignalR connection is created in private-server mode.
    /// </summary>
    public partial class LegacyBanchoMetadataClient : MetadataClient
    {
        private readonly LegacyBanchoAPIAccess bancho;

        public override IBindable<bool> IsConnected => isConnected;
        private readonly BindableBool isConnected = new();

        public override UserPresence LocalUserPresence => localUserPresence;
        private UserPresence localUserPresence;

        public override IBindableDictionary<int, UserPresence> UserPresences => userPresences;
        private readonly BindableDictionary<int, UserPresence> userPresences = new();

        public override IBindableDictionary<int, UserPresence> FriendPresences => friendPresences;
        private readonly BindableDictionary<int, UserPresence> friendPresences = new();

        public override IBindable<DailyChallengeInfo?> DailyChallengeInfo => dailyChallengeInfo;
        private readonly Bindable<DailyChallengeInfo?> dailyChallengeInfo = new();

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        [Resolved]
        private IBindable<RulesetInfo> ruleset { get; set; } = null!;

        [Resolved]
        private IBindable<IReadOnlyList<Mod>> selectedMods { get; set; } = null!;

        [Resolved]
        private IBindable<WorkingBeatmap> beatmap { get; set; } = null!;

        private IBindable<APIState> apiState = null!;
        private IBindable<UserStatus> userStatus = null!;
        private IBindable<UserActivity?> userActivity = null!;

        public LegacyBanchoMetadataClient(LegacyBanchoAPIAccess bancho)
        {
            this.bancho = bancho;
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config, SessionStatics session)
        {
            userStatus = config.GetBindable<UserStatus>(OsuSetting.UserOnlineStatus);
            userActivity = session.GetBindable<UserActivity?>(Static.UserOnlineActivity);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            bancho.BanchoPresenceChanged += onPresenceChanged;
            apiState = api.State.GetBoundCopy();
            apiState.BindValueChanged(state => Schedule(() =>
            {
                isConnected.Value = state.NewValue == APIState.Online;
                if (isConnected.Value)
                {
                    loadSnapshot();
                    UpdateActivity(userActivity.Value).FireAndForget();
                    UpdateStatus(userStatus.Value).FireAndForget();
                }
                else
                    clearPresences();
            }), true);

            userStatus.BindValueChanged(status =>
            {
                if (api.IsLoggedIn)
                    UpdateStatus(status.NewValue).FireAndForget();
            });
            userActivity.BindValueChanged(activity =>
            {
                if (api.IsLoggedIn)
                    UpdateActivity(activity.NewValue).FireAndForget();
            });

            ruleset.BindValueChanged(_ => updateLegacySelection(), true);
            selectedMods.BindValueChanged(_ => updateLegacySelection(), true);
            beatmap.BindValueChanged(_ => updateLegacySelection(), true);
        }

        private void updateLegacySelection()
        {
            if (ruleset.Value == null || beatmap.Value == null)
                return;

            bancho.UpdateLegacySelection(ruleset.Value, selectedMods.Value, beatmap.Value);
        }

        private void loadSnapshot()
        {
            clearPresences();
            foreach ((int userId, UserPresence presence) in bancho.GetPresenceSnapshot())
                setPresence(userId, presence);
        }

        private void onPresenceChanged(int userId, UserPresence? presence) => Schedule(() =>
        {
            if (!isConnected.Value || userId <= 0)
                return;

            if (presence.HasValue)
                setPresence(userId, presence.Value);
            else
                removePresence(userId);
        });

        private void setPresence(int userId, UserPresence presence)
        {
            if (userId == api.LocalUser.Value.OnlineID)
                localUserPresence = presence;

            userPresences[userId] = presence;

            if (bancho.IsFriend(userId))
                friendPresences[userId] = presence;
            else
                friendPresences.Remove(userId);
        }

        private void removePresence(int userId)
        {
            userPresences.Remove(userId);
            friendPresences.Remove(userId);
            if (userId == api.LocalUser.Value.OnlineID)
                localUserPresence = default;
        }

        private void clearPresences()
        {
            userPresences.Clear();
            friendPresences.Clear();
            localUserPresence = default;
        }

        protected override Task BeginWatchingUserPresenceInternal()
        {
            loadSnapshot();
            return Task.CompletedTask;
        }

        protected override Task EndWatchingUserPresenceInternal() => Task.CompletedTask;

        public override Task UpdateActivity(UserActivity? activity)
        {
            UserActivity? stableActivity = activity is UserActivity.ChoosingBeatmap ? null : activity;
            localUserPresence = localUserPresence with { Activity = stableActivity, Status = localUserPresence.Status ?? UserStatus.Online };
            bancho.UpdateLegacyStatus(stableActivity!, localUserPresence.Status);
            return Task.CompletedTask;
        }

        public override Task UpdateStatus(UserStatus? status)
        {
            localUserPresence = localUserPresence with { Status = status };
            bancho.UpdateLegacyStatus(localUserPresence.Activity!, status);
            return Task.CompletedTask;
        }

        public override Task UserPresenceUpdated(int userId, UserPresence? presence)
        {
            if (presence.HasValue)
                setPresence(userId, presence.Value);
            else
                removePresence(userId);
            return Task.CompletedTask;
        }

        public override Task FriendPresenceUpdated(int userId, UserPresence? presence)
        {
            if (presence.HasValue)
                friendPresences[userId] = presence.Value;
            else
                friendPresences.Remove(userId);
            return Task.CompletedTask;
        }

        public override Task<BeatmapUpdates> GetChangesSince(int queueId) =>
            Task.FromResult(new BeatmapUpdates(Array.Empty<int>(), queueId));

        public override Task BeatmapSetsUpdated(BeatmapUpdates updates) => Task.CompletedTask;

        public override Task DailyChallengeUpdated(DailyChallengeInfo? info)
        {
            dailyChallengeInfo.Value = info;
            return Task.CompletedTask;
        }

        public override Task<MultiplayerPlaylistItemStats[]> BeginWatchingMultiplayerRoom(long id) =>
            Task.FromResult(Array.Empty<MultiplayerPlaylistItemStats>());

        public override Task EndWatchingMultiplayerRoom(long id) => Task.CompletedTask;
        public override Task RefreshFriends() => Task.CompletedTask;
        public override Task Reconnect() => Task.CompletedTask;

        protected override Task DisconnectInternal()
        {
            clearPresences();
            return Task.CompletedTask;
        }

        protected override void Dispose(bool isDisposing)
        {
            bancho.BanchoPresenceChanged -= onPresenceChanged;
            base.Dispose(isDisposing);
        }
    }
}
